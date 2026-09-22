using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.ExtensionsAI;

/// <summary>Assesses explicitly selected text around a real chat provider; this is not a security guarantee.</summary>
/// <remarks>
/// Output-review streams retain original update boundaries and metadata, then replay approved updates.
/// Buffers are bounded by update count and text characters, not arbitrary opaque payload sizes.
/// Applications supporting nontext content must supply a deliberate selector and their own payload limits.
/// Request collections and text are snapshotted; opaque content and option values remain borrowed.
/// </remarks>
public sealed class JevAssessmentChatClient : DelegatingChatClient
{
    private readonly IJevDecisionClient _decisionClient;
    private readonly JevAssessmentOptions _options;
    private int _disposed;

    /// <summary>Creates assessment middleware without making requests. Clients are borrowed by default.</summary>
    public JevAssessmentChatClient(IChatClient innerClient, IJevDecisionClient decisionClient, JevAssessmentOptions options)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(decisionClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.CreateRequest);
        ArgumentNullException.ThrowIfNull(options.Allow);
        ArgumentNullException.ThrowIfNull(options.TextSelector);
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentException("The assessment mode must be defined.", nameof(options));
        if (options.MaxBufferedUpdates <= 0 || options.MaxBufferedCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Buffer limits must be positive.");
        }

        _decisionClient = decisionClient;
        _options = options;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ChatMessage> snapshot = JevChatSnapshots.Messages(messages);
        ChatOptions? chatOptions = options?.Clone();
        JevChatDecisionMetadata? metadata = null;
        if (_options.Mode == JevAssessmentMode.InputOnly)
        {
            metadata = await AssessAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        ChatResponse response = JevChatSnapshots.Response(
            await InnerClient.GetResponseAsync(snapshot, chatOptions, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Mode == JevAssessmentMode.BufferedOutputReview)
        {
            CheckBufferLimits(0, response.Messages.Sum(message => (long)message.Text.Length));
            metadata = await AssessAsync(JevChatSnapshots.Messages(response.Messages), cancellationToken).ConfigureAwait(false);
        }

        response.AdditionalProperties = JevChatMetadata.Append(response.AdditionalProperties, metadata!);
        return response;
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return StreamAsync(JevChatSnapshots.Messages(messages), options?.Clone(), cancellationToken);
    }

    /// <summary>Resolves this wrapper, its unkeyed decision client, or services exposed by the real inner chat client.</summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType == typeof(IJevDecisionClient)) return _decisionClient;
        return base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && _options.OwnsInnerClient)
        {
            base.Dispose(disposing);
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_options.Mode == JevAssessmentMode.InputOnly)
        {
            JevChatDecisionMetadata metadata = await AssessAsync(messages, cancellationToken).ConfigureAwait(false);
            bool attached = false;
            await foreach (ChatResponseUpdate update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ChatResponseUpdate copy = JevChatSnapshots.Update(update);
                if (!attached)
                {
                    copy.AdditionalProperties = JevChatMetadata.Append(copy.AdditionalProperties, metadata);
                    attached = true;
                }

                yield return copy;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!attached) yield return MetadataUpdate(metadata);
            yield break;
        }

        var buffered = new List<ChatResponseUpdate>();
        long characters = 0;
        await foreach (ChatResponseUpdate update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            characters += update.Text.Length;
            CheckBufferLimits((long)buffered.Count + 1, characters);
            buffered.Add(JevChatSnapshots.Update(update));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Aggregate a separate snapshot for assessment; replay the original update boundaries, not reconstructed output.
        ChatResponse completed = buffered.Select(JevChatSnapshots.Update).ToChatResponse();
        JevChatDecisionMetadata approval = await AssessAsync(JevChatSnapshots.Messages(completed.Messages), cancellationToken).ConfigureAwait(false);
        if (buffered.Count == 0)
        {
            yield return MetadataUpdate(approval);
        }
        else
        {
            buffered[0].AdditionalProperties = JevChatMetadata.Append(buffered[0].AdditionalProperties, approval);
            foreach (ChatResponseUpdate update in buffered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
    }

    private async Task<JevChatDecisionMetadata> AssessAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        JevChatDecisionStage stage = _options.Mode == JevAssessmentMode.InputOnly
            ? JevChatDecisionStage.InputAssessment : JevChatDecisionStage.OutputAssessment;
        JevDecisionResponse decision;
        bool allowed;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string text = _options.TextSelector(JevChatSnapshots.Messages(messages));
            JevDecisionRequest request = _options.CreateRequest(text)
                ?? throw new InvalidOperationException("The assessment request factory returned null.");
            cancellationToken.ThrowIfCancellationRequested();
            decision = await _decisionClient.EvaluateAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The assessment client returned null.");
            cancellationToken.ThrowIfCancellationRequested();
            allowed = _options.Allow(decision);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new JevAssessmentFailedException(stage, error); }

        if (!allowed) throw new JevAssessmentRejectedException(stage, decision);
        return new JevChatDecisionMetadata(stage, decision);
    }

    private void CheckBufferLimits(long updates, long characters)
    {
        if (updates > _options.MaxBufferedUpdates || characters > _options.MaxBufferedCharacters)
        {
            throw new JevAssessmentFailedException(JevChatDecisionStage.OutputAssessment,
                new InvalidOperationException("The configured output-review buffer limit was exceeded."));
        }
    }

    private static ChatResponseUpdate MetadataUpdate(JevChatDecisionMetadata metadata) =>
        new() { AdditionalProperties = JevChatMetadata.Append(null, metadata) };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
