using System.Collections.ObjectModel;
using System.Text.Json;

namespace ElBruno.AI.Jev;

/// <summary>A named result handle whose type identifies the expected answer.</summary>
public sealed record JevQuestionKey<TAnswer> where TAnswer : JevAnswer
{
    /// <summary>Creates a correlation key. Question names are not sent to the underlying model.</summary>
    public JevQuestionKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the case-sensitive question identifier.</summary>
    public string Name { get; }
}

/// <summary>A native Jev question. Its JSON values are owned by the question.</summary>
public abstract class JevQuestion
{
    private protected JevQuestion(JsonElement? instructions)
    {
        Instructions = instructions is { } value ? JevJson.Own(value) : null;
    }

    /// <summary>Gets structured instructions, or null when omitted.</summary>
    public JsonElement? Instructions { get; }

    /// <summary>Gets the native wire discriminator.</summary>
    public abstract string Type { get; }

    internal abstract JsonElement? SerializeCriteria();
}

/// <summary>A question associated with a concrete answer type.</summary>
public abstract class JevQuestion<TAnswer> : JevQuestion where TAnswer : JevAnswer
{
    private protected JevQuestion(JsonElement? instructions) : base(instructions) { }
}

/// <summary>Chooses one caller-defined label and reports a probability distribution.</summary>
public sealed class JevChoiceQuestion : JevQuestion<JevChoiceAnswer>
{
    /// <summary>Creates a question with textual descriptions. Null descriptions are preserved.</summary>
    public JevChoiceQuestion(string? instructions, IReadOnlyDictionary<string, string?> criteria)
        : this(instructions is null ? null : JevJson.Text(instructions), ToStructured(criteria)) { }

    /// <summary>Creates a question with structured instructions and descriptions.</summary>
    public JevChoiceQuestion(JsonElement? instructions, IReadOnlyDictionary<string, JsonElement> criteria)
        : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(criteria), "Choice requires 1-255 options.");
        }

        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string label, JsonElement value) in criteria)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label);
            copy.Add(label, JevJson.Own(value));
        }

        Criteria = new ReadOnlyDictionary<string, JsonElement>(copy);
    }

    /// <inheritdoc />
    public override string Type => "choice";

    /// <summary>Gets exact option labels and their structured descriptions.</summary>
    public IReadOnlyDictionary<string, JsonElement> Criteria { get; }

    internal override JsonElement? SerializeCriteria() =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>(Criteria, StringComparer.Ordinal),
            JevJsonContext.Default.DictionaryStringJsonElement);

    private static Dictionary<string, JsonElement> ToStructured(IReadOnlyDictionary<string, string?> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return criteria.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is null ? JevJson.Parse("null") : JevJson.Text(pair.Value),
            StringComparer.Ordinal);
    }
}

/// <summary>Scores against 2-10 ordered, self-contained rubric levels.</summary>
public sealed class JevScoreQuestion : JevQuestion<JevScoreAnswer>
{
    /// <summary>Creates a question with textual rubric levels in their intended order.</summary>
    public JevScoreQuestion(string? instructions, IReadOnlyList<string> criteria)
        : this(instructions is null ? null : JevJson.Text(instructions), ToStructured(criteria)) { }

    /// <summary>Creates a question with structured rubric levels in their intended order.</summary>
    public JevScoreQuestion(JsonElement? instructions, IReadOnlyList<JsonElement> criteria) : base(instructions)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count is < 2 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(criteria), "Score requires 2-10 ordered levels.");
        }

        Criteria = Array.AsReadOnly(criteria.Select(JevJson.Own).ToArray());
    }

    /// <inheritdoc />
    public override string Type => "score";

    /// <summary>Gets the ordered rubric. Level indices are zero-based.</summary>
    public IReadOnlyList<JsonElement> Criteria { get; }

    internal override JsonElement? SerializeCriteria() =>
        JsonSerializer.SerializeToElement(Criteria.ToArray(), JevJsonContext.Default.JsonElementArray);

    private static JsonElement[] ToStructured(IReadOnlyList<string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return criteria.Select(JevJson.Text).ToArray();
    }
}

/// <summary>Assesses the probability of a proposition, without applying a boolean threshold.</summary>
public sealed class JevNoulQuestion : JevQuestion<JevNoulAnswer>
{
    /// <summary>Creates a proposition with optional textual true/false outcome descriptions.</summary>
    public JevNoulQuestion(string? instructions, string? trueCriteria = null, string? falseCriteria = null)
        : this(
            instructions is null ? null : JevJson.Text(instructions),
            trueCriteria is null ? null : JevJson.Text(trueCriteria),
            falseCriteria is null ? null : JevJson.Text(falseCriteria))
    { }

    /// <summary>Creates a proposition with structured instructions and optional structured outcomes.</summary>
    public JevNoulQuestion(JsonElement? instructions, JsonElement? trueCriteria, JsonElement? falseCriteria)
        : base(instructions)
    {
        TrueCriteria = trueCriteria is { } yes ? JevJson.Own(yes) : null;
        FalseCriteria = falseCriteria is { } no ? JevJson.Own(no) : null;
    }

    /// <inheritdoc />
    public override string Type => "noul";

    /// <summary>Gets the optional true outcome description.</summary>
    public JsonElement? TrueCriteria { get; }

    /// <summary>Gets the optional false outcome description.</summary>
    public JsonElement? FalseCriteria { get; }

    internal override JsonElement? SerializeCriteria()
    {
        if (TrueCriteria is null && FalseCriteria is null) return null;
        var criteria = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (TrueCriteria is { } yes) criteria.Add("true", yes);
        if (FalseCriteria is { } no) criteria.Add("false", no);
        return JsonSerializer.SerializeToElement(criteria, JevJsonContext.Default.DictionaryStringJsonElement);
    }
}
