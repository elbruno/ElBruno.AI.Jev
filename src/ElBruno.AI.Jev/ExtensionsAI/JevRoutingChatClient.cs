using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.ExtensionsAI;

/// <summary>Routes once with a Jev Choice, then invokes one real chat client without retries.</summary>
/// <remarks>
/// Uses the stable <see cref="IChatClient"/> contract rather than the experimental RoutingChatClient.
/// Registered routes and request collections are snapshotted. Opaque content and option values remain borrowed.
/// Do not mutate those values during a call. Decision usage is never combined with chat usage.
/// </remarks>
public sealed class JevRoutingChatClient : IChatClient
{
    private readonly IJevDecisionClient _decisionClient;
    private readonly IReadOnlyDictionary<string, IChatClient> _routes;
    private readonly JevRoutingOptions _options;
    private int _disposed;

    /// <summary>Creates a router without making any requests. Route names use ordinal, case-sensitive comparison.</summary>
    public JevRoutingChatClient(
        IJevDecisionClient decisionClient,
        IReadOnlyDictionary<string, IChatClient> routes,
        JevRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(decisionClient);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Question);
        ArgumentNullException.ThrowIfNull(options.QuestionKey);
        ArgumentNullException.ThrowIfNull(options.TextSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        if (routes.Count == 0) throw new ArgumentException("At least one chat route is required.", nameof(routes));
        if (options.MinimumConfidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum confidence must be finite and within [0,1].");
        }

        if (!Enum.IsDefined(options.UnknownRoutePolicy) || !Enum.IsDefined(options.LowConfidencePolicy))
        {
            throw new ArgumentException("Routing policies must be defined values.", nameof(options));
        }

        var copy = new Dictionary<string, IChatClient>(StringComparer.Ordinal);
        foreach ((string label, IChatClient client) in routes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label);
            ArgumentNullException.ThrowIfNull(client);
            copy.Add(label, client);
        }

        if ((options.UnknownRoutePolicy == JevRouteFailurePolicy.UseFallback ||
             options.LowConfidencePolicy == JevRouteFailurePolicy.UseFallback) &&
            (options.FallbackRoute is null || !copy.ContainsKey(options.FallbackRoute)))
        {
            throw new ArgumentException("An enabled fallback policy requires a registered fallback route.", nameof(options));
        }

        _decisionClient = decisionClient;
        _routes = copy;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ChatMessage> snapshot = JevChatSnapshots.Messages(messages);
        ChatOptions? chatOptions = options?.Clone();
        (IChatClient client, JevChatDecisionMetadata metadata) = await SelectAsync(snapshot, cancellationToken).ConfigureAwait(false);
        ChatResponse response = JevChatSnapshots.Response(
            await client.GetResponseAsync(snapshot, chatOptions, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        response.AdditionalProperties = JevChatMetadata.Append(response.AdditionalProperties, metadata);
        return response;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return StreamAsync(JevChatSnapshots.Messages(messages), options?.Clone(), cancellationToken);
    }

    /// <summary>
    /// Gets this router or its decision client for an unkeyed request.
    /// A string service key equal to a route label resolves that route's services; no route is selected by service discovery.
    /// </summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is string label && _routes.TryGetValue(label, out IChatClient? route))
        {
            return serviceType == typeof(IChatClient) ? route : route.GetService(serviceType);
        }

        if (serviceKey is not null) return null;
        if (serviceType.IsInstanceOfType(this)) return this;
        return serviceType == typeof(IJevDecisionClient) ? _decisionClient : null;
    }

    /// <summary>Disposes each distinct registered client once only when ownership was explicitly enabled.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_options.OwnsChatClients) return;
        List<Exception>? errors = null;
        foreach (IChatClient client in _routes.Values.Distinct<IChatClient>(ReferenceEqualityComparer.Instance))
        {
            try { client.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }

        if (errors is not null) throw new AggregateException("One or more owned chat clients failed to dispose.", errors);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        (IChatClient client, JevChatDecisionMetadata metadata) = await SelectAsync(messages, cancellationToken).ConfigureAwait(false);
        bool attached = false;
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options, cancellationToken)
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
        if (!attached)
        {
            yield return new ChatResponseUpdate { AdditionalProperties = JevChatMetadata.Append(null, metadata) };
        }
    }

    private async Task<(IChatClient Client, JevChatDecisionMetadata Metadata)> SelectAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JevDecisionResponse decision;
        try
        {
            string state = _options.TextSelector(JevChatSnapshots.Messages(messages));
            var request = new JevDecisionRequest(state, _options.Model).WithQuestion(_options.QuestionKey, _options.Question);
            cancellationToken.ThrowIfCancellationRequested();
            decision = await _decisionClient.EvaluateAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The routing decision client returned null.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new JevRoutingException(JevRoutingFailureReason.DecisionFailed, error); }

        JevChoiceAnswer answer;
        try { answer = decision.GetAnswer(_options.QuestionKey); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            throw new JevRoutingException(JevRoutingFailureReason.InvalidAnswer, error);
        }

        string route = answer.Choice;
        bool fallback = false;
        if (!_options.Question.Criteria.ContainsKey(route) || !_routes.ContainsKey(route))
        {
            route = ResolveFailure(_options.UnknownRoutePolicy, JevRoutingFailureReason.UnknownRoute);
            fallback = true;
        }
        else if (_options.MinimumConfidence is { } minimum && answer.Confidence < minimum)
        {
            route = ResolveFailure(_options.LowConfidencePolicy, JevRoutingFailureReason.LowConfidence);
            fallback = true;
        }

        return (_routes[route], new JevChatDecisionMetadata(JevChatDecisionStage.Routing, decision, route, fallback));
    }

    private string ResolveFailure(JevRouteFailurePolicy policy, JevRoutingFailureReason reason) =>
        policy == JevRouteFailurePolicy.UseFallback ? _options.FallbackRoute! : throw new JevRoutingException(reason);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
