# Microsoft.Extensions.AI composition

`ElBruno.AI.Jev.ExtensionsAI` composes official Jev decisions with **real, separately configured chat providers**. A Jev credential does not enable generative chat, embeddings, images, audio, native tool calls, or service-side streaming.

The package uses the stable `IChatClient`, `DelegatingChatClient`, `AIFunction`, `ChatClientBuilder`, and stream aggregation contracts from Microsoft.Extensions.AI 10.10.0. Microsoft's `RoutingChatClient` is experimental in that version; this package instead implements its routing wrapper against stable `IChatClient`, without experimental-warning suppression.

## Explicit decision tools

The application owns the model, questions, instructions, and tool name. Only textual `state` is exposed as a tool argument; models and policies cannot be overridden through extra arguments.

```csharp
using ElBruno.AI.Jev;
using ElBruno.AI.Jev.ExtensionsAI;
using Microsoft.Extensions.AI;

var priority = new JevQuestionKey<JevChoiceAnswer>("priority");
var question = new JevChoiceQuestion("Choose the support priority.",
    new Dictionary<string, string?>
    {
        ["normal"] = "Routine assistance",
        ["urgent"] = "An outage affecting active work"
    });

// decisions is an IJevDecisionClient; realChat is an independently configured IChatClient.
var tool = new JevDecisionFunction(
    decisions, "assess_priority", "Assess a support ticket's priority.",
    state => new JevDecisionRequest(state, JevModels.Version1_13_0).WithQuestion(priority, question));

using IChatClient chat = realChat.AsBuilder().UseFunctionInvocation().Build();
ChatResponse response = await chat.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Assess this ticket: production is unavailable.")],
    new ChatOptions { Tools = [tool] },
    cancellationToken);
```

Use your chosen model ID if your application does not use the convenience constant. Tool names contain 1–64 ASCII letters, digits, underscores, or hyphens. The tool exposes explicit `JsonSchema` and `ReturnJsonSchema`. Invocations accept a .NET string or JSON string under `state`, reject missing/extra/nonstring arguments, and propagate cancellation.

For direct invocation, use:

```csharp
object? result = await tool.InvokeAsync(
    new AIFunctionArguments { ["state"] = "A production outage" }, cancellationToken);
// result is an owned JsonElement, directly serializable by System.Text.Json.
```

The JSON result contains:

- `model`, nullable `requestId`, and `usage.inputTokens` / `usage.outputTokens` (null means unknown).
- `answers`, keyed by the original question IDs.
- Choice: `type`, exact `choice`, full `probabilities`, and server `confidence`.
- Score: `type`, fractional `score`, full `probabilities`, structured `legend`, and server `confidence`.
- Noul: `type` and `probability`, **not** an invented boolean or confidence.
- `rawRepresentation` at response/answer level when supplied by the native client, preserving unknown fields.

These results may contain sensitive native data. Do not automatically log them or expose them to a chat provider unless your application intends to. `UseFunctionInvocation` executes application tools; Jev itself does not generate tool-call messages.

## Choice-based chat routing

```csharp
var routeQuestion = new JevChoiceQuestion("Choose the appropriate assistant.",
    new Dictionary<string, string?>
    {
        ["Fast"] = "Routine, short questions",
        ["Deep"] = "Complex reasoning requiring the specialist"
    });

using var router = new JevRoutingChatClient(
    decisions,
    new Dictionary<string, IChatClient>
    {
        ["Fast"] = fastChat,
        ["Deep"] = specialistChat
    },
    new JevRoutingOptions
    {
        Question = routeQuestion,
        Model = JevModels.Version1_13_0,
        MinimumConfidence = 0.8,
        // Defaults: unknown or low-confidence routes throw without invoking chat.
        UnknownRoutePolicy = JevRouteFailurePolicy.Throw,
        LowConfidencePolicy = JevRouteFailurePolicy.Throw
    });

ChatResponse answer = await router.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Explain this problem.")],
    new ChatOptions { MaxOutputTokens = 400 },
    cancellationToken);
```

