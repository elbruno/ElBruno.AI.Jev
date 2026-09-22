using System.Collections.ObjectModel;
using System.Text.Json;

namespace ElBruno.AI.Jev;

/// <summary>A native Jev answer. Raw data may contain sensitive application content.</summary>
public abstract class JevAnswer
{
    private protected JevAnswer(JsonElement? rawRepresentation) =>
        RawRepresentation = rawRepresentation is { } value ? JevJson.Own(value) : null;

    /// <summary>Gets the unmodified native JSON, including unknown fields, when available.</summary>
    public JsonElement? RawRepresentation { get; }

    internal static double ValidateProbability(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A probability must be finite and within [0,1].");
        }

        return value;
    }

    internal static IReadOnlyDictionary<string, double> Distribution(IReadOnlyDictionary<string, double> probabilities)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        if (probabilities.Count == 0) throw new ArgumentException("A distribution must not be empty.", nameof(probabilities));
        return new ReadOnlyDictionary<string, double>(probabilities.ToDictionary(
            pair => pair.Key, pair => ValidateProbability(pair.Value, nameof(probabilities)), StringComparer.Ordinal));
    }
}

/// <summary>A chosen label, its full distribution, and server-provided confidence.</summary>
public sealed class JevChoiceAnswer : JevAnswer
{
    /// <summary>Creates a choice answer. Labels and probabilities are not normalized or renamed.</summary>
    public JevChoiceAnswer(string choice, IReadOnlyDictionary<string, double> probabilities, double confidence, JsonElement? rawRepresentation = null)
        : base(rawRepresentation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choice);
        Probabilities = Distribution(probabilities);
        if (!Probabilities.ContainsKey(choice)) throw new ArgumentException("The selected label must be in the distribution.", nameof(choice));
        Choice = choice;
        Confidence = ValidateProbability(confidence, nameof(confidence));
    }

    /// <summary>Gets the exact selected label.</summary>
    public string Choice { get; }

    /// <summary>Gets the full probability distribution.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; }

    /// <summary>Gets server confidence, not a guarantee of semantic correctness.</summary>
    public double Confidence { get; }
}

/// <summary>A fractional expected rubric position, distribution, legend, and confidence.</summary>
public sealed class JevScoreAnswer : JevAnswer
{
    /// <summary>Creates a score answer without rounding or converting it to a percentage.</summary>
    public JevScoreAnswer(double score, IReadOnlyDictionary<string, double> probabilities, IReadOnlyDictionary<string, JsonElement> legend, double confidence, JsonElement? rawRepresentation = null)
        : base(rawRepresentation)
    {
        ArgumentNullException.ThrowIfNull(legend);
        Probabilities = Distribution(probabilities);
        if (!double.IsFinite(score) || score < 0 || score > probabilities.Count - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }

        Score = score;
        Legend = new ReadOnlyDictionary<string, JsonElement>(
            legend.ToDictionary(pair => pair.Key, pair => JevJson.Own(pair.Value), StringComparer.Ordinal));
        Confidence = ValidateProbability(confidence, nameof(confidence));
    }

    /// <summary>Gets the expected zero-based position in the supplied rubric.</summary>
    public double Score { get; }

    /// <summary>Gets probabilities keyed by zero-based string indices.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; }

    /// <summary>Gets the structured rubric legend, retaining native string index keys.</summary>
    public IReadOnlyDictionary<string, JsonElement> Legend { get; }

    /// <summary>Gets server-provided confidence.</summary>
    public double Confidence { get; }
}

/// <summary>A proposition probability. It intentionally has no fabricated confidence or boolean property.</summary>
public sealed class JevNoulAnswer : JevAnswer
{
    /// <summary>Creates a probability answer in [0,1].</summary>
    public JevNoulAnswer(double probability, JsonElement? rawRepresentation = null) : base(rawRepresentation) =>
        Probability = ValidateProbability(probability, nameof(probability));

    /// <summary>Gets the probability of the proposition being true.</summary>
    public double Probability { get; }
}
