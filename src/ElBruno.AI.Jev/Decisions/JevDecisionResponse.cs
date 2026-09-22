using System.Collections.ObjectModel;
using System.Text.Json;

namespace ElBruno.AI.Jev;

/// <summary>Token usage. Missing values are unknown, not zero.</summary>
/// <param name="InputTokens">Reported input token count.</param>
/// <param name="OutputTokens">Reported output token count.</param>
public sealed record JevUsage(long? InputTokens = null, long? OutputTokens = null);

/// <summary>An evaluation's typed answers and native metadata.</summary>
public sealed class JevDecisionResponse
{
    /// <summary>Creates a response, including for deterministic integration test clients.</summary>
    public JevDecisionResponse(string model, IReadOnlyDictionary<string, JevAnswer> answers, JevUsage? usage = null, string? requestId = null, JsonElement? rawRepresentation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Values.Any(answer => answer is null)) throw new ArgumentException("Answers cannot contain null.", nameof(answers));
        if (usage?.InputTokens < 0 || usage?.OutputTokens < 0) throw new ArgumentOutOfRangeException(nameof(usage));
        Model = model;
        Answers = new ReadOnlyDictionary<string, JevAnswer>(new Dictionary<string, JevAnswer>(answers, StringComparer.Ordinal));
        Usage = usage ?? new JevUsage();
        RequestId = requestId;
        RawRepresentation = rawRepresentation is { } value ? JevJson.Own(value) : null;
    }

    /// <summary>Gets the model actually used, including alias resolution.</summary>
    public string Model { get; }

    /// <summary>Gets answers indexed by original question identifier.</summary>
    public IReadOnlyDictionary<string, JevAnswer> Answers { get; }

    /// <summary>Gets reported token usage.</summary>
    public JevUsage Usage { get; }

    /// <summary>Gets the x-typesafe-request-id header, when present.</summary>
    public string? RequestId { get; }

    /// <summary>Gets native JSON, including unknown fields. Do not log this by default.</summary>
    public JsonElement? RawRepresentation { get; }

    /// <summary>Retrieves the answer for a typed key, rejecting a missing key or type mismatch.</summary>
    public TAnswer GetAnswer<TAnswer>(JevQuestionKey<TAnswer> key) where TAnswer : JevAnswer
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Answers.TryGetValue(key.Name, out JevAnswer? answer))
        {
            throw new KeyNotFoundException("The response does not contain the requested question.");
        }

        return answer as TAnswer ?? throw new InvalidOperationException("The answer does not match the question key's type.");
    }
}
