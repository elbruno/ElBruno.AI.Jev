using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElBruno.AI.Jev;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(JsonElement[]))]
[JsonSerializable(typeof(JevWireRequest))]
internal partial class JevJsonContext : JsonSerializerContext;

internal sealed class JevWireRequest
{
    [JsonPropertyName("state")]
    public required JsonElement State { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("questions")]
    public required Dictionary<string, JevWireQuestion> Questions { get; init; }
}

internal sealed class JevWireQuestion
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("instructions")]
    public JsonElement? Instructions { get; init; }

    [JsonPropertyName("criteria")]
    public JsonElement? Criteria { get; init; }
}
