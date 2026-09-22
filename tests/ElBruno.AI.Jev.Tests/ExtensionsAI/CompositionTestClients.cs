using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.Tests.ExtensionsAI;

internal sealed class DecisionClientStub : IJevDecisionClient
{
    internal Func<JevDecisionRequest, CancellationToken, Task<JevDecisionResponse>> Evaluate { get; set; } =
        (_, _) => Task.FromResult(DecisionFixtures.Choice());
    internal int Calls { get; private set; }
    internal JevDecisionRequest? Request { get; private set; }
    internal CancellationToken Token { get; private set; }

    public Task<JevDecisionResponse> EvaluateAsync(JevDecisionRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        Request = request;
        Token = cancellationToken;
        return Evaluate(request, cancellationToken);
    }

    public Task<JevModelList> ListModelsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class ChatClientStub : IChatClient
{
    internal Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> Respond { get; set; } =
        (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
    internal Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Stream { get; set; } =
        (_, _, token) => Updates([new(ChatRole.Assistant, "answer")], token);
    internal int ResponseCalls { get; private set; }
    internal int StreamCalls { get; private set; }
    internal int Disposals { get; private set; }
    internal Exception? DisposalError { get; set; }
    internal ChatClientMetadata Metadata { get; } = new("real-provider", new Uri("https://example.invalid"), "chat-model");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ResponseCalls++;
        return Respond(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        StreamCalls++;
        return Stream(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null : serviceType == typeof(ChatClientMetadata) ? Metadata
            : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        Disposals++;
        if (DisposalError is { } error) throw error;
    }

    internal static async IAsyncEnumerable<ChatResponseUpdate> Updates(
        IEnumerable<ChatResponseUpdate> updates, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (ChatResponseUpdate update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }
}

internal static class DecisionFixtures
{
    internal static readonly JevQuestionKey<JevChoiceAnswer> RouteKey = new("route");
    internal static readonly JevQuestionKey<JevNoulAnswer> SafeKey = new("allowed");
    internal static ChatMessage[] Messages => [new(ChatRole.User, "question")];
    internal static JevChoiceQuestion RouteQuestion => new("Choose a route.",
        new Dictionary<string, string?> { ["Fast"] = "Simple", ["Deep"] = "Complex" });
    internal static JevDecisionResponse Choice(string label = "Fast", double confidence = .95) =>
        new("jev-resolved", new Dictionary<string, JevAnswer>
        {
            ["route"] = new JevChoiceAnswer(label, new Dictionary<string, double> { [label] = 1 }, confidence)
        }, new JevUsage(17, 3), "decision-request");
    internal static JevDecisionResponse Assessment(double probability = .9) =>
        new("jev-resolved", new Dictionary<string, JevAnswer> { ["allowed"] = new JevNoulAnswer(probability) },
            new JevUsage(11, 2), "assessment-request");
    internal static JevDecisionRequest AssessmentRequest(string text) =>
        new JevDecisionRequest(text, "jev-pinned").WithQuestion(SafeKey, new JevNoulQuestion("Is this allowed by the application policy?"));
    internal static async Task<List<ChatResponseUpdate>> Collect(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var result = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in updates) result.Add(update);
        return result;
    }
}
