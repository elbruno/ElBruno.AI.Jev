# Errors, cancellation, and retries

## HTTP failures

`JevHttpException` exposes `StatusCode`, `Kind`, `RequestId`, optional `RetryAfter`, and an optional recognized provider `ErrorCode`. Its message never includes credentials, state, or response body.

| Status | Kind | Default retry |
| --- | --- | --- |
| 400 / 422 | Validation | No |
| 401 | Authentication | No |
| 403 | Forbidden | No |
| 404 | NotFound | No |
| 429 | RateLimited | Up to `MaxRetries` |
| 529 | Overloaded | Up to `MaxRetries` |
| Other statuses, including redirects | Http | No |

No error envelope is assumed to exist for every status. HTML, text, malformed JSON, and truncated error bodies still produce the correct HTTP failure. The SDK reads at most `MaxErrorBodyBytes` (8 KiB by default). The bounded body is exposed only when `IncludeErrorBody = true`; it can be sensitive and must not be indiscriminately logged.

## Resilience

Defaults: two additional attempts for 429/529, 500 ms initial exponential delay, a 5-second maximum wait, jitter, and a **30-second total deadline** including sending, response reads, and backoff.

`retry-after-ms` takes precedence over `Retry-After` seconds/date. A hint longer than `MaxRetryDelay` prevents retry; it is not shortened to call the service before the permitted time.

`RetryTransientErrors = true` explicitly permits replay after network `HttpRequestException`, HTTP 408, and other 5xx. Such requests may already have executed or been billed. Interrupted response-body `IOException` is not replayed. Set `MaxRetries = 0` to disable all SDK retries.

Do not layer another retry handler blindly over SDK retries. Choose one resilience owner. The policy deliberately differs from the official JavaScript SDK's broader defaults because idempotency and duplicate billing are not documented.

## Other failures

- `JevTransportException`: failed network operation, preserving its underlying cause.
- `JevTimeoutException`: total deadline, or a shorter caller-supplied HTTP-client timeout.
- `OperationCanceledException`: caller cancellation, not an HTTP error or a silent empty result.
- `JevProtocolException`: malformed successful response, unsupported answer type, invalid values, unexpected content type, or response-size limit. When available, native JSON is exposed explicitly.
- `ArgumentException` and options-validation failures: invalid local arguments/configuration, before sending a request.
- `ObjectDisposedException`: use after disposal.

Cancellation stops local HTTP work and retry waits. It does **not** promise remote cancellation or a billing refund. Streaming in MEAI wrappers belongs to downstream chat providers, not native Jev.

Successful bodies are capped at 16 MiB by default, including unknown-length responses. Supplied transports should disable redirects and provide appropriate HTTP lifetimes. The SDK's owned/factory transports do so.
