using System.Collections.Concurrent;
using System.Diagnostics;
using ElBruno.AI.Jev.ExtensionsAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElBruno.AI.Jev.Tests.ExtensionsAI;

public sealed class JevPipelineTests
{
    [Fact]
    public async Task AssessmentAroundCachedChatInteroperatesWithLoggingAndTelemetry()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var chat = new ChatClientStub();
        var activities = new ConcurrentBag<Activity>();
        string source = $"Jev.Tests.{Guid.NewGuid()}";
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);
        using IChatClient pipeline = chat.AsBuilder()
            .UseOpenTelemetry(NullLoggerFactory.Instance, source)
            .UseLogging(NullLoggerFactory.Instance)
            .Use(inner => new JevAssessmentChatClient(inner, decisions, new JevAssessmentOptions
            {
                CreateRequest = DecisionFixtures.AssessmentRequest,
                Allow = _ => true,
                OwnsInnerClient = true
            }))
            .UseDistributedCache(new MemoryCacheStub())
            .Build();

        ChatResponse first = await pipeline.GetResponseAsync(DecisionFixtures.Messages);
        ChatResponse second = await pipeline.GetResponseAsync(DecisionFixtures.Messages);

        Assert.Equal("answer", first.Text);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(1, chat.ResponseCalls);
        Assert.Equal(2, decisions.Calls);
        Assert.Single(JevChatMetadata.GetDecisions(second.AdditionalProperties));
        Assert.NotEmpty(activities);
        Assert.Same(chat.Metadata, pipeline.GetService(typeof(ChatClientMetadata)));
    }

    [Fact]
    public async Task RouterAndAssessmentMetadataComposeWithoutOverwritingProviderProperties()
    {
        var routingDecisions = new DecisionClientStub();
        var assessments = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var chat = new ChatClientStub();
        using var assessor = new JevAssessmentChatClient(chat, assessments, new JevAssessmentOptions
        {
            CreateRequest = DecisionFixtures.AssessmentRequest,
            Allow = _ => true
        });
        using var router = new JevRoutingChatClient(routingDecisions, new Dictionary<string, IChatClient> { ["Fast"] = assessor },
            new JevRoutingOptions { Question = DecisionFixtures.RouteQuestion, Model = "jev-pinned" });
        ChatResponse response = await router.GetResponseAsync(DecisionFixtures.Messages);
        Assert.Equal(new[] { JevChatDecisionStage.InputAssessment, JevChatDecisionStage.Routing },
            JevChatMetadata.GetDecisions(response.AdditionalProperties).Select(decision => decision.Stage));
        List<ChatResponseUpdate> updates = await DecisionFixtures.Collect(router.GetStreamingResponseAsync(DecisionFixtures.Messages));
        Assert.Equal(2, JevChatMetadata.GetDecisions(updates[0].AdditionalProperties).Count);
        Assert.Equal(2, routingDecisions.Calls);
        Assert.Equal(2, assessments.Calls);
    }

    [Fact]
    public async Task ConcurrentCallsKeepRouteDecisionMetadataIsolated()
    {
        var decisions = new DecisionClientStub
        {
            Evaluate = async (request, _) =>
            {
                await Task.Yield();
                return DecisionFixtures.Choice(request.State.GetString()!.Contains("complex", StringComparison.Ordinal) ? "Deep" : "Fast");
            }
        };
        var fast = new ChatClientStub { Respond = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fast"))) };
        var deep = new ChatClientStub { Respond = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "deep"))) };
        using var router = new JevRoutingChatClient(decisions, new Dictionary<string, IChatClient> { ["Fast"] = fast, ["Deep"] = deep },
            new JevRoutingOptions { Question = DecisionFixtures.RouteQuestion, Model = "jev-pinned" });
        ChatResponse[] responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            router.GetResponseAsync([new(ChatRole.User, index % 2 == 0 ? "simple" : "complex")])));
        for (int index = 0; index < responses.Length; index++)
        {
            Assert.Equal(index % 2 == 0 ? "fast" : "deep", responses[index].Text);
            Assert.Equal(index % 2 == 0 ? "Fast" : "Deep",
                Assert.Single(JevChatMetadata.GetDecisions(responses[index].AdditionalProperties)).SelectedRoute);
        }
    }

    private sealed class MemoryCacheStub : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _items = new();
        public byte[]? Get(string key) => _items.TryGetValue(key, out byte[]? value) ? value : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _items.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _items[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        { Set(key, value, options); return Task.CompletedTask; }
    }
}
