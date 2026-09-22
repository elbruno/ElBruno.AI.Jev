using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ElBruno.AI.Jev;

/// <summary>Creates independently owned JSON values for structured decision input.</summary>
public static class JevJson
{
    /// <summary>Creates a JSON string, not JSON parsed from a string.</summary>
    public static JsonElement Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToElement(value, JevJsonContext.Default.String);
    }

    /// <summary>Parses and owns a JSON value independently of the source document.</summary>
    public static JsonElement Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Serializes a structured value using explicitly supplied serialization metadata.</summary>
    public static JsonElement From<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonSerializer.SerializeToElement(value, typeInfo);
    }

    internal static JsonElement Own(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("An undefined JSON value is not supported.", nameof(value));
        }

        return value.Clone();
    }
}
