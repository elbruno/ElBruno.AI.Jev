# ElBruno.AI.Jev

A community .NET 10 client for the official TypeSafe AI Jev service.

> **Tentative early-access release (0.5.0).** Live service compatibility remains
> unverified, and live testing is deferred. Offline test and package validation
> success does not prove real-service behavior. Evaluate before production use;
> APIs may change before 1.0. NuGet classifies `0.5.0` as stable because it has
> no prerelease suffix, but this package is still explicitly tentative.

```powershell
dotnet add package ElBruno.AI.Jev --version 0.5.0
```

## Capabilities

- Choice classification with full probability distributions and confidence.
- Score evaluation against ordered rubrics, preserving fractional results and legends.
- Noul proposition probability without hidden boolean thresholds.
- Structured state and criteria, typed question handles, model discovery and pinning.
- Microsoft.Extensions.AI function tools, chat routing, and assessment composition.
- Async HTTP, cancellation, bounded responses, explicit errors and controlled retries.

Jev is not a generative chat or embedding service. This package does not invent image, speech, or realtime endpoints. It is not an official TypeSafe AI SDK and does not target the independent jevtypesafeai.com proxy.

## Quickstart

```csharp
using ElBruno.AI.Jev;

using var client = new JevClient(new JevClientOptions
{
    ApiKey = configuration["Jev:ApiKey"]
        ?? throw new InvalidOperationException("Configure Jev:ApiKey."),
    DefaultModel = JevModels.Version1_13_0
});
var question = new JevQuestionKey<JevNoulAnswer>("relevant");
var request = new JevDecisionRequest("A .NET developer asks about dependency injection.")
    .WithQuestion(question, new JevNoulQuestion("Is this about .NET development?"));
var response = await client.EvaluateAsync(request, cancellationToken);
Console.WriteLine(response.GetAnswer(question).Probability);
```

The consuming application supplies `configuration` and `cancellationToken`. Use .NET user-secrets for development or your application's production secret provider. Do not hardcode API keys.

Microsoft.Extensions.AI integrations wrap your own chat clients; a Jev credential does not enable another provider. Review configured confidence thresholds and model versions before making consequential decisions. Assessments are not security guarantees.

Official service documentation: https://docs.typesafe.ai/

The maintainer has authorized this tentative release before live verification.
See [the project repository](https://github.com/elbruno/ElBruno.AI.Jev) for runnable
samples, configuration, tests, known limitations, and release instructions.
