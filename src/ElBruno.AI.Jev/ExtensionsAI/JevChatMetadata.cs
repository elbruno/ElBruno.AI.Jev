using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.ExtensionsAI;

/// <summary>The stage responsible for a Jev decision, separate from generative chat.</summary>
public enum JevChatDecisionStage
{
    /// <summary>A Choice selected a chat provider.</summary>
    Routing,
    /// <summary>Input was assessed before calling a chat provider.</summary>
    InputAssessment,
    /// <summary>Complete output was assessed before releasing it.</summary>
    OutputAssessment
}

/// <summary>Attribution for one decision call. This data is not added to the chat provider's usage.</summary>
/// <param name="Stage">The integration stage.</param>
/// <param name="Decision">The complete native result, including distributions, model, usage and request ID.</param>
/// <param name="SelectedRoute">The registered route actually used, if routing.</param>
/// <param name="UsedFallback">Whether an explicitly configured fallback policy selected the route.</param>
public sealed record JevChatDecisionMetadata(
    JevChatDecisionStage Stage,
    JevDecisionResponse Decision,
    string? SelectedRoute = null,
    bool UsedFallback = false)
{
    /// <summary>Gets decision-only usage in Microsoft's standard representation; unknown counts stay null.</summary>
    public UsageDetails Usage => new()
    {
        InputTokenCount = Decision.Usage.InputTokens,
        OutputTokenCount = Decision.Usage.OutputTokens
    };
}

/// <summary>Accesses the separately attributable decisions attached by Jev chat middleware.</summary>
public static class JevChatMetadata
{
    /// <summary>The reserved additional-properties key. Its value is a read-only decision metadata list.</summary>
    public const string PropertyName = "ElBruno.AI.Jev.Decisions";

    /// <summary>Gets decisions from response or update properties, or an empty list when absent.</summary>
    /// <exception cref="InvalidOperationException">An unrelated value occupies the reserved key.</exception>
    public static IReadOnlyList<JevChatDecisionMetadata> GetDecisions(AdditionalPropertiesDictionary? properties)
    {
        if (properties?.TryGetValue(PropertyName, out object? value) != true)
        {
            return Array.Empty<JevChatDecisionMetadata>();
        }

        return value as IReadOnlyList<JevChatDecisionMetadata>
            ?? throw new InvalidOperationException("The reserved Jev decision metadata key contains an incompatible value.");
    }

    internal static AdditionalPropertiesDictionary Append(
        AdditionalPropertiesDictionary? properties, JevChatDecisionMetadata metadata)
    {
        var copy = properties?.Clone() ?? new();
        copy[PropertyName] = Array.AsReadOnly(GetDecisions(properties).Append(metadata).ToArray());
        return copy;
    }
}
