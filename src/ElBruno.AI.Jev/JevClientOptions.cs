namespace ElBruno.AI.Jev;

/// <summary>Connection and bounded resilience settings. A client snapshots these options at construction.</summary>
public sealed class JevClientOptions
{
    /// <summary>Gets or sets the official TypeSafe credential. It is never included in default diagnostics.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Gets or sets an explicit service origin. Credentials are sent only to this configured origin.</summary>
    public Uri Endpoint { get; set; } = new("https://api.typesafe.ai");

    /// <summary>Gets or sets the model used when a request does not specify one. Defaults to the moving stable alias.</summary>
    public string DefaultModel { get; set; } = JevModels.Latest;

    /// <summary>Gets or sets the total deadline including response reads and all retry delays.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets additional attempts for 429/529. Set zero to disable all retries.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Gets or sets whether ambiguous network failures, HTTP 408 and other 5xx may be replayed. Replays may incur charges.</summary>
    public bool RetryTransientErrors { get; set; }

    /// <summary>Gets or sets the initial exponential backoff before jitter.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets or sets the largest acceptable retry wait. Longer server hints prevent a retry rather than being shortened.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the maximum successful response size in bytes.</summary>
    public int MaxResponseBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Gets or sets the maximum error-body bytes read from the service.</summary>
    public int MaxErrorBodyBytes { get; set; } = 8 * 1024;

    /// <summary>Gets or sets whether bounded, potentially sensitive error bodies are exposed through an exception property.</summary>
    public bool IncludeErrorBody { get; set; }

    /// <summary>Gets or sets an explicit test-only exception allowing HTTP to a loopback origin.</summary>
    public bool AllowInsecureLoopback { get; set; }

    internal string[] ValidationErrors()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsWhiteSpace))
            errors.Add("Jev:ApiKey must contain a nonempty credential without whitespace.");
        if (Endpoint is null || !Endpoint.IsAbsoluteUri ||
            (Endpoint.Scheme != Uri.UriSchemeHttps && !(AllowInsecureLoopback && Endpoint.Scheme == Uri.UriSchemeHttp && Endpoint.IsLoopback)) ||
            Endpoint.AbsolutePath != "/" || Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0 || Endpoint.UserInfo.Length != 0)
            errors.Add("Jev:Endpoint must be an HTTPS origin without a path, query, fragment, or user information. HTTP loopback requires explicit test opt-in.");
        if (string.IsNullOrWhiteSpace(DefaultModel)) errors.Add("Jev:DefaultModel must be nonempty.");
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromDays(1)) errors.Add("Jev:Timeout must be greater than zero and at most one day.");
        if (MaxRetries is < 0 or > 10) errors.Add("Jev:MaxRetries must be between zero and ten.");
        if (InitialRetryDelay <= TimeSpan.Zero || MaxRetryDelay < InitialRetryDelay || MaxRetryDelay > TimeSpan.FromDays(1))
            errors.Add("Jev retry delays must be positive, ordered, and at most one day.");
        if (MaxResponseBytes is < 1 or > 64 * 1024 * 1024) errors.Add("Jev:MaxResponseBytes must be between 1 and 67108864.");
        if (MaxErrorBodyBytes is < 1 or > 1024 * 1024) errors.Add("Jev:MaxErrorBodyBytes must be between 1 and 1048576.");
        return errors.ToArray();
    }

    internal JevClientOptions Snapshot()
    {
        string[] errors = ValidationErrors();
        if (errors.Length > 0) throw new ArgumentException(string.Join(" ", errors), nameof(JevClientOptions));
        return (JevClientOptions)MemberwiseClone();
    }
}
