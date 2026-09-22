using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.ExtensionsAI;

/// <summary>An explicit routing policy; no fallback is enabled by default.</summary>
public enum JevRouteFailurePolicy
{
    /// <summary>Throw without invoking any chat client.</summary>
    Throw,
    /// <summary>Invoke the explicitly named fallback route once.</summary>
    UseFallback
}

/// <summary>Immutable configuration for decision-based routing.</summary>
public sealed class JevRoutingOptions
{
    /// <summary>Gets the caller-defined Choice question, including exact route labels and descriptions.</summary>
    public required JevChoiceQuestion Question { get; init; }

    /// <summary>Gets the explicit model used for decision calls, independent of <see cref="ChatOptions.ModelId"/>.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the question's result key.</summary>
    public JevQuestionKey<JevChoiceAnswer> QuestionKey { get; init; } = new("route");

    /// <summary>Gets the minimum accepted confidence in [0,1], or null to apply no confidence threshold.</summary>
    public double? MinimumConfidence { get; init; }

    /// <summary>Gets the policy for a response label absent from the question or registered routes.</summary>
    public JevRouteFailurePolicy UnknownRoutePolicy { get; init; } = JevRouteFailurePolicy.Throw;

    /// <summary>Gets the policy for confidence below <see cref="MinimumConfidence"/>.</summary>
    public JevRouteFailurePolicy LowConfidencePolicy { get; init; } = JevRouteFailurePolicy.Throw;

    /// <summary>Gets the registered fallback label. Required when either policy enables fallback.</summary>
    public string? FallbackRoute { get; init; }

    /// <summary>Gets the explicit input projection. The default rejects all nontext content.</summary>
    public Func<IReadOnlyList<ChatMessage>, string> TextSelector { get; init; } = JevChatText.Extract;

    /// <summary>Gets whether disposal owns registered chat clients; false borrows them. The decision client is always borrowed.</summary>
    public bool OwnsChatClients { get; init; }
}

/// <summary>The reason a router did not invoke a chat provider.</summary>
public enum JevRoutingFailureReason
{
    /// <summary>Decision-state selection, request creation or evaluation failed.</summary>
    DecisionFailed,
    /// <summary>The response omitted the configured answer or returned an incompatible type.</summary>
    InvalidAnswer,
    /// <summary>The selected label is not configured or registered.</summary>
    UnknownRoute,
    /// <summary>The caller's confidence threshold was not met.</summary>
    LowConfidence
}

/// <summary>A routing failure. No automatic retry or chat-provider fallback follows this exception.</summary>
public sealed class JevRoutingException : Exception
{
    /// <summary>Creates a routing error without including state or provider response bodies in its message.</summary>
    public JevRoutingException(JevRoutingFailureReason reason, Exception? innerException = null)
        : base($"Jev chat routing failed ({reason}).", innerException) => Reason = reason;

    /// <summary>Gets the machine-readable failure reason.</summary>
    public JevRoutingFailureReason Reason { get; }
}
