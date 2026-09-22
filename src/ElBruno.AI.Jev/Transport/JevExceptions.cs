using System.Net;
using System.Text.Json;

namespace ElBruno.AI.Jev;

/// <summary>Base SDK exception. Default messages do not include state, keys, or response bodies.</summary>
public abstract class JevException : Exception
{
    private protected JevException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Classification of HTTP failures without assuming a stable provider error envelope.</summary>
public enum JevHttpErrorKind
{
    /// <summary>An otherwise unclassified HTTP failure.</summary>
    Http,
    /// <summary>The credential was not accepted.</summary>
    Authentication,
    /// <summary>Access was forbidden.</summary>
    Forbidden,
    /// <summary>The request failed validation.</summary>
    Validation,
    /// <summary>A resource was not found.</summary>
    NotFound,
    /// <summary>The provider rejected a request due to rate limiting.</summary>
    RateLimited,
    /// <summary>The provider reported temporary overload (529).</summary>
    Overloaded
}

/// <summary>An HTTP error, with safe status metadata and explicitly opt-in body access.</summary>
public sealed class JevHttpException : JevException
{
    /// <summary>Creates an HTTP failure. The body is never interpolated into the message.</summary>
    public JevHttpException(HttpStatusCode statusCode, string? requestId = null, TimeSpan? retryAfter = null, string? errorCode = null, string? responseBody = null)
        : base($"Jev returned HTTP {(int)statusCode}. Inspect StatusCode, Kind, and RequestId for diagnostics.")
    {
        StatusCode = statusCode;
        RequestId = requestId;
        RetryAfter = retryAfter;
        ErrorCode = errorCode;
        ResponseBody = responseBody;
        Kind = (int)statusCode switch
        {
            401 => JevHttpErrorKind.Authentication,
            403 => JevHttpErrorKind.Forbidden,
            400 or 422 => JevHttpErrorKind.Validation,
            404 => JevHttpErrorKind.NotFound,
            429 => JevHttpErrorKind.RateLimited,
            529 => JevHttpErrorKind.Overloaded,
            _ => JevHttpErrorKind.Http
        };
    }

    /// <summary>Gets the original status.</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>Gets the error category.</summary>
    public JevHttpErrorKind Kind { get; }
    /// <summary>Gets the provider request identifier when available.</summary>
    public string? RequestId { get; }
    /// <summary>Gets a valid server retry delay, even when too long for the configured retry budget.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>Gets a provider code when a recognized JSON code field exists. Not included in default messages.</summary>
    public string? ErrorCode { get; }
    /// <summary>Gets a bounded, possibly truncated sensitive body only when explicitly enabled.</summary>
    public string? ResponseBody { get; }
}

/// <summary>A network failure. Automatic replay is disabled by default.</summary>
public sealed class JevTransportException : JevException
{
    /// <summary>Creates a transport failure.</summary>
    public JevTransportException(Exception innerException) : base("The Jev request failed at the transport layer.", innerException) { }
}

/// <summary>A request exceeded its total deadline or the supplied HttpClient's shorter timeout.</summary>
public sealed class JevTimeoutException : JevException
{
    /// <summary>Creates a timeout failure, distinct from caller cancellation.</summary>
    public JevTimeoutException(Exception innerException) : base("The Jev request timed out.", innerException) { }
}

/// <summary>A successful HTTP response did not satisfy the native contract.</summary>
public sealed class JevProtocolException : JevException
{
    /// <summary>Creates a protocol failure with an optional independently owned native value.</summary>
    public JevProtocolException(string message, string? requestId = null, JsonElement? rawRepresentation = null)
        : base(message)
    {
        RequestId = requestId;
        RawRepresentation = rawRepresentation is { } value ? JevJson.Own(value) : null;
    }

    /// <summary>Gets the provider request identifier.</summary>
    public string? RequestId { get; }
    /// <summary>Gets native JSON when available. This property can contain sensitive data.</summary>
    public JsonElement? RawRepresentation { get; }
}
