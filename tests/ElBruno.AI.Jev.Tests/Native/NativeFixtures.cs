using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ElBruno.AI.Jev.Tests.Native;

internal static class NativeFixtures
{
    internal const string Credential = "offline-test-credential-not-a-real-key";
    internal const string ChoiceJson = """{"model":"jev-1.13.0","answers":{"route":{"type":"choice","choice":"fast","probabilities":{"fast":0.75,"careful":0.25},"confidence":0.8}}}""";
    internal const string ScoreJson = """{"model":"jev-1.13.0","answers":{"quality":{"type":"score","score":1.25,"probabilities":{"0":0.125,"1":0.5,"2":0.375},"legend":{"0":"poor","1":"fair","2":"good"},"confidence":0.7}}}""";
    internal const string NoulJson = """{"model":"jev-1.13.0","answers":{"safe":{"type":"noul","noul":0.8}}}""";

    internal static JevClientOptions Options(Action<JevClientOptions>? configure = null)
    {
        var options = new JevClientOptions
        {
            ApiKey = Credential,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(2)
        };
        configure?.Invoke(options);
        return options;
    }

    internal static JevDecisionRequest ChoiceRequest(string? model = null) =>
        new JevDecisionRequest("private test state", model).WithQuestion(
            new JevQuestionKey<JevChoiceAnswer>("route"),
            new JevChoiceQuestion("Pick a route.", new Dictionary<string, string?> { ["fast"] = "Simple", ["careful"] = "Complex" }));

    internal static JevDecisionRequest ScoreRequest() =>
        new JevDecisionRequest("state").WithQuestion(new JevQuestionKey<JevScoreAnswer>("quality"),
            new JevScoreQuestion("Assess quality.", new[] { "poor", "fair", "good" }));

    internal static JevDecisionRequest NoulRequest() =>
        new JevDecisionRequest("state").WithQuestion(new JevQuestionKey<JevNoulAnswer>("safe"), new JevNoulQuestion("Is it safe?"));

    internal static HttpResponseMessage JsonResponse(
        string json = ChoiceJson,
        int status = 200,
        string? requestId = "offline-request-id",
        string? contentType = "application/json")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8) };
        response.Content.Headers.ContentType = contentType is null ? null : new MediaTypeHeaderValue(contentType);
        if (requestId is not null) response.Headers.Add("x-typesafe-request-id", requestId);
        return response;
    }

    internal static async Task<JevDecisionResponse> EvaluateAsync(string json, JevDecisionRequest? request = null)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, Options());
        return await client.EvaluateAsync(request ?? ChoiceRequest());
    }

    internal static void AssertJson(string expected, string actual) =>
        Assert.True(JsonElement.DeepEquals(JevJson.Parse(expected), JevJson.Parse(actual)), $"Expected JSON: {expected}\nActual JSON: {actual}");
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? Authorization,
    string Accept,
    string UserAgent,
    string? ContentType,
    string? Body);

internal sealed class RecordingHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    private int _calls;
    private int _disposals;
    internal int Calls => Volatile.Read(ref _calls);
    internal int DisposalCount => Volatile.Read(ref _disposals);
    internal ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _calls);
        Requests.Enqueue(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.ToString(),
            request.Headers.Accept.ToString(),
            request.Headers.UserAgent.ToString(),
            request.Content?.Headers.ContentType?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return await response(attempt, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Interlocked.Increment(ref _disposals);
        base.Dispose(disposing);
    }
}

internal sealed class ObservedStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _source = new(bytes);
    internal int BytesRead { get; private set; }
    internal int ReadCalls { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal bool BlockReads { get; init; }
    internal bool FailReads { get; init; }
    internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        ReadStarted.TrySetResult();
        if (BlockReads) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (FailReads) throw new IOException("Offline simulated dropped response");
        int read = await _source.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class ObservedContent : HttpContent
{
    private readonly long? _length;
    internal ObservedStream Stream { get; }
    internal bool IsDisposed { get; private set; }

    internal ObservedContent(ObservedStream stream, long? length = null)
    {
        Stream = stream;
        _length = length;
        Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length ?? 0;
        return _length.HasValue;
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Stream);
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(Stream);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        if (disposing) Stream.Dispose();
        base.Dispose(disposing);
    }
}
