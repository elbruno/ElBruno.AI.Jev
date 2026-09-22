using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElBruno.AI.Jev.Tests.Native;

public sealed class NativeOptionsAndLifetimeTests
{
    [Fact]
    public void DefaultsAreSecureBoundedAndConservative()
    {
        var options = new JevClientOptions();
        Assert.Equal("", options.ApiKey);
        Assert.Equal(new Uri("https://api.typesafe.ai"), options.Endpoint);
        Assert.Equal(JevModels.Latest, options.DefaultModel);
        Assert.Equal("jev-latest", JevModels.Latest);
        Assert.Equal("jev-preview", JevModels.Preview);
        Assert.Equal("jev-1.13.0", JevModels.Version1_13_0);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(2, options.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.InitialRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), options.MaxRetryDelay);
        Assert.Equal(16 * 1024 * 1024, options.MaxResponseBytes);
        Assert.Equal(8 * 1024, options.MaxErrorBodyBytes);
        Assert.False(options.IncludeErrorBody);
        Assert.False(options.RetryTransientErrors);
        Assert.False(options.AllowInsecureLoopback);
        using var client = new JevClient(NativeFixtures.Options());
        client.Dispose();
        client.Dispose();
    }

    [Theory]
    [InlineData("key-null")]
    [InlineData("key-empty")]
    [InlineData("key-whitespace")]
    [InlineData("key-space")]
    [InlineData("key-crlf")]
    [InlineData("endpoint-null")]
    [InlineData("endpoint-relative")]
    [InlineData("endpoint-http")]
    [InlineData("endpoint-nonloopback-optin")]
    [InlineData("endpoint-ftp")]
    [InlineData("endpoint-path")]
    [InlineData("endpoint-query")]
    [InlineData("endpoint-fragment")]
    [InlineData("endpoint-userinfo")]
    [InlineData("endpoint-loopback-without-optin")]
    [InlineData("model-empty")]
    [InlineData("model-null")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-infinite")]
    [InlineData("timeout-long")]
    [InlineData("retries-negative")]
    [InlineData("retries-large")]
    [InlineData("initial-zero")]
    [InlineData("initial-negative")]
    [InlineData("delays-reversed")]
    [InlineData("delay-long")]
    [InlineData("response-zero")]
    [InlineData("response-negative")]
    [InlineData("response-large")]
    [InlineData("error-zero")]
    [InlineData("error-negative")]
    [InlineData("error-large")]
    public void InvalidOptionsFailConstructionAndDiValidationWithoutLeakingValues(string invalid)
    {
        JevClientOptions options = NativeFixtures.Options(o => MakeInvalid(o, invalid));
        using var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new JevClient(http, options));
        Assert.DoesNotContain(NativeFixtures.Credential, exception.ToString());
        Assert.DoesNotContain("user-secret", exception.ToString());
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, handler.DisposalCount);
        var services = new ServiceCollection();
        services.AddJev(o =>
        {
            o.ApiKey = NativeFixtures.Credential;
            MakeInvalid(o, invalid);
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IJevDecisionClient>());
        Assert.NotEmpty(failure.Failures);
        Assert.DoesNotContain(NativeFixtures.Credential, failure.ToString());
        Assert.DoesNotContain("user-secret", failure.ToString());
    }

    private static void MakeInvalid(JevClientOptions options, string invalid)
    {
        switch (invalid)
        {
            case "key-null": options.ApiKey = null!; break;
            case "key-empty": options.ApiKey = ""; break;
            case "key-whitespace": options.ApiKey = " "; break;
            case "key-space": options.ApiKey = NativeFixtures.Credential + " value"; break;
            case "key-crlf": options.ApiKey = NativeFixtures.Credential + "\r\nbad"; break;
            case "endpoint-null": options.Endpoint = null!; break;
            case "endpoint-relative": options.Endpoint = new Uri("relative", UriKind.Relative); break;
            case "endpoint-http": options.Endpoint = new Uri("http://offline.invalid"); break;
            case "endpoint-nonloopback-optin": options.Endpoint = new Uri("http://offline.invalid"); options.AllowInsecureLoopback = true; break;
            case "endpoint-ftp": options.Endpoint = new Uri("ftp://localhost"); options.AllowInsecureLoopback = true; break;
            case "endpoint-path": options.Endpoint = new Uri("https://offline.invalid/v1"); break;
            case "endpoint-query": options.Endpoint = new Uri("https://offline.invalid?api_key=user-secret"); break;
            case "endpoint-fragment": options.Endpoint = new Uri("https://offline.invalid#user-secret"); break;
            case "endpoint-userinfo": options.Endpoint = new Uri("https://user:user-secret@offline.invalid"); break;
            case "endpoint-loopback-without-optin": options.Endpoint = new Uri("http://127.0.0.1:4321"); break;
            case "model-empty": options.DefaultModel = " "; break;
            case "model-null": options.DefaultModel = null!; break;
            case "timeout-zero": options.Timeout = TimeSpan.Zero; break;
            case "timeout-infinite": options.Timeout = Timeout.InfiniteTimeSpan; break;
            case "timeout-long": options.Timeout = TimeSpan.FromDays(1) + TimeSpan.FromTicks(1); break;
            case "retries-negative": options.MaxRetries = -1; break;
            case "retries-large": options.MaxRetries = 11; break;
            case "initial-zero": options.InitialRetryDelay = TimeSpan.Zero; break;
            case "initial-negative": options.InitialRetryDelay = TimeSpan.FromTicks(-1); break;
            case "delays-reversed": options.MaxRetryDelay = TimeSpan.Zero; break;
            case "delay-long": options.MaxRetryDelay = TimeSpan.FromDays(2); break;
            case "response-zero": options.MaxResponseBytes = 0; break;
            case "response-negative": options.MaxResponseBytes = -1; break;
            case "response-large": options.MaxResponseBytes = 64 * 1024 * 1024 + 1; break;
            case "error-zero": options.MaxErrorBodyBytes = 0; break;
            case "error-negative": options.MaxErrorBodyBytes = -1; break;
            case "error-large": options.MaxErrorBodyBytes = 1024 * 1024 + 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }
    }

    [Theory]
    [InlineData("https://offline.invalid:8443", false)]
    [InlineData("http://localhost:4321", true)]
    [InlineData("http://127.0.0.1:4321", true)]
    [InlineData("http://[::1]:4321", true)]
    public async Task ExplicitOriginAndLoopbackOptInNeverChangeOfficialRelativePaths(string endpoint, bool insecure)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse()));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://ignored.invalid/path/") };
        using var client = new JevClient(http, NativeFixtures.Options(o =>
        {
            o.Endpoint = new Uri(endpoint);
            o.AllowInsecureLoopback = insecure;
        }));
        await client.EvaluateAsync(NativeFixtures.ChoiceRequest());
        Assert.Equal(new Uri(new Uri(endpoint), "v1/systemone"), Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public void ExactOptionUpperAndLowerBoundsAreAccepted()
    {
        using var http = new HttpClient(new RecordingHandler((_, _) => throw new InvalidOperationException("must not send")));
        using var maximum = new JevClient(http, NativeFixtures.Options(o =>
        {
            o.Timeout = TimeSpan.FromDays(1);
            o.InitialRetryDelay = TimeSpan.FromDays(1);
            o.MaxRetryDelay = TimeSpan.FromDays(1);
            o.MaxRetries = 10;
            o.MaxResponseBytes = 64 * 1024 * 1024;
            o.MaxErrorBodyBytes = 1024 * 1024;
        }));
        using var minimum = new JevClient(http, NativeFixtures.Options(o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(1);
            o.InitialRetryDelay = TimeSpan.FromMilliseconds(1);
            o.MaxRetryDelay = TimeSpan.FromMilliseconds(1);
            o.MaxRetries = 0;
            o.MaxResponseBytes = 1;
            o.MaxErrorBodyBytes = 1;
        }));
    }

    [Fact]
    public async Task MutatingOriginalOptionsCannotChangeClientCredentialOriginModelOrResilience()
    {
        var options = NativeFixtures.Options(o => o.DefaultModel = "initial-model");
        using var handler = new RecordingHandler((attempt, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: attempt == 1 ? 429 : 200);
            response.Headers.Add("retry-after-ms", "0");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, options);
        options.ApiKey = "modified-key";
        options.Endpoint = new Uri("https://changed.invalid");
        options.DefaultModel = "modified-model";
        options.MaxRetries = 0;
        options.Timeout = TimeSpan.FromTicks(1);
        options.MaxResponseBytes = 1;
        options.MaxErrorBodyBytes = 1;
        options.IncludeErrorBody = true;
        options.RetryTransientErrors = true;
        options.InitialRetryDelay = TimeSpan.FromDays(2);
        options.MaxRetryDelay = TimeSpan.Zero;
        Assert.NotNull(await client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal($"Bearer {NativeFixtures.Credential}", request.Authorization);
            Assert.Equal("api.typesafe.ai", request.Uri!.Host);
            Assert.Equal("initial-model", JevJson.Parse(request.Body!).GetProperty("model").GetString());
        });
    }

    [Fact]
    public async Task BorrowedTransportSurvivesIdempotentClientDisposal()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse()));
        using var http = new HttpClient(handler);
        var client = new JevClient(http, NativeFixtures.Options());
        client.Dispose();
        client.Dispose();
        Assert.Equal(0, handler.DisposalCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.EvaluateAsync(NativeFixtures.ChoiceRequest()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListModelsAsync());
        using HttpResponseMessage result = await http.GetAsync("https://offline.invalid");
        Assert.True(result.IsSuccessStatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OwnedTransportIsDisposedExactlyOnce()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse()));
        var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options(), disposeHttpClient: true);
        client.Dispose();
        client.Dispose();
        Assert.Equal(1, handler.DisposalCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => http.GetAsync("https://offline.invalid"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListModelsAsync());
    }

    [Fact]
    public async Task SharedHttpClientDoesNotMixPerRequestCredentialsOrMutateDefaults()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight = 0;
        using var handler = new RecordingHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref inFlight) == 2) entered.TrySetResult();
            await gate.Task.WaitAsync(token);
            return NativeFixtures.JsonResponse();
        });
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-default");
        using var first = new JevClient(http, NativeFixtures.Options(o => o.ApiKey = "offline-first-key"));
        using var second = new JevClient(http, NativeFixtures.Options(o => o.ApiKey = "offline-second-key"));
        Task<JevDecisionResponse> one = first.EvaluateAsync(NativeFixtures.ChoiceRequest("first-model"));
        Task<JevDecisionResponse> two = second.EvaluateAsync(NativeFixtures.ChoiceRequest("second-model"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetResult();
        await Task.WhenAll(one, two);
        Assert.Equal("Bearer caller-default", http.DefaultRequestHeaders.Authorization.ToString());
        Assert.Contains(handler.Requests, r => r.Authorization == "Bearer offline-first-key" && JevJson.Parse(r.Body!).GetProperty("model").GetString() == "first-model");
        Assert.Contains(handler.Requests, r => r.Authorization == "Bearer offline-second-key" && JevJson.Parse(r.Body!).GetProperty("model").GetString() == "second-model");
    }

    [Fact]
    public async Task FactorySingletonCreatesAndDisposesOneClientPerConcurrentOperationNotAtResolution()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int operations = 12;
        int inFlight = 0;
        using var handler = new RecordingHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref inFlight) == operations) entered.TrySetResult();
            await gate.Task.WaitAsync(token);
            return NativeFixtures.JsonResponse();
        });
        var factory = new ObservedHttpClientFactory(handler);
        var services = new ServiceCollection();
        services.AddJev(o => o.ApiKey = NativeFixtures.Credential);
        services.AddSingleton<IHttpClientFactory>(factory);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        JevClient concrete = provider.GetRequiredService<JevClient>();
        Assert.Same(concrete, provider.GetRequiredService<IJevDecisionClient>());
        Assert.Same(concrete, provider.GetRequiredService<JevClient>());
        Assert.Empty(factory.Clients);
        Task<JevDecisionResponse>[] work = Enumerable.Range(0, operations).Select(_ => concrete.EvaluateAsync(NativeFixtures.ChoiceRequest())).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(operations, factory.Clients.Count);
        Assert.All(factory.Clients, item => Assert.Equal(0, item.DisposalCount));
        gate.SetResult();
        JevDecisionResponse[] answers = await Task.WhenAll(work);
        Assert.All(answers, response => Assert.Equal("fast", response.GetAnswer(new JevQuestionKey<JevChoiceAnswer>("route")).Choice));
        Assert.All(factory.Clients, item => Assert.Equal(1, item.DisposalCount));
        Assert.All(factory.Names, name => Assert.Equal(JevServiceCollectionExtensions.HttpClientName, name));
        Assert.Equal(0, handler.DisposalCount);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("http")]
    [InlineData("protocol")]
    [InlineData("transport")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task FactoryTransportIsDisposedOnEveryTerminalOutcome(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler(async (_, token) =>
        {
            started.TrySetResult();
            if (outcome is "timeout" or "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (outcome == "transport") throw new HttpRequestException("offline failure");
            return NativeFixtures.JsonResponse(outcome == "protocol" ? "{}" : NativeFixtures.ChoiceJson, outcome == "http" ? 401 : 200);
        });
        var factory = new ObservedHttpClientFactory(handler);
        var services = new ServiceCollection();
        services.AddJev(o =>
        {
            o.ApiKey = NativeFixtures.Credential;
            o.Timeout = TimeSpan.FromMilliseconds(outcome == "timeout" ? 50 : 5000);
        });
        services.AddSingleton<IHttpClientFactory>(factory);
        using ServiceProvider provider = services.BuildServiceProvider();
        Task<JevDecisionResponse> operation = provider.GetRequiredService<IJevDecisionClient>().EvaluateAsync(NativeFixtures.ChoiceRequest(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (outcome == "cancel") cancellation.Cancel();
        switch (outcome)
        {
            case "http": await Assert.ThrowsAsync<JevHttpException>(() => operation); break;
            case "protocol": await Assert.ThrowsAsync<JevProtocolException>(() => operation); break;
            case "transport": await Assert.ThrowsAsync<JevTransportException>(() => operation); break;
            case "timeout": await Assert.ThrowsAsync<JevTimeoutException>(() => operation); break;
            case "cancel": await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); break;
            default: Assert.NotNull(await operation); break;
        }
        Assert.Equal(1, Assert.Single(factory.Clients).DisposalCount);
        Assert.Equal(0, handler.DisposalCount);
    }

    [Fact]
    public async Task FactoryClientIsReusedWithinRetryBudgetButNotAcrossOperations()
    {
        using var handler = new RecordingHandler((attempt, _) =>
        {
            var response = NativeFixtures.JsonResponse(status: attempt == 1 ? 429 : 200);
            response.Headers.Add("retry-after-ms", "0");
            return Task.FromResult(response);
        });
        var factory = new ObservedHttpClientFactory(handler);
        var services = new ServiceCollection();
        services.AddJev(o => o.ApiKey = NativeFixtures.Credential);
        services.AddSingleton<IHttpClientFactory>(factory);
        using ServiceProvider provider = services.BuildServiceProvider();
        IJevDecisionClient client = provider.GetRequiredService<IJevDecisionClient>();
        await client.EvaluateAsync(NativeFixtures.ChoiceRequest());
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, Assert.Single(factory.Clients).DisposalCount);
        await client.EvaluateAsync(NativeFixtures.ChoiceRequest());
        Assert.Equal(2, factory.Clients.Count);
        Assert.All(factory.Clients, item => Assert.Equal(1, item.DisposalCount));
    }

    [Fact]
    public async Task ActualDiNamedClientAllowsHandlerInjectionAndKeepsPooledHandlerAlive()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse("""{"models":[]}""")));
        var services = new ServiceCollection();
        IHttpClientBuilder builder = services.AddJev(o => o.ApiKey = NativeFixtures.Credential);
        builder.ConfigurePrimaryHttpMessageHandler(() => handler);
        Assert.Equal(JevServiceCollectionExtensions.HttpClientName, builder.Name);
        using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient transport = provider.GetRequiredService<IHttpClientFactory>().CreateClient(builder.Name);
        Assert.Equal(Timeout.InfiniteTimeSpan, transport.Timeout);
        IJevDecisionClient client = provider.GetRequiredService<IJevDecisionClient>();
        Assert.Empty((await client.ListModelsAsync()).Models);
        Assert.Empty((await client.ListModelsAsync()).Models);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(0, handler.DisposalCount);
        Assert.Equal(ValidateOptionsResult.Success, provider.GetRequiredService<IValidateOptions<JevClientOptions>>().Validate(null, NativeFixtures.Options()));
        Assert.NotEmpty(provider.GetServices<IStartupValidator>());
    }

    [Fact]
    public void StartupValidationIsRegisteredAndRejectsMissingCredentials()
    {
        var services = new ServiceCollection();
        services.AddJev(_ => { });
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void NullDependenciesAreRejectedWithoutDisposingBorrowedResources()
    {
        using var http = new HttpClient(new RecordingHandler((_, _) => throw new InvalidOperationException("must not send")));
        Assert.Throws<ArgumentNullException>(() => new JevClient((JevClientOptions)null!));
        Assert.Throws<ArgumentNullException>(() => new JevClient(null!, NativeFixtures.Options()));
        Assert.Throws<ArgumentNullException>(() => new JevClient(http, null!));
        Assert.Throws<ArgumentNullException>(() => JevServiceCollectionExtensions.AddJev(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddJev(null!));
    }

    private sealed class ObservedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        internal ConcurrentBag<ObservedHttpClient> Clients { get; } = [];
        internal ConcurrentBag<string> Names { get; } = [];
        public HttpClient CreateClient(string name)
        {
            var client = new ObservedHttpClient(handler);
            Names.Add(name);
            Clients.Add(client);
            return client;
        }
    }

    private sealed class ObservedHttpClient(HttpMessageHandler handler) : HttpClient(handler, disposeHandler: false)
    {
        internal int DisposalCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposalCount++;
            base.Dispose(disposing);
        }
    }
}
