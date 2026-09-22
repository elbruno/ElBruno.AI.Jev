using System.Collections.ObjectModel;
using System.Text.Json;

namespace ElBruno.AI.Jev;

/// <summary>An immutable evaluation of independent questions against one shared state.</summary>
public sealed class JevDecisionRequest
{
    /// <summary>Creates a textual-state request. Add questions with <see cref="WithQuestion{TAnswer}"/>.</summary>
    public JevDecisionRequest(string state, string? model = null)
        : this(JevJson.Text(state), new Dictionary<string, JevQuestion>(), model) { }

    /// <summary>Creates a structured-state request. Add questions with <see cref="WithQuestion{TAnswer}"/>.</summary>
    public JevDecisionRequest(JsonElement state, string? model = null)
        : this(state, new Dictionary<string, JevQuestion>(), model) { }

    /// <summary>Creates a request with a snapshot of the supplied questions and state.</summary>
    public JevDecisionRequest(JsonElement state, IReadOnlyDictionary<string, JevQuestion> questions, string? model = null)
    {
        if (state.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new ArgumentException("State must be a JSON string, object, or array.", nameof(state));
        }

        ArgumentNullException.ThrowIfNull(questions);
        if (model is not null) ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var copy = new Dictionary<string, JevQuestion>(StringComparer.Ordinal);
        foreach ((string key, JevQuestion question) in questions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(question);
            copy.Add(key, question);
        }

        State = JevJson.Own(state);
        Model = model;
        Questions = new ReadOnlyDictionary<string, JevQuestion>(copy);
    }

    /// <summary>Gets the independently owned state.</summary>
    public JsonElement State { get; }

    /// <summary>Gets questions indexed by case-sensitive correlation identifiers.</summary>
    public IReadOnlyDictionary<string, JevQuestion> Questions { get; }

    /// <summary>Gets the per-call model, or null to use the client's documented default.</summary>
    public string? Model { get; }

    /// <summary>Returns a new request with an additional typed question. Duplicate identifiers are rejected.</summary>
    public JevDecisionRequest WithQuestion<TAnswer>(JevQuestionKey<TAnswer> key, JevQuestion<TAnswer> question)
        where TAnswer : JevAnswer
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(question);
        var copy = new Dictionary<string, JevQuestion>(Questions, StringComparer.Ordinal);
        if (!copy.TryAdd(key.Name, question))
        {
            throw new ArgumentException("A question with this identifier already exists.", nameof(key));
        }

        return new JevDecisionRequest(State, copy, Model);
    }
}
