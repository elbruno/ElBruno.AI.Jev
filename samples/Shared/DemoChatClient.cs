using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Jev.Samples;

internal sealed class DemoChatClient(string name) : IChatClient
{
    private bool _disposed;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"DEMO chat ({name}): this is a deterministic reply, not model inference."))
        {
            ModelId = $"demo-{name}",
            ResponseId = "demo-response",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, $"DEMO chat ({name}): ") { ModelId = $"demo-{name}", ResponseId = "demo-stream" };
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "this is a deterministic reply, not model inference.")
        {
            ModelId = $"demo-{name}",
            ResponseId = "demo-stream",
            FinishReason = ChatFinishReason.Stop
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(ChatClientMetadata)) return new ChatClientMetadata("sample-demo", defaultModelId: $"demo-{name}");
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() => _disposed = true;
}
