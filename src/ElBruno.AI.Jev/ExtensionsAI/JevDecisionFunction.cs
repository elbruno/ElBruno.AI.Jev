using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.ExtensionsAI;

/// <summary>An explicitly named text-state tool backed by caller-configured Jev decisions, not native Jev tool calls.</summary>
/// <remarks>
/// The only argument is the required string <c>state</c>. The request factory owns questions, instructions and model choice.
/// Invocation returns a JSON object containing every typed answer and full distributions, usage and native metadata.
/// The decision client is borrowed. No reflection-based schema generation or arbitrary schema-to-Jev conversion is performed.
/// </remarks>
public sealed class JevDecisionFunction : AIFunction
{
    private static readonly JsonElement InputSchema = Parse("""
        {
          "type": "object",
          "properties": { "state": { "type": "string", "description": "Textual state to assess using the configured decision questions." } },
          "required": ["state"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ResultSchema = Parse("""
        {
          "type": "object",
          "properties": {
            "model": { "type": "string" },
            "requestId": { "type": ["string", "null"] },
            "usage": {
              "type": "object",
              "properties": {
                "inputTokens": { "type": ["integer", "null"] },
                "outputTokens": { "type": ["integer", "null"] }
              },
              "required": ["inputTokens", "outputTokens"]
            },
            "answers": {
              "type": "object",
              "additionalProperties": {
                "oneOf": [
                  {
                    "type": "object",
                    "properties": {
                      "type": { "const": "choice" },
                      "choice": { "type": "string" },
                      "confidence": { "type": "number" },
                      "probabilities": { "type": "object", "additionalProperties": { "type": "number" } },
                      "rawRepresentation": {}
                    },
                    "required": ["type", "choice", "confidence", "probabilities"]
                  },
                  {
                    "type": "object",
                    "properties": {
                      "type": { "const": "score" },
                      "score": { "type": "number" },
                      "confidence": { "type": "number" },
                      "probabilities": { "type": "object", "additionalProperties": { "type": "number" } },
                      "legend": { "type": "object" },
                      "rawRepresentation": {}
                    },
                    "required": ["type", "score", "confidence", "probabilities", "legend"]
                  },
                  {
                    "type": "object",
                    "properties": {
                      "type": { "const": "noul" },
                      "probability": { "type": "number" },
                      "rawRepresentation": {}
                    },
                    "required": ["type", "probability"]
                  }
                ]
              }
            },
            "rawRepresentation": {}
          },
          "required": ["model", "requestId", "usage", "answers"]
        }
        """);

    private readonly IJevDecisionClient _client;
    private readonly Func<string, JevDecisionRequest> _createRequest;

    /// <summary>Creates a tool with a provider-portable name (1-64 ASCII letters, digits, underscores or hyphens).</summary>
    public JevDecisionFunction(
        IJevDecisionClient client, string name, string description, Func<string, JevDecisionRequest> createRequest)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(createRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (name.Length > 64 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
        {
            throw new ArgumentException("A tool name must contain 1-64 ASCII letters, digits, underscores or hyphens.", nameof(name));
        }

        _client = client;
        _createRequest = createRequest;
        Name = name;
        Description = description;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string Description { get; }

    /// <inheritdoc />
    public override JsonElement JsonSchema => InputSchema;

    /// <inheritdoc />
    public override JsonElement? ReturnJsonSchema => ResultSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Count != 1 || !arguments.TryGetValue("state", out object? value))
        {
            throw new ArgumentException("The tool requires exactly one argument named 'state'.", nameof(arguments));
        }

        string state = value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString()!,
            _ => throw new ArgumentException("The 'state' argument must be a string.", nameof(arguments))
        };
        JevDecisionRequest request = _createRequest(state)
            ?? throw new InvalidOperationException("The decision request factory returned null.");
        cancellationToken.ThrowIfCancellationRequested();
        JevDecisionResponse response = await _client.EvaluateAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The decision client returned null.");
        cancellationToken.ThrowIfCancellationRequested();
        return Serialize(response);
    }

    private static JsonElement Serialize(JevDecisionResponse response)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", response.Model);
            writer.WriteString("requestId", response.RequestId);
            writer.WriteStartObject("usage");
            WriteCount(writer, "inputTokens", response.Usage.InputTokens);
            WriteCount(writer, "outputTokens", response.Usage.OutputTokens);
            writer.WriteEndObject();
            writer.WriteStartObject("answers");
            foreach ((string key, JevAnswer answer) in response.Answers)
            {
                writer.WriteStartObject(key);
                switch (answer)
                {
                    case JevChoiceAnswer choice:
                        writer.WriteString("type", "choice");
                        writer.WriteString("choice", choice.Choice);
                        writer.WriteNumber("confidence", choice.Confidence);
                        WriteDistribution(writer, choice.Probabilities);
                        break;
                    case JevScoreAnswer score:
                        writer.WriteString("type", "score");
                        writer.WriteNumber("score", score.Score);
                        writer.WriteNumber("confidence", score.Confidence);
                        WriteDistribution(writer, score.Probabilities);
                        writer.WriteStartObject("legend");
                        foreach ((string index, JsonElement level) in score.Legend)
                        {
                            writer.WritePropertyName(index);
                            level.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                        break;
                    case JevNoulAnswer noul:
                        writer.WriteString("type", "noul");
                        writer.WriteNumber("probability", noul.Probability);
                        break;
                    default:
                        throw new NotSupportedException("This answer type is not supported by the decision tool.");
                }

                WriteRaw(writer, answer.RawRepresentation);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            WriteRaw(writer, response.RawRepresentation);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteCount(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } count) writer.WriteNumber(name, count);
        else writer.WriteNull(name);
    }

    private static void WriteDistribution(Utf8JsonWriter writer, IReadOnlyDictionary<string, double> probabilities)
    {
        writer.WriteStartObject("probabilities");
        foreach ((string label, double probability) in probabilities) writer.WriteNumber(label, probability);
        writer.WriteEndObject();
    }

    private static void WriteRaw(Utf8JsonWriter writer, JsonElement? raw)
    {
        if (raw is not { } json) return;
        writer.WritePropertyName("rawRepresentation");
        json.WriteTo(writer);
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