Thresholds are examples, not calibrated guarantees. A null `MinimumConfidence` applies no threshold. Equality meets the threshold. Labels are ordinal and case-sensitive. `QuestionKey` defaults to `route`.

To deliberately enable a fallback, set the corresponding policy to `UseFallback` and set `FallbackRoute` to a registered label. No fallback is inferred. If the chosen label is absent from either the configured question or registered routes, the unknown-route policy applies first. Otherwise the confidence policy applies. A fallback is selected once; it does not trigger another decision call.

Missing or incorrectly typed answers raise `JevRoutingException` with `InvalidAnswer`. Decision/projection errors use `DecisionFailed`; other reasons are `UnknownRoute` and `LowConfidence`. **Decision failures never trigger fallback**, even if the unknown-route policy allows one. Caller cancellation remains an `OperationCanceledException`. Chat-provider failures propagate unchanged; there is no retry, second provider, or replay after partially delivered output.

Streaming performs one nonstreaming Jev decision and forwards the selected provider's stream once. It honors enumeration cancellation and disposes the inner enumerator on early exit. Jev metadata is attached to the first update only; an empty stream produces a metadata-only update.

## Caller-owned input and output assessments

```csharp
var allowed = new JevQuestionKey<JevNoulAnswer>("allowed");
var assessmentOptions = new JevAssessmentOptions
{
    CreateRequest = text => new JevDecisionRequest(text, JevModels.Version1_13_0)
        .WithQuestion(allowed, new JevNoulQuestion(
            "Does this text satisfy our explicitly defined application policy?")),
    Allow = result => result.GetAnswer(allowed).Probability >= 0.9,
    Mode = JevAssessmentMode.BufferedOutputReview,
    MaxBufferedUpdates = 4096,
    MaxBufferedCharacters = 1_048_576
};

using var reviewed = new JevAssessmentChatClient(realChat, decisions, assessmentOptions);
ChatResponse approved = await reviewed.GetResponseAsync(messages, options, cancellationToken);
```

`CreateRequest` can include Choice, Score, Noul, or multiple independent questions. `Allow` must implement the application's explicit acceptance criteria. Neither the SDK nor a Noul answer invents a universal policy threshold.

| Mode | Before generation | Before returning output |
| --- | --- | --- |
| `InputOnly` (default) | Assess the projected input; invoke chat only on approval | Forward output normally, without output assessment |
| `BufferedOutputReview` | Invoke the real chat provider; no input assessment | Buffer the entire output, assess it once, release only on approval |

To assess both stages, compose two independently configured wrappers. `BufferedOutputReview` also applies to `GetResponseAsync`. For `GetStreamingResponseAsync`, the wrapper consumes the **actual underlying stream**, aggregates a separate copy with Microsoft's `ToChatResponse()` for assessment, and then replays approved original update boundaries, contents, usage updates, and provider metadata. It never emits unchecked tokens, tool calls, or metadata before approval.

The stream is not interactive while output review is pending. Generation cost is already incurred even if assessment rejects the result. The configured maximum update count and text-character count fail closed if exceeded; these are **not token limits or complete memory quotas** for arbitrary nontext payloads. Nonstreaming review applies the text-character limit. Set appropriate provider output limits as well.

- `JevAssessmentRejectedException`: `Allow` returned false; exposes the stage and decision.
- `JevAssessmentFailedException`: projection, request creation, decision evaluation, acceptance predicate, or buffer limits failed. No content is approved.
- Caller cancellation remains `OperationCanceledException`, not a rejection or fail-open result.
- Inner chat-provider exceptions propagate unchanged. A partial buffered stream is discarded, not assessed or released.

These are **model-based assessments, not security guarantees**. Adversarial state can influence model decisions. Use independent application controls, explicit policies, and calibrated thresholds.

## Text selection and unsupported content

The default `JevChatText.Extract` includes every message's text with its role and rejects every non-`TextContent` item. It does not silently omit tool calls/results, images, audio, reasoning content, or other unsupported content. Role prefixes provide context, not trusted boundaries.

