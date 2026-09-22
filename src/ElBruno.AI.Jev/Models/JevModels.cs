using System.Text.Json;

namespace ElBruno.AI.Jev;

/// <summary>Documented model identifiers. Other present and future identifiers are accepted.</summary>
public static class JevModels
{
    /// <summary>The moving stable alias.</summary>
    public const string Latest = "jev-latest";

    /// <summary>The moving preview alias, which may currently resolve to the stable release.</summary>
    public const string Preview = "jev-preview";

    /// <summary>The version documented when this package was created.</summary>
    public const string Version1_13_0 = "jev-1.13.0";
}

/// <summary>A model discovery entry. Discovery need not include every accepted pinned version.</summary>
/// <param name="Name">The account-visible model identifier.</param>
/// <param name="Description">Provider description, when supplied.</param>
/// <param name="ReleaseDate">Provider date text, when supplied.</param>
public sealed record JevModelInfo(string Name, string? Description = null, string? ReleaseDate = null);

/// <summary>Model discovery with request metadata.</summary>
public sealed class JevModelList
{
    /// <summary>Creates an independently owned discovery result.</summary>
    public JevModelList(IEnumerable<JevModelInfo> models, string? requestId = null, JsonElement? rawRepresentation = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        Models = Array.AsReadOnly(models.ToArray());
        RequestId = requestId;
        RawRepresentation = rawRepresentation is { } value ? JevJson.Own(value) : null;
    }

    /// <summary>Gets account-visible model entries.</summary>
    public IReadOnlyList<JevModelInfo> Models { get; }

    /// <summary>Gets the provider request identifier.</summary>
    public string? RequestId { get; }

    /// <summary>Gets the native JSON, including unknown fields.</summary>
    public JsonElement? RawRepresentation { get; }
}
