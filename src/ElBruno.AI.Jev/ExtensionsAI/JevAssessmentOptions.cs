using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.ExtensionsAI;

/// <summary>When assessment happens relative to the real generative provider.</summary>
public enum JevAssessmentMode
{
    /// <summary>Assess input before invoking the provider. Output is not assessed and streams normally.</summary>
    InputOnly,
    /// <summary>Assess complete output only. Streaming is fully buffered; nothing is released before approval.</summary>
    BufferedOutputReview
}

/// <summary>Caller-owned assessment questions, thresholds, projection and bounded buffering.</summary>
public sealed class JevAssessmentOptions
{
    /// <summary>Gets the factory for explicit questions and a decision model, given the selected text.</summary>
    public required Func<string, JevDecisionRequest> CreateRequest { get; init; }

    /// <summary>Gets the application policy. Return true to approve, false to reject. Exceptions fail closed.</summary>
    public required Func<JevDecisionResponse, bool> Allow { get; init; }

    /// <summary>Gets the assessment timing. Combine two wrappers if both input and output need separate assessments.</summary>
    public JevAssessmentMode Mode { get; init; } = JevAssessmentMode.InputOnly;

    /// <summary>Gets the projection of input or output messages. The default rejects all nontext content.</summary>
    public Func<IReadOnlyList<ChatMessage>, string> TextSelector { get; init; } = JevChatText.Extract;

    /// <summary>Gets the maximum number of stream updates retained before review; exceeding it fails closed.</summary>
    public int MaxBufferedUpdates { get; init; } = 4096;

    /// <summary>Gets the maximum text character count retained before output review, not a token or total memory limit.</summary>
    public int MaxBufferedCharacters { get; init; } = 1_048_576;

    /// <summary>Gets whether disposal owns the inner chat client. False borrows it; the decision client is always borrowed.</summary>
    public bool OwnsInnerClient { get; init; }
}

/// <summary>An application assessment policy explicitly rejected the selected input or output.</summary>
public sealed class JevAssessmentRejectedException : Exception
{
    /// <summary>Creates an explicit policy rejection.</summary>
    public JevAssessmentRejectedException(JevChatDecisionStage stage, JevDecisionResponse decision)
        : base($"Jev chat assessment rejected content ({stage}).")
    {
        ArgumentNullException.ThrowIfNull(decision);
        Stage = stage;
        Decision = decision;
    }

    /// <summary>Gets the rejected stage.</summary>
    public JevChatDecisionStage Stage { get; }

    /// <summary>Gets the decision for deliberate audit use. It can include sensitive native result data.</summary>
    public JevDecisionResponse Decision { get; }
}

/// <summary>An assessment could not complete. This is distinct from an explicit policy rejection and never fails open.</summary>
public sealed class JevAssessmentFailedException : Exception
{
    /// <summary>Creates an assessment failure without including content in the default message.</summary>
    public JevAssessmentFailedException(JevChatDecisionStage stage, Exception innerException)
        : base($"Jev chat assessment failed ({stage}); content was not approved.", innerException) => Stage = stage;

    /// <summary>Gets the failed stage.</summary>
    public JevChatDecisionStage Stage { get; }
}
