using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ElBruno.AI.Jev;

/// <summary>A thread-safe, asynchronous client for the official TypeSafe Jev API.</summary>
/// <remarks>Do not dispose while requests are running. Caller-supplied HTTP clients are borrowed by default.</remarks>
public sealed class JevClient : IJevDecisionClient, IDisposable
{
    private static readonly ProductInfoHeaderValue UserAgent = new("ElBruno.AI.Jev", typeof(JevClient).Assembly.GetName().Version!.ToString(3));
    private readonly JevClientOptions _options;
    private readonly HttpClient? _httpClient;
    private readonly Func<HttpClient>? _httpClientFactory;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    /// <summary>Creates a client owning an HTTP transport with redirects disabled and a bounded connection lifetime.</summary>
    public JevClient(JevClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        _ownsHttpClient = true;
    }

    /// <summary>Creates a client using an explicitly supplied transport. Configure its redirect policy and lifetime appropriately.</summary>
    public JevClient(HttpClient httpClient, JevClientOptions options, bool disposeHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
        _httpClient = httpClient;
        _ownsHttpClient = disposeHttpClient;
    }

    internal JevClient(Func<HttpClient> httpClientFactory, JevClientOptions options)
    {
        _options = options.Snapshot();
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public Task<JevDecisionResponse> EvaluateAsync(JevDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Questions.Count == 0) throw new ArgumentException("At least one question is required.", nameof(request));
        var wire = new JevWireRequest
        {
            State = request.State,
            Model = request.Model ?? _options.DefaultModel,
            Questions = request.Questions.ToDictionary(pair => pair.Key, pair => new JevWireQuestion
            {
                Type = pair.Value.Type,
                Instructions = pair.Value.Instructions,
                Criteria = pair.Value.SerializeCriteria()
            }, StringComparer.Ordinal)
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(wire, JevJsonContext.Default.JevWireRequest);
        return SendAsync(HttpMethod.Post, "v1/systemone", body, (json, id) => JevResponseReader.Decision(json, request, id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<JevModelList> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return SendAsync(HttpMethod.Get, "v1/models", null, JevResponseReader.Models, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, byte[]? body, Func<JsonElement, string?, T> parse, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        HttpClient client = _httpClientFactory?.Invoke() ?? _httpClient!;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                HttpResult result;
                try
                {
                    result = await AttemptAsync(client, method, path, body, deadline.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                {
                    if (_options.RetryTransientErrors && attempt < _options.MaxRetries)
                    {
                        await Task.Delay(Backoff(attempt), deadline.Token).ConfigureAwait(false);
                        continue;
                    }

                    throw new JevTransportException(exception);
                }
                catch (IOException exception)
                {
                    // A dropped response may already have been billed: no implicit replay.
                    throw new JevTransportException(exception);
                }

                if ((int)result.Status is < 200 or >= 300)
                {
                    bool retryable = (int)result.Status is 429 or 529 ||
                        (_options.RetryTransientErrors && ((int)result.Status == 408 || (int)result.Status is >= 500 and <= 599));
                    TimeSpan delay = result.RetryAfter ?? Backoff(attempt);
                    if (retryable && attempt < _options.MaxRetries && delay <= _options.MaxRetryDelay)
                    {
                        await Task.Delay(delay, deadline.Token).ConfigureAwait(false);
                        continue;
                    }

                    throw new JevHttpException(result.Status, result.RequestId, result.RetryAfter, ErrorCode(result.Body),
                        _options.IncludeErrorBody ? Encoding.UTF8.GetString(result.Body) : null);
                }

                if (!IsJson(result.ContentType)) throw new JevProtocolException("Jev returned a successful response with a non-JSON content type.", result.RequestId);
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(result.Body);
                }
                catch (JsonException)
                {
                    throw new JevProtocolException("Jev returned malformed JSON.", result.RequestId);
                }

                using (document)
                {
                    try
                    {
                        return parse(document.RootElement, result.RequestId);
                    }
                    catch (JevProtocolException exception)
                    {
                        throw new JevProtocolException(exception.Message, result.RequestId, document.RootElement);
                    }
                    catch (ArgumentException)
                    {
                        throw new JevProtocolException("Jev returned an invalid decision value or distribution.", result.RequestId, document.RootElement);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new JevTimeoutException(exception);
        }
        finally
        {
            if (_httpClientFactory is not null) client.Dispose();
        }
    }

    private async Task<HttpResult> AttemptAsync(HttpClient client, HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.Endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(UserAgent);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        string? requestId = response.Headers.TryGetValues("x-typesafe-request-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null;
        bool success = response.IsSuccessStatusCode;
        int limit = success ? _options.MaxResponseBytes : _options.MaxErrorBodyBytes;
        if (success && response.Content.Headers.ContentLength > limit)
            throw new JevProtocolException("Jev's response exceeded the configured size limit.", requestId);
        byte[] data = await ReadBoundedAsync(response.Content, limit, truncate: !success, requestId, cancellationToken).ConfigureAwait(false);
        return new HttpResult(response.StatusCode, data, response.Content.Headers.ContentType?.MediaType, requestId, RetryAfter(response));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, bool truncate, string? requestId, CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[Math.Min(8192, limit + 1)];
        while (true)
        {
            int remaining = limit - (int)destination.Length;
            int read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (read > remaining)
            {
                if (!truncate) throw new JevProtocolException("Jev's response exceeded the configured size limit.", requestId);
                destination.Write(buffer, 0, remaining);
                break;
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private TimeSpan Backoff(int attempt)
    {
        double milliseconds = Math.Min(_options.MaxRetryDelay.TotalMilliseconds, _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(milliseconds * (0.75 + Random.Shared.NextDouble() * 0.25));
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out IEnumerable<string>? values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds) &&
            double.IsFinite(milliseconds) && milliseconds >= 0)
            return milliseconds > TimeSpan.FromDays(1).TotalMilliseconds ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(milliseconds);
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            TimeSpan remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private static string? ErrorCode(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object) root = error;
            return root.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            // HTTP status remains authoritative for HTML, text, and truncated error bodies.
            return null;
        }
    }

    private static bool IsJson(string? mediaType) =>
        string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
        (mediaType?.StartsWith("application/", StringComparison.OrdinalIgnoreCase) == true && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Disposes owned transports once. Borrowed transports and pooled factory handlers remain caller-owned.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient) _httpClient?.Dispose();
    }

    private sealed record HttpResult(HttpStatusCode Status, byte[] Body, string? ContentType, string? RequestId, TimeSpan? RetryAfter);
}
