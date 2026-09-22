using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ElBruno.AI.Jev.Tests.Native;

public sealed class NativeTransportTests
{
    [Theory]
    [InlineData(400, JevHttpErrorKind.Validation)]
    [InlineData(401, JevHttpErrorKind.Authentication)]
    [InlineData(403, JevHttpErrorKind.Forbidden)]
    [InlineData(404, JevHttpErrorKind.NotFound)]
    [InlineData(408, JevHttpErrorKind.Http)]
    [InlineData(422, JevHttpErrorKind.Validation)]
    [InlineData(429, JevHttpErrorKind.RateLimited)]
    [InlineData(529, JevHttpErrorKind.Overloaded)]
    [InlineData(500, JevHttpErrorKind.Http)]
    [InlineData(502, JevHttpErrorKind.Http)]
    [InlineData(503, JevHttpErrorKind.Http)]
    [InlineData(302, JevHttpErrorKind.Http)]
    public async Task HttpFailuresAreClassifiedWithoutExposingSensitiveBody(int status, JevHttpErrorKind kind)
    {
        const string body = """{"error":{"code":"provider-code","message":"private test state offline-test-credential-not-a-real-key"}}""";
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(body, status)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.MaxRetries = 0));
        JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(kind, exception.Kind);
        Assert.Equal("provider-code", exception.ErrorCode);
        Assert.Equal("offline-request-id", exception.RequestId);
        Assert.Null(exception.RetryAfter);
        Assert.Null(exception.ResponseBody);
        Assert.Contains(status.ToString(), exception.Message);
        Assert.DoesNotContain("private test state", exception.ToString());
        Assert.DoesNotContain(NativeFixtures.Credential, exception.ToString());
        Assert.DoesNotContain("provider-code", exception.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{\"code\":\"top-level\"}", "top-level")]
    [InlineData("{\"error\":{\"code\":\"nested\"}}", "nested")]
    [InlineData("{\"error\":\"text\",\"code\":\"outer\"}", "outer")]
    [InlineData("{\"error\":{\"message\":\"unknown\"}}", null)]
    [InlineData("{\"code\":7}", null)]
    [InlineData("{\"code\":null}", null)]
    [InlineData("[]", null)]
    [InlineData("null", null)]
    [InlineData("<html>upstream failed</html>", null)]
    [InlineData("{", null)]
    [InlineData("", null)]
    public async Task ErrorParsingNeverHidesHttpStatusAndBodyIsExplicitlyOptIn(string body, string? code)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(body, 422, requestId: null, contentType: "text/html")));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.IncludeErrorBody = true));
        JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Equal(code, exception.ErrorCode);
        Assert.Equal(body, exception.ResponseBody);
        Assert.Null(exception.RequestId);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/json")]
    [InlineData("image/json")]
    [InlineData(null)]
    public async Task SuccessfulNonJsonContentTypeIsProtocolFailureAndNeverRetried(string? contentType)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(contentType: contentType)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.RetryTransientErrors = true));
        JevProtocolException exception = await Assert.ThrowsAsync<JevProtocolException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal("offline-request-id", exception.RequestId);
        Assert.Null(exception.RawRepresentation);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("Application/JSON")]
    [InlineData("application/vnd.typesafe+json")]
    public async Task AcceptsJsonMediaTypes(string mediaType)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(contentType: mediaType)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        Assert.Equal("fast", (await client.EvaluateAsync(NativeFixtures.ChoiceRequest())).GetAnswer(new JevQuestionKey<JevChoiceAnswer>("route")).Choice);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("<html>not JSON</html>")]
    [InlineData("{\"model\":NaN}")]
    [InlineData("{\"model\":\"a\",}")]
    public async Task MalformedJsonSuccessProducesProtocolFailure(string body)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(body)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        JevProtocolException exception = await Assert.ThrowsAsync<JevProtocolException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal("offline-request-id", exception.RequestId);
        Assert.Null(exception.RawRepresentation);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulOversizedResponsesAreBoundedAndDisposed(bool knownLength)
    {
        var stream = new ObservedStream(Encoding.UTF8.GetBytes(new string('x', 1000)));
        var content = new ObservedContent(stream, knownLength ? 1000 : null);
        using var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.Add("x-typesafe-request-id", "size-id");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.MaxResponseBytes = 64));
        JevProtocolException exception = await Assert.ThrowsAsync<JevProtocolException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal("size-id", exception.RequestId);
        Assert.Equal(knownLength ? 0 : 65, stream.BytesRead);
        Assert.True(content.IsDisposed);
        Assert.True(stream.IsDisposed);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessExactlyAtByteLimitIsAccepted(bool knownLength)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(NativeFixtures.ChoiceJson);
        var stream = new ObservedStream(bytes);
        var content = new ObservedContent(stream, knownLength ? bytes.Length : null);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.MaxResponseBytes = bytes.Length));
        Assert.NotNull(await client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(bytes.Length, stream.BytesRead);
        Assert.True(stream.IsDisposed);
        Assert.True(content.IsDisposed);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ErrorBodyReadsAreBoundedEvenWithoutContentLength(bool knownLength, bool exposeBody)
    {
        var stream = new ObservedStream(Encoding.UTF8.GetBytes(new string('x', 1000)));
        var content = new ObservedContent(stream, knownLength ? 1000 : null);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content }));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o =>
        {
            o.MaxErrorBodyBytes = 32;
            o.IncludeErrorBody = exposeBody;
        }));
        JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(exposeBody ? new string('x', 32) : null, exception.ResponseBody);
        Assert.Null(exception.ErrorCode);
        Assert.Equal(33, stream.BytesRead);
        Assert.True(content.IsDisposed);
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task MultiBufferResponseUsesByteRatherThanCharacterLimit()
    {
        string json = NativeFixtures.NoulJson[..^1] + ",\"unknown\":\"" + new string('é', 9000) + "\"}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var stream = new ObservedStream(bytes);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ObservedContent(stream) }));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.MaxResponseBytes = bytes.Length));
        JevDecisionResponse response = await client.EvaluateAsync(NativeFixtures.NoulRequest());
        Assert.Equal(9000, response.RawRepresentation!.Value.GetProperty("unknown").GetString()!.Length);
        Assert.Equal(bytes.Length, stream.BytesRead);
        Assert.True(stream.ReadCalls >= 3);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task DefaultRetriesExactlyTwiceThenReturnsOriginalHttpClassification(int status)
    {
        var contents = new List<ObservedContent>();
        using var handler = new RecordingHandler((_, _) =>
        {
            var content = new ObservedContent(new ObservedStream(Encoding.UTF8.GetBytes("""{"code":"busy"}""")));
            contents.Add(content);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
            response.Headers.Add("retry-after-ms", "0");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(3, handler.Calls);
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(TimeSpan.Zero, exception.RetryAfter);
        Assert.All(contents, content => Assert.True(content.IsDisposed));
        Assert.Single(handler.Requests.Select(r => r.Body).Distinct());
        Assert.All(handler.Requests, r => Assert.Equal($"Bearer {NativeFixtures.Credential}", r.Authorization));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task RetriesCanRecoverAndHonorMillisecondHints(int status)
    {
        using var handler = new RecordingHandler((attempt, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: attempt == 1 ? status : 200);
            response.Headers.Add("retry-after-ms", "80");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        Stopwatch elapsed = Stopwatch.StartNew();
        Assert.NotNull(await client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(65), $"Retry happened before the server delay: {elapsed.Elapsed}.");
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task AmbiguousHttpFailuresAreNotReplayedByDefault(int status)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(status: status)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task AmbiguousHttpRetriesRequireExplicitOptIn(int status)
    {
        using var handler = new RecordingHandler((attempt, _) => Task.FromResult(NativeFixtures.JsonResponse(status: attempt < 3 ? status : 200)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.RetryTransientErrors = true));
        Assert.NotNull(await client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(false, 599, 1)]
    [InlineData(false, 600, 1)]
    [InlineData(true, 499, 1)]
    [InlineData(true, 500, 3)]
    [InlineData(true, 599, 3)]
    [InlineData(true, 600, 1)]
    [InlineData(true, 699, 1)]
    public async Task TransientOptInRetriesOnlyActual5xxStatuses(bool optIn, int status, int attempts)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(status: status)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.RetryTransientErrors = optIn));
        JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(JevHttpErrorKind.Http, exception.Kind);
        Assert.Equal(attempts, handler.Calls);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public async Task TransportFailuresRespectAmbiguousReplayOptIn(bool optIn, int expectedAttempts)
    {
        var failure = new HttpRequestException("simulated offline connection failure");
        using var handler = new RecordingHandler((_, _) => throw failure);
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.RetryTransientErrors = optIn));
        JevTransportException exception = await Assert.ThrowsAsync<JevTransportException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(expectedAttempts, handler.Calls);
    }

    [Fact]
    public async Task OptInNetworkRetryCanRecover()
    {
        using var handler = new RecordingHandler((attempt, _) => attempt < 2
            ? throw new HttpRequestException("offline transient failure")
            : Task.FromResult(NativeFixtures.JsonResponse()));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.RetryTransientErrors = true));
        Assert.NotNull(await client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DroppedResponseReadIsTransportFailureWithoutReplay()
    {
        var stream = new ObservedStream([]) { FailReads = true };
        var content = new ObservedContent(stream);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        var exception = await Assert.ThrowsAsync<JevTransportException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(1, handler.Calls);
        Assert.True(stream.IsDisposed);
        Assert.True(content.IsDisposed);
    }

    [Theory]
    [InlineData("retry-after-ms", "10000", 10000d)]
    [InlineData("retry-after-ms", "100000000", double.MaxValue)]
    [InlineData("Retry-After", "10", 10000d)]
    public async Task ServerDelayAboveMaximumIsNotShortenedOrRetried(string header, string value, double expectedMilliseconds)
    {
        using var handler = new RecordingHandler((_, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: 429);
            response.Headers.Add(header, value);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.MaxRetryDelay = TimeSpan.FromMilliseconds(100)));
        JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(expectedMilliseconds == double.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(expectedMilliseconds), exception.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task InvalidMillisecondHintsFallBackToRetryAfter(string value)
    {
        using var handler = new RecordingHandler((_, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: 429);
            response.Headers.TryAddWithoutValidation("retry-after-ms", value);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MillisecondHintTakesPrecedenceOverSeconds()
    {
        using var handler = new RecordingHandler((attempt, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: attempt == 1 ? 429 : 200);
            response.Headers.Add("retry-after-ms", "0.5");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(999));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        Assert.NotNull(await client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterDatesAreUnderstood(bool future)
    {
        using var handler = new RecordingHandler((attempt, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: future || attempt == 1 ? 429 : 200);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(future ? 10 : -10));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        if (future)
        {
            JevHttpException exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
            Assert.True(exception.RetryAfter > TimeSpan.FromMinutes(9));
            Assert.Equal(1, handler.Calls);
        }
        else
        {
            Assert.NotNull(await client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
            Assert.Equal(2, handler.Calls);
        }
    }

    [Fact]
    public async Task SecondsRetryAfterIsHonoredRatherThanUsingShorterLocalBackoff()
    {
        using var handler = new RecordingHandler((attempt, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: attempt == 1 ? 429 : 200);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        Stopwatch watch = Stopwatch.StartNew();
        await client.EvaluateAsync(NativeFixtures.ChoiceRequest());
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("read")]
    [InlineData("backoff")]
    public async Task CallerCancellationIsNotReportedAsTimeoutOrReplayed(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new ObservedStream([]) { BlockReads = true };
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler(async (_, token) =>
        {
            reached.TrySetResult();
            if (stage == "send") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (stage == "read") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ObservedContent(stream) };
            var response = NativeFixtures.JsonResponse(status: 429);
            response.Headers.Add("retry-after-ms", "1000");
            return response;
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        Task<JevDecisionResponse> operation = client.EvaluateAsync(NativeFixtures.ChoiceRequest(), cancellation.Token);
        await (stage == "read" ? stream.ReadStarted.Task : reached.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (stage == "backoff") await Task.Delay(30);
        cancellation.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, handler.Calls);
        if (stage == "read") Assert.True(stream.IsDisposed);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("read")]
    [InlineData("backoff")]
    public async Task TotalDeadlineIncludesSendingReadingAndRetryWaits(string stage)
    {
        var stream = new ObservedStream([]) { BlockReads = true };
        using var handler = new RecordingHandler(async (_, token) =>
        {
            if (stage == "send") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (stage == "read") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ObservedContent(stream) };
            var response = NativeFixtures.JsonResponse(status: 429);
            response.Headers.Add("retry-after-ms", "1000");
            return response;
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o => o.Timeout = TimeSpan.FromMilliseconds(60)));
        var exception = await Assert.ThrowsAsync<JevTimeoutException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal(1, handler.Calls);
        if (stage == "read") Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task TotalDeadlineIsNotResetBetweenRetryAttempts()
    {
        using var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(70, token);
            var response = NativeFixtures.JsonResponse(status: 429);
            response.Headers.Add("retry-after-ms", "0");
            return response;
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(180);
            o.MaxRetries = 10;
        }));
        await Assert.ThrowsAsync<JevTimeoutException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.InRange(handler.Calls, 1, 3);
    }

    [Fact]
    public async Task ShorterBorrowedHttpClientTimeoutIsStillDistinguishedFromCallerCancellation()
    {
        using var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return NativeFixtures.JsonResponse();
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        using var client = new JevClient(http, NativeFixtures.Options());
        await Assert.ThrowsAsync<JevTimeoutException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(TimeSpan.FromMilliseconds(50), http.Timeout);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PreCanceledRequestsNeverReachTransport()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest(), source.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ListModelsAsync(source.Token));
        Assert.Equal(0, handler.Calls);
    }
}