For tool-history conversations, deliberately choose what your policy assesses:

```csharp
TextSelector = history =>
    JevChatText.Extract(history.Where(message => message.Role == ChatRole.User).ToArray())
```

This example assesses user text only; it explicitly excludes tool history from the decision state. It still rejects nontext user content. A custom output selector similarly owns the meaning and completeness of its review: approving a projection is not a claim that excluded content was assessed. Such selectors must enforce their own opaque-payload limits.

Original messages/content are forwarded intact to the chat provider, regardless of the projection. Request enumerables are materialized once; message containers, their content lists, text content, additional-property dictionaries, and `ChatOptions` collections are snapshotted before asynchronous work. Streaming inputs are snapshotted when `GetStreamingResponseAsync` is called. Opaque tool/content payloads, raw representations, annotations, and objects inside options remain borrowed; do not mutate them while a call is running. Custom selectors, factories, and predicates must be safe for concurrent use.

## Metadata, services, ownership, and pipeline ordering

```csharp
foreach (JevChatDecisionMetadata item in JevChatMetadata.GetDecisions(answer.AdditionalProperties))
{
    Console.WriteLine($"{item.Stage}: {item.Decision.Model}, route={item.SelectedRoute}");
    // item.Decision preserves native answers, usage, raw data and request ID.
    // item.Usage is decision-only UsageDetails; it never modifies answer.Usage.
}
```

The reserved key is `JevChatMetadata.PropertyName` (`ElBruno.AI.Jev.Decisions`). Nested wrappers append to a read-only metadata list rather than overwriting earlier decisions. Incompatible values under this reserved key are errors. Response model IDs, usage, finish reasons, messages, and raw provider metadata remain the **chat provider's**, not Jev's. No combined token total or fabricated chat-provider identity is reported.

The router resolves itself and `IJevDecisionClient` through unkeyed `GetService`. A string route label resolves a registered route's services, for example `router.GetService(typeof(ChatClientMetadata), "Fast")`. Unkeyed provider metadata is deliberately absent because no route has been selected. Assessment middleware delegates ordinary service discovery to its real inner client and exposes its decision client unkeyed.

All decision clients are borrowed. Registered/inner chat clients are borrowed by default. Set `JevRoutingOptions.OwnsChatClients` or `JevAssessmentOptions.OwnsInnerClient` to true only when transferring ownership. The router disposes a shared registered instance once, even under multiple labels. Disposing a wrapper makes subsequent calls invalid but does not implicitly cancel active calls.

Use Microsoft's pipeline utilities directly:

```csharp
using IChatClient pipeline = realChat.AsBuilder()
    .UseOpenTelemetry()
    .UseLogging()
    .Use(inner => new JevAssessmentChatClient(inner, decisions,
        new JevAssessmentOptions
        {
            CreateRequest = assessmentOptions.CreateRequest,
            Allow = assessmentOptions.Allow,
            OwnsInnerClient = true
        }))
    .UseDistributedCache(distributedCache)
    .Build();
```

Put caching **inside** routing/assessment boundaries (for routing, configure each registered chat pipeline separately). Then cached chat output still passes through the current assessment and every routed call gets a current decision. Caching an entire outer wrapper can bypass policy evaluation and persist sensitive decision metadata; typed values in `AdditionalProperties` are not a guaranteed durable cache serialization contract. Do not treat a cached approval as a fresh approval.

Place function invocation outside a raw provider's tool-call output review only when your explicit selector/policy supports tool calls. Otherwise put output review outside the full invocation pipeline to review its completed result, remembering that tools may already have run. No wrapper can undo side effects.

Offline tests exercise real function-invocation middleware, logging, OpenTelemetry, inner caching, route selection, failure/cancellation, metadata preservation, ownership, concurrency, and no-leak buffering. They do not establish real-provider tool/stream interoperability or assess model quality. Cancellation stops local work; it does not promise a remote stop or billing refund.
