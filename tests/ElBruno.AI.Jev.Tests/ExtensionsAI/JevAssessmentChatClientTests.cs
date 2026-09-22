using System.Runtime.CompilerServices;
using ElBruno.AI.Jev.ExtensionsAI;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.Tests.ExtensionsAI;

public sealed class JevAssessmentChatClientTests
{
    [Fact]
    public async Task InputRejectionNeverCallsChatAndDiffersFromEvaluationFailure()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment(.1)) };
        var chat = new ChatClientStub();
        using var assessment = Create(chat, decisions);
        JevAssessmentRejectedException rejected = await Assert.ThrowsAsync<JevAssessmentRejectedException>(
            () => assessment.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(JevChatDecisionStage.InputAssessment, rejected.Stage);
        Assert.Equal(.1, rejected.Decision.GetAnswer(DecisionFixtures.SafeKey).Probability);
        Assert.Equal(0, chat.ResponseCalls);

        decisions.Evaluate = (_, _) => throw new HttpRequestException("offline failure");
        JevAssessmentFailedException failed = await Assert.ThrowsAsync<JevAssessmentFailedException>(
            () => assessment.GetResponseAsync(DecisionFixtures.Messages));
        Assert.IsType<HttpRequestException>(failed.InnerException);
        Assert.Equal(0, chat.ResponseCalls);
        Assert.Equal(0, chat.StreamCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectionAndAllowPredicateExceptionsFailClosed(bool projectionThrows)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var chat = new ChatClientStub();
        using var assessment = new JevAssessmentChatClient(chat, decisions, new JevAssessmentOptions
        {
            CreateRequest = DecisionFixtures.AssessmentRequest,
            TextSelector = projectionThrows ? _ => throw new InvalidOperationException("selector") : JevChatText.Extract,
            Allow = _ => throw new InvalidOperationException("predicate")
        });
        await Assert.ThrowsAsync<JevAssessmentFailedException>(() => assessment.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(projectionThrows ? 0 : 1, decisions.Calls);
        Assert.Equal(0, chat.ResponseCalls);
    }

    [Fact]
    public async Task InputOnlyStreamingForwardsWithoutBufferingOutput()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        bool disposed = false;
        var chat = new ChatClientStub { Stream = (_, _, token) => Stream(token) };
        using var assessment = Create(chat, decisions);
        await using IAsyncEnumerator<ChatResponseUpdate> enumerator = assessment.GetStreamingResponseAsync(DecisionFixtures.Messages).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("unchecked output", enumerator.Current.Text);
        Assert.Equal(JevChatDecisionStage.InputAssessment, Assert.Single(JevChatMetadata.GetDecisions(enumerator.Current.AdditionalProperties)).Stage);
        Assert.Equal("user: question", decisions.Request!.State.GetString());
        await enumerator.DisposeAsync();
        Assert.True(disposed);
        Assert.Equal(1, decisions.Calls);

        async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return new(ChatRole.Assistant, "unchecked output");
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public async Task OutputReviewDoesNotReleaseAnyUpdateUntilCompleteAssessmentApproves()
    {
        var evaluating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new DecisionClientStub
        {
            Evaluate = async (_, _) =>
            {
                evaluating.SetResult();
                await approve.Task;
                return DecisionFixtures.Assessment();
            }
        };
        var usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 25 };
        object raw = new();
        ChatResponseUpdate[] originals =
        [
            new(ChatRole.Assistant, "first ") { ModelId = "real-model", MessageId = "m", RawRepresentation = raw, AdditionalProperties = new() { ["provider"] = "kept" } },
            new(ChatRole.Assistant, "second") { MessageId = "m", ResponseId = "r", FinishReason = ChatFinishReason.Stop },
            new(null, [new UsageContent(usage)])
        ];
        bool streamComplete = false;
        var chat = new ChatClientStub { Stream = (_, _, token) => Stream(token) };
        using var assessment = Create(chat, decisions, JevAssessmentMode.BufferedOutputReview);
        await using IAsyncEnumerator<ChatResponseUpdate> enumerator = assessment.GetStreamingResponseAsync(DecisionFixtures.Messages).GetAsyncEnumerator();

        Task<bool> first = enumerator.MoveNextAsync().AsTask();
        await evaluating.Task;
        Assert.True(streamComplete);
        Assert.False(first.IsCompleted);
        Assert.Equal("assistant: first second", decisions.Request!.State.GetString());
        approve.SetResult();
        Assert.True(await first);
        var outputs = new List<ChatResponseUpdate> { enumerator.Current };
        while (await enumerator.MoveNextAsync()) outputs.Add(enumerator.Current);

        Assert.Equal(3, outputs.Count);
        Assert.Equal("first ", outputs[0].Text);
        Assert.Equal("second", outputs[1].Text);
        Assert.Same(raw, outputs[0].RawRepresentation);
        Assert.Same(usage, Assert.IsType<UsageContent>(Assert.Single(outputs[2].Contents)).Details);
        Assert.Equal("kept", outputs[0].AdditionalProperties!["provider"]);
        Assert.Equal("real-model", outputs[0].ModelId);
        Assert.Equal("r", outputs[1].ResponseId);
        Assert.Equal(ChatFinishReason.Stop, outputs[1].FinishReason);
        Assert.Equal(JevChatDecisionStage.OutputAssessment, Assert.Single(JevChatMetadata.GetDecisions(outputs[0].AdditionalProperties)).Stage);
        Assert.Empty(JevChatMetadata.GetDecisions(originals[0].AdditionalProperties));
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(1, chat.StreamCalls);

        async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (ChatResponseUpdate update in ChatClientStub.Updates(originals, token)) yield return update;
            streamComplete = true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedRejectionOrErrorLeaksNoOutput(bool error)
    {
        var decisions = new DecisionClientStub
        {
            Evaluate = (_, _) => error ? throw new HttpRequestException("unavailable") : Task.FromResult(DecisionFixtures.Assessment(.1))
        };
        var chat = new ChatClientStub
        {
            Stream = (_, _, token) => ChatClientStub.Updates([new(ChatRole.Assistant, "secret"), new(ChatRole.Assistant, "suffix")], token)
        };
        using var assessment = Create(chat, decisions, JevAssessmentMode.BufferedOutputReview);
        var received = new List<ChatResponseUpdate>();
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (ChatResponseUpdate update in assessment.GetStreamingResponseAsync(DecisionFixtures.Messages)) received.Add(update);
        });
        if (error) Assert.IsType<JevAssessmentFailedException>(failure);
        else Assert.IsType<JevAssessmentRejectedException>(failure);
        Assert.Empty(received);
        Assert.Equal(1, chat.StreamCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutputBufferBoundsFailClosedAndDisposeStream(bool characterLimit)
    {
        var decisions = new DecisionClientStub();
        bool disposed = false;
        var chat = new ChatClientStub { Stream = (_, _, token) => Stream(token) };
        using var assessment = new JevAssessmentChatClient(chat, decisions, new JevAssessmentOptions
        {
            CreateRequest = DecisionFixtures.AssessmentRequest,
            Allow = _ => true,
            Mode = JevAssessmentMode.BufferedOutputReview,
            MaxBufferedCharacters = characterLimit ? 3 : 100,
            MaxBufferedUpdates = characterLimit ? 10 : 1
        });
        var received = new List<ChatResponseUpdate>();
        await Assert.ThrowsAsync<JevAssessmentFailedException>(async () =>
        {
            await foreach (ChatResponseUpdate update in assessment.GetStreamingResponseAsync(DecisionFixtures.Messages)) received.Add(update);
        });
        Assert.Empty(received);
        Assert.Equal(0, decisions.Calls);
        Assert.True(disposed);

        async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return new(ChatRole.Assistant, "abcd");
                yield return new(ChatRole.Assistant, "ef");
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { disposed = true; }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringBufferingOrAssessmentLeaksNothing(bool duringDecision)
    {
        using var source = new CancellationTokenSource();
        var decisions = new DecisionClientStub
        {
            Evaluate = async (_, token) =>
            {
                source.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return DecisionFixtures.Assessment();
            }
        };
        bool disposed = false;
        var chat = new ChatClientStub { Stream = (_, _, token) => Stream(token) };
        using var assessment = Create(chat, decisions, JevAssessmentMode.BufferedOutputReview);
        int count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in assessment.GetStreamingResponseAsync(DecisionFixtures.Messages, cancellationToken: source.Token)) count++;
        });
        Assert.Equal(0, count);
        Assert.Equal(duringDecision ? 1 : 0, decisions.Calls);
        Assert.True(disposed);

        async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return new(ChatRole.Assistant, "unreviewed");
                if (!duringDecision)
                {
                    source.Cancel();
                    await Task.Delay(Timeout.Infinite, token);
                }
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public async Task ProviderFailureDuringBufferedStreamReleasesNothingAndDoesNotAssess()
    {
        var decisions = new DecisionClientStub();
        var chat = new ChatClientStub { Stream = (_, _, _) => Stream() };
        using var assessment = Create(chat, decisions, JevAssessmentMode.BufferedOutputReview);
        int delivered = 0;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in assessment.GetStreamingResponseAsync(DecisionFixtures.Messages)) delivered++;
        });
        Assert.Equal(0, delivered);
        Assert.Equal(0, decisions.Calls);

        static async IAsyncEnumerable<ChatResponseUpdate> Stream()
        {
            yield return new(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new IOException("stream failure");
        }
    }

    [Fact]
    public async Task DefaultNontextReviewIsExplicitAndCustomProjectionPreservesToolAndContentMetadata()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var call = new FunctionCallContent("call", "real-tool", new Dictionary<string, object?> { ["x"] = 1 })
        { AdditionalProperties = new() { ["content"] = true }, RawRepresentation = new object() };
        var message = new ChatMessage(ChatRole.Assistant, [call]) { AdditionalProperties = new() { ["message"] = 1 } };
        var response = new ChatResponse(message) { Usage = new() { InputTokenCount = 4 }, FinishReason = ChatFinishReason.ToolCalls };
        var chat = new ChatClientStub { Respond = (_, _, _) => Task.FromResult(response) };
        using var strict = Create(chat, decisions, JevAssessmentMode.BufferedOutputReview);
        JevAssessmentFailedException failure = await Assert.ThrowsAsync<JevAssessmentFailedException>(() => strict.GetResponseAsync(DecisionFixtures.Messages));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Equal(0, decisions.Calls);

        using var custom = new JevAssessmentChatClient(chat, decisions, new JevAssessmentOptions
        {
            CreateRequest = DecisionFixtures.AssessmentRequest,
            Allow = _ => true,
            Mode = JevAssessmentMode.BufferedOutputReview,
            TextSelector = history => Assert.IsType<FunctionCallContent>(Assert.Single(Assert.Single(history).Contents)).Name
        });
        ChatResponse approved = await custom.GetResponseAsync(DecisionFixtures.Messages);
        Assert.Same(call, Assert.Single(approved.Messages[0].Contents));
        Assert.Equal(1, approved.Messages[0].AdditionalProperties!["message"]);
        Assert.Same(response.Usage, approved.Usage);
        Assert.Equal(ChatFinishReason.ToolCalls, approved.FinishReason);
        Assert.Equal("real-tool", decisions.Request!.State.GetString());
        Assert.Null(response.AdditionalProperties);
    }

    [Fact]
    public async Task BufferSnapshotsReusedTextUpdatesBeforeAssessment()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var chat = new ChatClientStub { Stream = (_, _, _) => Stream() };
        using var assessment = Create(chat, decisions, JevAssessmentMode.BufferedOutputReview);
        List<ChatResponseUpdate> output = await DecisionFixtures.Collect(assessment.GetStreamingResponseAsync(DecisionFixtures.Messages));
        Assert.Equal("first", output[0].Text);
        Assert.Equal("second", output[1].Text);
        Assert.Equal("assistant: firstsecond", decisions.Request!.State.GetString());

        static async IAsyncEnumerable<ChatResponseUpdate> Stream()
        {
            var text = new TextContent("first");
            var update = new ChatResponseUpdate(ChatRole.Assistant, [text]);
            yield return update;
            await Task.Yield();
            text.Text = "second";
            yield return update;
            text.Text = "unreviewed";
        }
    }

    [Fact]
    public async Task GetServiceDisposalAndPreCanceledCallsRespectBorrowing()
    {
        var decisions = new DecisionClientStub();
        var chat = new ChatClientStub();
        var assessment = Create(chat, decisions);
        Assert.Same(assessment, assessment.GetService(typeof(JevAssessmentChatClient)));
        Assert.Same(decisions, assessment.GetService(typeof(IJevDecisionClient)));
        Assert.Same(chat.Metadata, assessment.GetService(typeof(ChatClientMetadata)));
        Assert.Null(assessment.GetService(typeof(IJevDecisionClient), "other"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assessment.GetResponseAsync(DecisionFixtures.Messages, cancellationToken: canceled.Token));
        Assert.Equal(0, decisions.Calls);
        Assert.Equal(0, chat.ResponseCalls);
        assessment.Dispose();
        Assert.Equal(0, chat.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => assessment.GetResponseAsync(DecisionFixtures.Messages));

        var owned = new JevAssessmentChatClient(chat, decisions,
            new JevAssessmentOptions { CreateRequest = DecisionFixtures.AssessmentRequest, Allow = _ => true, OwnsInnerClient = true });
        owned.Dispose();
        owned.Dispose();
        Assert.Equal(1, chat.Disposals);
    }

    [Theory]
    [InlineData(JevAssessmentMode.InputOnly)]
    [InlineData(JevAssessmentMode.BufferedOutputReview)]
    public async Task EmptyStreamsStillExposeDecisionMetadata(JevAssessmentMode mode)
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var chat = new ChatClientStub { Stream = (_, _, token) => ChatClientStub.Updates([], token) };
        using var assessment = Create(chat, decisions, mode);
        List<ChatResponseUpdate> output = await DecisionFixtures.Collect(assessment.GetStreamingResponseAsync(DecisionFixtures.Messages));
        Assert.Empty(Assert.Single(output).Contents);
        Assert.Single(JevChatMetadata.GetDecisions(output[0].AdditionalProperties));
    }

    [Fact]
    public async Task NonstreamingOutputReviewRejectsAndEnforcesCharacterLimit()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment(.1)) };
        var chat = new ChatClientStub();
        using var assessment = Create(chat, decisions, JevAssessmentMode.BufferedOutputReview);
        await Assert.ThrowsAsync<JevAssessmentRejectedException>(() => assessment.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal("assistant: answer", decisions.Request!.State.GetString());

        using var bounded = new JevAssessmentChatClient(chat, decisions, new JevAssessmentOptions
        {
            CreateRequest = DecisionFixtures.AssessmentRequest,
            Allow = _ => true,
            Mode = JevAssessmentMode.BufferedOutputReview,
            MaxBufferedCharacters = 1
        });
        await Assert.ThrowsAsync<JevAssessmentFailedException>(() => bounded.GetResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(2, chat.ResponseCalls);
    }

    [Fact]
    public async Task InputSnapshotAndClonedOptionsProtectCallerEvenWithMutatingSelector()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var messages = new List<ChatMessage> { new(ChatRole.User, "original") };
        var options = new ChatOptions { ModelId = "real", StopSequences = ["stop"], AdditionalProperties = new() { ["value"] = 1 } };
        var chat = new ChatClientStub
        {
            Respond = (forwarded, clonedOptions, _) =>
            {
                Assert.Equal("original", Assert.Single(forwarded).Text);
                Assert.NotSame(options, clonedOptions);
                Assert.Equal("real", clonedOptions!.ModelId);
                clonedOptions.ModelId = "changed";
                clonedOptions.StopSequences!.Clear();
                clonedOptions.AdditionalProperties!["value"] = 2;
                return Task.FromResult(new ChatResponse());
            }
        };
        using var assessment = new JevAssessmentChatClient(chat, decisions, new JevAssessmentOptions
        {
            CreateRequest = DecisionFixtures.AssessmentRequest,
            Allow = _ => true,
            TextSelector = input =>
            {
                string text = input[0].Text;
                ((TextContent)input[0].Contents[0]).Text = "mutated selector";
                input[0].Contents.Clear();
                return text;
            }
        });
        await assessment.GetResponseAsync(messages, options);
        Assert.Equal("original", messages[0].Text);
        Assert.Equal("original", decisions.Request!.State.GetString());
        Assert.Equal("real", options.ModelId);
        Assert.Single(options.StopSequences);
        Assert.Equal(1, options.AdditionalProperties["value"]);
    }

    private static JevAssessmentChatClient Create(ChatClientStub chat, DecisionClientStub decisions, JevAssessmentMode mode = JevAssessmentMode.InputOnly) =>
        new(chat, decisions, new JevAssessmentOptions
        {
            CreateRequest = DecisionFixtures.AssessmentRequest,
            Allow = response => response.GetAnswer(DecisionFixtures.SafeKey).Probability >= .8,
            Mode = mode
        });
}
