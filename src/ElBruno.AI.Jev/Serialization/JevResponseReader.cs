using System.Globalization;
using System.Text.Json;

namespace ElBruno.AI.Jev;

internal static class JevResponseReader
{
    internal static JevDecisionResponse Decision(JsonElement root, JevDecisionRequest request, string? requestId)
    {
        Object(root);
        string model = Text(root, "model");
        JsonElement answers = Required(root, "answers", JsonValueKind.Object);
        Object(answers);
        if (answers.EnumerateObject().Count() != request.Questions.Count)
            throw new JevProtocolException("The response question identifiers do not match the request.");

        var results = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
        foreach (JsonProperty answer in answers.EnumerateObject())
        {
            if (!request.Questions.TryGetValue(answer.Name, out JevQuestion? question))
                throw new JevProtocolException("The response contains an unexpected question identifier.");
            Object(answer.Value);
            if (Text(answer.Value, "type") != question.Type)
                throw new JevProtocolException("The answer type is unsupported or does not match the requested question.");

            results.Add(answer.Name, question switch
            {
                JevChoiceQuestion choice => Choice(answer.Value, choice),
                JevScoreQuestion score => Score(answer.Value, score),
                JevNoulQuestion => new JevNoulAnswer(Number(answer.Value, "noul"), answer.Value),
                _ => throw new JevProtocolException("The question type is unsupported.")
            });
        }

        JevUsage usage = new();
        if (root.TryGetProperty("usage", out JsonElement usageJson) && usageJson.ValueKind != JsonValueKind.Null)
        {
            Object(usageJson);
            usage = new JevUsage(TokenCount(usageJson, "input_tokens"), TokenCount(usageJson, "output_tokens"));
        }

        return new JevDecisionResponse(model, results, usage, requestId, root);
    }

    internal static JevModelList Models(JsonElement root, string? requestId)
    {
        Object(root);
        JsonElement entries = Required(root, "models", JsonValueKind.Array);
        var models = new List<JevModelInfo>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            Object(entry);
            models.Add(new JevModelInfo(Text(entry, "name"), OptionalText(entry, "description"), OptionalText(entry, "release_date")));
        }

        return new JevModelList(models, requestId, root);
    }

    private static JevChoiceAnswer Choice(JsonElement answer, JevChoiceQuestion question)
    {
        Dictionary<string, double> probabilities = Probabilities(answer);
        if (!probabilities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(question.Criteria.Keys))
            throw new JevProtocolException("The choice distribution does not match the requested criteria.");
        return new JevChoiceAnswer(Text(answer, "choice"), probabilities, Number(answer, "confidence"), answer);
    }

    private static JevScoreAnswer Score(JsonElement answer, JevScoreQuestion question)
    {
        Dictionary<string, double> probabilities = Probabilities(answer);
        string[] indices = Enumerable.Range(0, question.Criteria.Count).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (!probabilities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(indices))
            throw new JevProtocolException("The score distribution does not match the requested rubric.");
        JsonElement legend = Required(answer, "legend", JsonValueKind.Object);
        Object(legend);
        var values = legend.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        if (!values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(indices))
            throw new JevProtocolException("The score legend does not match the requested rubric.");
        return new JevScoreAnswer(Number(answer, "score"), probabilities, values, Number(answer, "confidence"), answer);
    }

    private static Dictionary<string, double> Probabilities(JsonElement answer)
    {
        JsonElement distribution = Required(answer, "probabilities", JsonValueKind.Object);
        Object(distribution);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (JsonProperty property in distribution.EnumerateObject())
        {
            values.Add(property.Name, Number(distribution, property.Name));
        }

        return values;
    }

    private static long? TokenCount(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out JsonElement count) || count.ValueKind == JsonValueKind.Null) return null;
        if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out long result) || result < 0)
            throw new JevProtocolException("A reported token count is invalid.");
        return result;
    }

    private static double Number(JsonElement value, string name)
    {
        JsonElement element = Required(value, name, JsonValueKind.Number);
        if (!element.TryGetDouble(out double result) || !double.IsFinite(result))
            throw new JevProtocolException("A numeric answer field is invalid.");
        return result;
    }

    private static string Text(JsonElement value, string name)
    {
        string? text = Required(value, name, JsonValueKind.String).GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new JevProtocolException("A required string field is empty.");
        return text;
    }

    private static string? OptionalText(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new JevProtocolException("An optional string field has the wrong type.");
        return element.GetString();
    }

    private static JsonElement Required(JsonElement value, string name, JsonValueKind kind)
    {
        if (!value.TryGetProperty(name, out JsonElement element) || element.ValueKind != kind)
            throw new JevProtocolException("A required response field is missing or has the wrong type.");
        return element;
    }

    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JevProtocolException("A response object was expected.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new JevProtocolException("The response contains duplicate JSON properties.");
        }
    }
}
