using System.Runtime.CompilerServices;
using ElBruno.AI.Jev.ExtensionsAI;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.Tests.ExtensionsAI;

public sealed class JevRoutingChatClientTests
{
    [Fact]
    public async Task PreservesOptionsResponseAndSeparatelyAttributesDecision()
    {
        var decision = new DecisionClientStub();
        object raw = new();
        var usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 };
        var content = new FunctionCallContent("call", "real-tool", new Dictionary<string, object?> { ["x"] = 1 });
        var providerResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, [content]))
        {
            ModelId = "real-model",
            ResponseId = "chat-id",
            ConversationId = "conversation",
            FinishReason = ChatFinishReason.ToolCalls,
            CreatedAt = DateTimeOffset.UtcNow,
            Usage = usage,
            RawRepresentation = raw,
            AdditionalProperties = new() { ["provider"] = "kept" }
        };
        var options = new ChatOptions
        {
            ModelId = "requested-chat-model",
            Temperature = .2f,
            TopP = .8f,
            MaxOutputTokens = 123,
            StopSequences = ["END"],
            ResponseFormat = ChatResponseFormat.Json,
            Tools = [new JevDecisionFunction(decision, "check", "Check.", DecisionFixtures.AssessmentRequest)],
            AdditionalProperties = new() { ["custom"] = "value" }
        };
        var chat = new ChatClientStub
        {
            Respond = (_, forwarded, _) =>
            {
                Assert.NotSame(options, forwarded);
                Assert.Equal(options.ModelId, forwarded!.ModelId);
                Assert.Equal(options.Temperature, forwarded.Temperature);
                Assert.Equal(options.TopP, forwarded.TopP);
                Assert.Equal(options.MaxOutputTokens, forwarded.MaxOutputTokens);
                Assert.Same(options.ResponseFormat, forwarded.ResponseFormat);
                Assert.Same(options.Tools[0], Assert.Single(forwarded.Tools!));
                forwarded.StopSequences!.Add("changed");
                forwarded.AdditionalProperties!["custom"] = "mutated";
                return Task.FromResult(providerResponse);
            }
        };
        using var router = Create(decision, chat);
        ChatResponse response = await router.GetResponseAsync(DecisionFixtures.Messages, options);

        Assert.Equal("jev-pinned", decision.Request!.Model);
        Assert.Equal("user: question", decision.Request.State.GetString());
        Assert.Equal("requested-chat-model", options.ModelId);
        Assert.Single(options.StopSequences);
        Assert.Equal("value", options.AdditionalProperties["custom"]);
        Assert.NotSame(providerResponse, response);
        Assert.Same(content, Assert.Single(response.Messages[0].Contents));
        Assert.Same(raw, response.RawRepresentation);
        Assert.Same(usage, response.Usage);
        Assert.Equal(providerResponse.ModelId, response.ModelId);
        Assert.Equal(providerResponse.ResponseId, response.ResponseId);
        Assert.Equal(providerResponse.ConversationId, response.ConversationId);
        Assert.Equal(providerResponse.CreatedAt, response.CreatedAt);
        Assert.Equal(providerResponse.FinishReason, response.FinishReason);
        Assert.Equal("kept", response.AdditionalProperties!["provider"]);
        JevChatDecisionMetadata metadata = Assert.Single(JevChatMetadata.GetDecisions(response.AdditionalProperties));
        Assert.Equal("Fast", metadata.SelectedRoute);
        Assert.Equal("decision-request", metadata.Decision.RequestId);
        Assert.Equal(17, metadata.Usage.InputTokenCount);
        Assert.Null(metadata.Usage.TotalTokenCount);
        Assert.False(metadata.UsedFallback);
        Assert.False(providerResponse.AdditionalProperties!.ContainsKey(JevChatMetadata.PropertyName));
    }

    [Theory]
    [InlineData("missing", .9, JevRoutingFailureReason.UnknownRoute)]
    [InlineData("fast", .9, JevRoutingFailureReason.UnknownRoute)]
    [InlineData("Deep", .9, JevRoutingFailureReason.UnknownRoute)]
    [InlineData("Fast", .4, JevRoutingFailureReason.LowConfidence)]
    public async Task InvalidRouteOrConfidenceNeverInvokesChat(string label, double confidence, JevRoutingFailureReason reason)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Choice(label, confidence)) };
        var chat = new ChatClientStub();
        using var router = Create(decisions, chat, minimumConfidence: .8);
        JevRoutingException error = await Assert.ThrowsAsync<JevRoutingException>(() => router.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(0, chat.ResponseCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrWrongAnswerIsExplicit(bool missing)
    {
        var decisions = new DecisionClientStub
        {
            Evaluate = (_, _) => Task.FromResult(new JevDecisionResponse("model",
                missing ? new Dictionary<string, JevAnswer>() : new() { ["route"] = new JevNoulAnswer(.9) }))
        };
        var chat = new ChatClientStub();
        using var router = Create(decisions, chat);
        JevRoutingException error = await Assert.ThrowsAsync<JevRoutingException>(() => router.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(JevRoutingFailureReason.InvalidAnswer, error.Reason);
        Assert.Equal(0, chat.ResponseCalls);
    }

    [Theory]
    [InlineData("unregistered", .95)]
    [InlineData("Fast", .2)]
    public async Task OnlyExplicitFallbackCanSelectAnotherClient(string label, double confidence)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Choice(label, confidence)) };
        var fast = new ChatClientStub();
        var fallback = new ChatClientStub();
        using var router = new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient> { ["Fast"] = fast, ["Deep"] = fallback },
            new JevRoutingOptions
            {
                Question = DecisionFixtures.RouteQuestion,
                Model = "jev-pinned",
                MinimumConfidence = .8,
                UnknownRoutePolicy = JevRouteFailurePolicy.UseFallback,
                LowConfidencePolicy = JevRouteFailurePolicy.UseFallback,
                FallbackRoute = "Deep"
            });
        ChatResponse response = await router.GetResponseAsync(DecisionFixtures.Messages);
        Assert.Equal(0, fast.ResponseCalls);
        Assert.Equal(1, fallback.ResponseCalls);
        JevChatDecisionMetadata metadata = Assert.Single(JevChatMetadata.GetDecisions(response.AdditionalProperties));
        Assert.True(metadata.UsedFallback);
        Assert.Equal("Deep", metadata.SelectedRoute);
    }

    [Fact]
    public async Task StreamingRoutesOnceAndDisposesInnerEnumeratorOnEarlyExit()
    {
        var decisions = new DecisionClientStub();
        bool disposed = false;
        var original = new ChatResponseUpdate(ChatRole.Assistant, "first") { AdditionalProperties = new() { ["inner"] = 1 }, ModelId = "actual" };
        var chat = new ChatClientStub { Stream = (_, _, token) => Stream(token) };
        using var router = Create(decisions, chat);

        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync(DecisionFixtures.Messages))
        {
            Assert.Equal("first", update.Text);
            Assert.Equal("actual", update.ModelId);
            Assert.Equal(1, update.AdditionalProperties!["inner"]);
            Assert.Single(JevChatMetadata.GetDecisions(update.AdditionalProperties));
            break;
        }

        Assert.True(disposed);
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(1, chat.StreamCalls);
        Assert.False(original.AdditionalProperties!.ContainsKey(JevChatMetadata.PropertyName));

        async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return original;
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public async Task StreamingCancellationPropagatesWithoutReplay()
    {
        using var source = new CancellationTokenSource();
        var decisions = new DecisionClientStub();
        var chat = new ChatClientStub { Stream = (_, _, token) => Stream(token) };
        using var router = Create(decisions, chat);
        int delivered = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in router.GetStreamingResponseAsync(DecisionFixtures.Messages).WithCancellation(source.Token)) delivered++;
        });
        Assert.Equal(1, delivered);
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(1, chat.StreamCalls);
        Assert.Equal(source.Token, decisions.Token);

        async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token)
        {
            yield return new(ChatRole.Assistant, "first");
            source.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    [Fact]
    public async Task InputsAndRegisteredRoutesAreSnapshottedBeforeAwait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new DecisionClientStub
        {
            Evaluate = async (_, _) => { entered.SetResult(); await release.Task; return DecisionFixtures.Choice(); }
        };
        var message = new ChatMessage(ChatRole.User, "original");
        var messages = new List<ChatMessage> { message };
        var options = new ChatOptions { ModelId = "original-model" };
        var chat = new ChatClientStub
        {
            Respond = (forwarded, forwardedOptions, _) =>
            {
                Assert.Equal("original", Assert.Single(forwarded).Text);
                Assert.Equal("original-model", forwardedOptions!.ModelId);
                return Task.FromResult(new ChatResponse());
            }
        };
        var routes = new Dictionary<string, IChatClient> { ["Fast"] = chat };
        using var router = new JevRoutingChatClient(decisions, routes, Options());
        Task<ChatResponse> pending = router.GetResponseAsync(messages, options);
        await entered.Task;
        ((TextContent)message.Contents[0]).Text = "changed";
        messages.Clear();
        options.ModelId = "changed-model";
        routes.Clear();
        release.SetResult();
        await pending;
        Assert.Equal(1, chat.ResponseCalls);
    }

    [Fact]
    public async Task NonTextRequiresAnExplicitSelectorAndNoChatOptionsAreLost()
    {
        var decisions = new DecisionClientStub();
        var chat = new ChatClientStub();
        ChatMessage[] messages = [new(ChatRole.Tool, [new FunctionResultContent("call", "result")]), new(ChatRole.User, "text")];
        using var strict = Create(decisions, chat);
        JevRoutingException error = await Assert.ThrowsAsync<JevRoutingException>(() => strict.GetResponseAsync(messages));
        Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.Equal(0, decisions.Calls);
        using var custom = new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient> { ["Fast"] = chat },
            new JevRoutingOptions { Question = DecisionFixtures.RouteQuestion, Model = "pinned", TextSelector = history => history.Last().Text });
        await custom.GetResponseAsync(messages);
        Assert.Equal("text", decisions.Request!.State.GetString());
    }

    [Fact]
    public async Task ServicesOwnershipAndDisposedBehaviorAreExplicit()
    {
        var decisions = new DecisionClientStub();
        var chat = new ChatClientStub();
        var borrowed = Create(decisions, chat);
        Assert.Same(borrowed, borrowed.GetService(typeof(IChatClient)));
        Assert.Same(decisions, borrowed.GetService(typeof(IJevDecisionClient)));
        Assert.Same(chat, borrowed.GetService(typeof(IChatClient), "Fast"));
        Assert.Same(chat.Metadata, borrowed.GetService(typeof(ChatClientMetadata), "Fast"));
        Assert.Null(borrowed.GetService(typeof(ChatClientMetadata)));
        Assert.Null(borrowed.GetService(typeof(IChatClient), "unknown"));
        borrowed.Dispose();
        Assert.Equal(0, chat.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => borrowed.GetResponseAsync(DecisionFixtures.Messages));

        var owned = new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient> { ["Fast"] = chat, ["Deep"] = chat },
            new JevRoutingOptions { Question = DecisionFixtures.RouteQuestion, Model = "pinned", OwnsChatClients = true });
        owned.Dispose();
        owned.Dispose();
        Assert.Equal(1, chat.Disposals);
    }

    [Fact]
    public void InvalidConfigurationFailsWithoutNetwork()
    {
        var routes = new Dictionary<string, IChatClient> { ["Fast"] = new ChatClientStub() };
        var decisions = new DecisionClientStub();
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevRoutingChatClient(decisions, routes,
            new JevRoutingOptions { Question = DecisionFixtures.RouteQuestion, Model = "pinned", MinimumConfidence = double.NaN }));
        Assert.Throws<ArgumentException>(() => new JevRoutingChatClient(decisions, routes,
            new JevRoutingOptions { Question = DecisionFixtures.RouteQuestion, Model = "pinned", UnknownRoutePolicy = JevRouteFailurePolicy.UseFallback }));
        Assert.Equal(0, decisions.Calls);
    }

    [Fact]
    public async Task DecisionCancellationAndFailureNeverTriggerConfiguredFallback()
    {
        using var source = new CancellationTokenSource();
        var decisions = new DecisionClientStub
        {
            Evaluate = async (_, token) =>
            {
                source.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return DecisionFixtures.Choice();
            }
        };
        var chat = new ChatClientStub();
        using var router = new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient> { ["Fast"] = chat },
            new JevRoutingOptions
            {
                Question = DecisionFixtures.RouteQuestion,
                Model = "pinned",
                UnknownRoutePolicy = JevRouteFailurePolicy.UseFallback,
                FallbackRoute = "Fast"
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.GetResponseAsync(DecisionFixtures.Messages, cancellationToken: source.Token));
        Assert.Equal(0, chat.ResponseCalls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.GetResponseAsync(DecisionFixtures.Messages, cancellationToken: source.Token));
        Assert.Equal(1, decisions.Calls);
        decisions.Evaluate = (_, _) => throw new HttpRequestException("offline");
        JevRoutingException error = await Assert.ThrowsAsync<JevRoutingException>(() => router.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(JevRoutingFailureReason.DecisionFailed, error.Reason);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(0, chat.ResponseCalls);
    }

    [Fact]
    public async Task FailedChatStreamIsNotRetriedOrReroutedAfterPartialOutput()
    {
        var decisions = new DecisionClientStub();
        var chat = new ChatClientStub { Stream = (_, _, _) => Stream() };
        using var router = Create(decisions, chat);
        int received = 0;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in router.GetStreamingResponseAsync(DecisionFixtures.Messages)) received++;
        });
        Assert.Equal(1, received);
        Assert.Equal(1, chat.StreamCalls);
        Assert.Equal(1, decisions.Calls);

        static async IAsyncEnumerable<ChatResponseUpdate> Stream()
        {
            yield return new(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new IOException("chat failure");
        }
    }

    [Theory]
    [InlineData(.8, .8)]
    [InlineData(null, .01)]
    public async Task ExplicitThresholdBoundaryAndDisabledThresholdAreRespected(double? minimum, double confidence)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Choice(confidence: confidence)) };
        var chat = new ChatClientStub();
        using var router = Create(decisions, chat, minimum);
        await router.GetResponseAsync(DecisionFixtures.Messages);
        Assert.Equal(1, chat.ResponseCalls);
    }

    [Fact]
    public async Task StreamCallSnapshotsSingleUseEnumerableAndOptionsBeforeEnumeration()
    {
        int enumerations = 0;
        var messages = new List<ChatMessage> { new(ChatRole.User, "initial") };
        var options = new ChatOptions { ModelId = "initial-model" };
        var decisions = new DecisionClientStub();
        var chat = new ChatClientStub
        {
            Stream = (forwarded, forwardedOptions, token) =>
            {
                Assert.Equal("initial", Assert.Single(forwarded).Text);
                Assert.Equal("initial-model", forwardedOptions!.ModelId);
                return ChatClientStub.Updates([], token);
            }
        };
        using var router = Create(decisions, chat);
        IAsyncEnumerable<ChatResponseUpdate> stream = router.GetStreamingResponseAsync(SingleUse(), options);
        messages.Clear();
        options.ModelId = "changed";
        List<ChatResponseUpdate> output = await DecisionFixtures.Collect(stream);
        Assert.Equal(1, enumerations);
        Assert.Single(JevChatMetadata.GetDecisions(Assert.Single(output).AdditionalProperties));

        IEnumerable<ChatMessage> SingleUse()
        {
            if (++enumerations != 1) throw new InvalidOperationException("enumerated twice");
            foreach (ChatMessage message in messages) yield return message;
        }
    }

    private static JevRoutingChatClient Create(DecisionClientStub decisions, ChatClientStub chat, double? minimumConfidence = null) =>
        new(decisions, new Dictionary<string, IChatClient> { ["Fast"] = chat }, Options(minimumConfidence));

    private static JevRoutingOptions Options(double? minimumConfidence = null) =>
        new() { Question = DecisionFixtures.RouteQuestion, Model = "jev-pinned", MinimumConfidence = minimumConfidence };
}
