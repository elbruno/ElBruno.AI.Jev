using ElBruno.AI.Jev.ExtensionsAI;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.Tests.ExtensionsAI;

public sealed class JevCompositionEdgeTests
{
    [Theory]
    [InlineData(-.01)]
    [InlineData(1.01)]
    public void RoutingConfidenceRejectsOutOfRangeValues(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevRoutingChatClient(new DecisionClientStub(), Routes(),
            new JevRoutingOptions { Model = "pinned", Question = DecisionFixtures.RouteQuestion, MinimumConfidence = confidence }));
    }

    [Fact]
    public void RoutingRequiresRoutesAndDefinedPolicies()
    {
        var decisions = new DecisionClientStub();
        Assert.Throws<ArgumentException>(() => new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient>(), RoutingOptions()));
        Assert.Throws<ArgumentException>(() => new JevRoutingChatClient(decisions, Routes(),
            new JevRoutingOptions { Model = "pinned", Question = DecisionFixtures.RouteQuestion, UnknownRoutePolicy = (JevRouteFailurePolicy)42 }));
        Assert.Throws<ArgumentException>(() => new JevRoutingChatClient(decisions, Routes(),
            new JevRoutingOptions { Model = "pinned", Question = DecisionFixtures.RouteQuestion, LowConfidencePolicy = (JevRouteFailurePolicy)42 }));
        Assert.Equal(0, decisions.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public void LowConfidenceFallbackAloneRequiresRegisteredFallback(string? fallback)
    {
        Assert.Throws<ArgumentException>(() => new JevRoutingChatClient(new DecisionClientStub(), Routes(),
            new JevRoutingOptions
            {
                Model = "pinned",
                Question = DecisionFixtures.RouteQuestion,
                LowConfidencePolicy = JevRouteFailurePolicy.UseFallback,
                FallbackRoute = fallback
            }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentFallbackPoliciesDoNotEnableEachOther(bool unknownFallback)
    {
        var decisions = new DecisionClientStub
        {
            Evaluate = (_, _) => Task.FromResult(unknownFallback
                ? DecisionFixtures.Choice(confidence: .1) : DecisionFixtures.Choice("unknown"))
        };
        var fallback = new ChatClientStub();
        using var router = new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient> { ["Fast"] = fallback },
            new JevRoutingOptions
            {
                Model = "pinned",
                Question = DecisionFixtures.RouteQuestion,
                MinimumConfidence = .8,
                UnknownRoutePolicy = unknownFallback ? JevRouteFailurePolicy.UseFallback : JevRouteFailurePolicy.Throw,
                LowConfidencePolicy = unknownFallback ? JevRouteFailurePolicy.Throw : JevRouteFailurePolicy.UseFallback,
                FallbackRoute = "Fast"
            });
        JevRoutingException error = await Assert.ThrowsAsync<JevRoutingException>(() => router.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(unknownFallback ? JevRoutingFailureReason.LowConfidence : JevRoutingFailureReason.UnknownRoute, error.Reason);
        Assert.Equal(0, fallback.ResponseCalls);
    }

    [Fact]
    public async Task LowConfidenceOnlyFallbackWorksWithoutEnablingUnknownFallback()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Choice(confidence: .1)) };
        var primary = new ChatClientStub();
        var fallback = new ChatClientStub();
        using var router = new JevRoutingChatClient(decisions,
            new Dictionary<string, IChatClient> { ["Fast"] = primary, ["Deep"] = fallback },
            new JevRoutingOptions
            {
                Model = "pinned",
                Question = DecisionFixtures.RouteQuestion,
                MinimumConfidence = .8,
                LowConfidencePolicy = JevRouteFailurePolicy.UseFallback,
                FallbackRoute = "Deep"
            });
        ChatResponse response = await router.GetResponseAsync(DecisionFixtures.Messages);
        Assert.Equal(0, primary.ResponseCalls);
        Assert.Equal(1, fallback.ResponseCalls);
        Assert.True(Assert.Single(JevChatMetadata.GetDecisions(response.AdditionalProperties)).UsedFallback);
    }

    [Fact]
    public void DisposalAttemptsAllOwnedClientsAndAggregatesFailuresOnlyOnce()
    {
        var first = new ChatClientStub { DisposalError = new IOException("first failed") };
        var second = new ChatClientStub { DisposalError = new IOException("second failed") };
        var healthy = new ChatClientStub();
        var router = new JevRoutingChatClient(new DecisionClientStub(),
            new Dictionary<string, IChatClient> { ["Fast"] = first, ["Alias"] = first, ["Deep"] = second, ["Other"] = healthy },
            new JevRoutingOptions { Model = "pinned", Question = DecisionFixtures.RouteQuestion, OwnsChatClients = true });

        AggregateException error = Assert.Throws<AggregateException>(router.Dispose);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(1, healthy.Disposals);
        router.Dispose();
        Assert.Equal(1, healthy.Disposals);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void AssessmentBufferLimitsMustBePositive(int updates, int characters)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevAssessmentChatClient(new ChatClientStub(), new DecisionClientStub(),
            new JevAssessmentOptions
            {
                CreateRequest = DecisionFixtures.AssessmentRequest,
                Allow = _ => true,
                MaxBufferedUpdates = updates,
                MaxBufferedCharacters = characters
            }));
    }

    [Fact]
    public void UndefinedAssessmentModeFailsBeforeAnyCall()
    {
        var decisions = new DecisionClientStub();
        Assert.Throws<ArgumentException>(() => new JevAssessmentChatClient(new ChatClientStub(), decisions,
            new JevAssessmentOptions { CreateRequest = DecisionFixtures.AssessmentRequest, Allow = _ => true, Mode = (JevAssessmentMode)42 }));
        Assert.Equal(0, decisions.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullAssessmentRequestOrResponseFailsClosed(bool nullRequest)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult<JevDecisionResponse>(null!) };
        var chat = new ChatClientStub();
        using var assessor = new JevAssessmentChatClient(chat, decisions, new JevAssessmentOptions
        {
            CreateRequest = nullRequest ? _ => null! : DecisionFixtures.AssessmentRequest,
            Allow = _ => true
        });
        JevAssessmentFailedException error = await Assert.ThrowsAsync<JevAssessmentFailedException>(
            () => assessor.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(JevChatDecisionStage.InputAssessment, error.Stage);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(nullRequest ? 0 : 1, decisions.Calls);
        Assert.Equal(0, chat.ResponseCalls);
    }

    [Fact]
    public async Task NullRoutingResponseIsDecisionFailureNotUnknownRouteFallback()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult<JevDecisionResponse>(null!) };
        var chat = new ChatClientStub();
        using var router = new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient> { ["Fast"] = chat },
            new JevRoutingOptions
            {
                Model = "pinned",
                Question = DecisionFixtures.RouteQuestion,
                UnknownRoutePolicy = JevRouteFailurePolicy.UseFallback,
                FallbackRoute = "Fast"
            });
        JevRoutingException error = await Assert.ThrowsAsync<JevRoutingException>(() => router.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(JevRoutingFailureReason.DecisionFailed, error.Reason);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(0, chat.ResponseCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullToolRequestOrResponseIsExplicit(bool nullRequest)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult<JevDecisionResponse>(null!) };
        var tool = new JevDecisionFunction(decisions, "assess", "Assessment.",
            nullRequest ? _ => null! : DecisionFixtures.AssessmentRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(new() { ["state"] = "text" }).AsTask());
        Assert.Equal(nullRequest ? 0 : 1, decisions.Calls);
    }

    [Fact]
    public void ToolNameBoundaryAndSchemaAreConsistent()
    {
        var decisions = new DecisionClientStub();
        var tool = new JevDecisionFunction(decisions, new string('x', 64), "Assessment.", DecisionFixtures.AssessmentRequest);
        Assert.Equal(64, tool.Name.Length);
        Assert.Throws<ArgumentException>(() => new JevDecisionFunction(decisions, new string('x', 65), "Assessment.", DecisionFixtures.AssessmentRequest));
        Assert.Throws<ArgumentException>(() => new JevDecisionFunction(decisions, "assess.text", "Assessment.", DecisionFixtures.AssessmentRequest));
        Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
        Assert.Equal("string", tool.JsonSchema.GetProperty("properties").GetProperty("state").GetProperty("type").GetString());
        Assert.Equal("object", tool.ReturnJsonSchema!.Value.GetProperty("type").GetString());
        Assert.Equal(3, tool.ReturnJsonSchema.Value.GetProperty("properties").GetProperty("answers").GetProperty("additionalProperties").GetProperty("oneOf").GetArrayLength());
    }

    [Fact]
    public async Task ReservedMetadataCollisionIsExplicitAndDoesNotOverwriteProviderData()
    {
        var original = new AdditionalPropertiesDictionary { [JevChatMetadata.PropertyName] = "another component" };
        Assert.Throws<InvalidOperationException>(() => JevChatMetadata.GetDecisions(original));
        var chat = new ChatClientStub
        {
            Respond = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "text")) { AdditionalProperties = original })
        };
        using var router = new JevRoutingChatClient(new DecisionClientStub(),
            new Dictionary<string, IChatClient> { ["Fast"] = chat }, RoutingOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal("another component", original[JevChatMetadata.PropertyName]);
    }

    [Fact]
    public async Task TextSnapshotsPreserveAnnotationsAndRawMetadataWithoutSharingContainers()
    {
        object raw = new();
        var content = new TextContent("text")
        {
            Annotations = [],
            RawRepresentation = raw,
            AdditionalProperties = new() { ["content"] = "kept" }
        };
        var message = new ChatMessage(ChatRole.User, [content]);
        var chat = new ChatClientStub
        {
            Respond = (messages, _, _) =>
            {
                TextContent copy = Assert.IsType<TextContent>(Assert.Single(Assert.Single(messages).Contents));
                Assert.NotSame(content, copy);
                Assert.NotSame(content.Annotations, copy.Annotations);
                Assert.NotSame(content.AdditionalProperties, copy.AdditionalProperties);
                Assert.Same(raw, copy.RawRepresentation);
                Assert.Equal("kept", copy.AdditionalProperties!["content"]);
                copy.AdditionalProperties["content"] = "mutated";
                return Task.FromResult(new ChatResponse());
            }
        };
        using var router = new JevRoutingChatClient(new DecisionClientStub(),
            new Dictionary<string, IChatClient> { ["Fast"] = chat }, RoutingOptions());
        await router.GetResponseAsync([message]);
        Assert.Equal("kept", content.AdditionalProperties["content"]);
    }

    [Fact]
    public async Task ConcurrentAssessmentsKeepDecisionAttributionIsolated()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var decisions = new DecisionClientStub
        {
            Evaluate = async (request, _) =>
            {
                if (Interlocked.Increment(ref entered) == 16) release.SetResult();
                await release.Task;
                return new JevDecisionResponse("resolved",
                    new Dictionary<string, JevAnswer> { ["allowed"] = new JevNoulAnswer(.9) },
                    requestId: request.State.GetString());
            }
        };
        using var assessment = new JevAssessmentChatClient(new ChatClientStub(), decisions,
            new JevAssessmentOptions
            {
                CreateRequest = DecisionFixtures.AssessmentRequest,
                Allow = response => response.GetAnswer(DecisionFixtures.SafeKey).Probability > .5
            });
        ChatResponse[] responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            assessment.GetResponseAsync([new(ChatRole.User, $"request-{index}")])));
        for (int index = 0; index < responses.Length; index++)
        {
            Assert.Equal($"user: request-{index}",
                Assert.Single(JevChatMetadata.GetDecisions(responses[index].AdditionalProperties)).Decision.RequestId);
        }
    }

    [Fact]
    public void ServiceDiscoveryRejectsNullTypeAndDoesNotPassUnregisteredKeysToProviders()
    {
        using var router = new JevRoutingChatClient(new DecisionClientStub(), Routes(), RoutingOptions());
        using var assessor = new JevAssessmentChatClient(new ChatClientStub(), new DecisionClientStub(),
            new JevAssessmentOptions { CreateRequest = DecisionFixtures.AssessmentRequest, Allow = _ => true });
        Assert.Throws<ArgumentNullException>(() => router.GetService(null!));
        Assert.Throws<ArgumentNullException>(() => assessor.GetService(null!));
        Assert.Null(router.GetService(typeof(ChatClientMetadata), new object()));
        Assert.Null(assessor.GetService(typeof(ChatClientMetadata), "unknown"));
    }

    [Theory]
    [InlineData(JevAssessmentMode.InputOnly)]
    [InlineData(JevAssessmentMode.BufferedOutputReview)]
    public async Task AssessmentStreamingSnapshotsOptionsBeforeEnumeration(JevAssessmentMode mode)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var options = new ChatOptions { ModelId = "before", Temperature = .7f, StopSequences = ["end"] };
        var messages = new List<ChatMessage> { new(ChatRole.User, "original") };
        var chat = new ChatClientStub
        {
            Stream = (forwarded, forwardedOptions, token) =>
            {
                Assert.Equal("original", Assert.Single(forwarded).Text);
                Assert.NotSame(options, forwardedOptions);
                Assert.Equal("before", forwardedOptions!.ModelId);
                Assert.Equal(.7f, forwardedOptions.Temperature);
                forwardedOptions.StopSequences!.Clear();
                return ChatClientStub.Updates([new(ChatRole.Assistant, "approved")], token);
            }
        };
        using var assessor = new JevAssessmentChatClient(chat, decisions,
            new JevAssessmentOptions { CreateRequest = DecisionFixtures.AssessmentRequest, Allow = _ => true, Mode = mode });

        IAsyncEnumerable<ChatResponseUpdate> stream = assessor.GetStreamingResponseAsync(messages, options);
        messages.Clear();
        options.ModelId = "after";
        List<ChatResponseUpdate> updates = await DecisionFixtures.Collect(stream);

        Assert.Equal("approved", Assert.Single(updates).Text);
        Assert.Single(options.StopSequences);
        Assert.Single(JevChatMetadata.GetDecisions(updates[0].AdditionalProperties));
    }

    private static Dictionary<string, IChatClient> Routes() => new() { ["Fast"] = new ChatClientStub() };
    private static JevRoutingOptions RoutingOptions() => new() { Model = "pinned", Question = DecisionFixtures.RouteQuestion };
}
