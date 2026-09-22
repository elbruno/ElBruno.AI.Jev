using System.Text.Json;
using ElBruno.AI.Jev.ExtensionsAI;
using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.Tests.ExtensionsAI;

public sealed class JevDecisionFunctionTests
{
    [Fact]
    public async Task InvocationHasExplicitSchemaAndSerializableCompleteAnswers()
    {
        var response = new JevDecisionResponse("resolved", new Dictionary<string, JevAnswer>
        {
            ["category"] = new JevChoiceAnswer("Exact Label", new Dictionary<string, double> { ["Exact Label"] = .8, ["other"] = .2 }, .7),
            ["quality"] = new JevScoreAnswer(1.37,
                new Dictionary<string, double> { ["0"] = .13, ["1"] = .37, ["2"] = .5 },
                new Dictionary<string, JsonElement> { ["0"] = JsonSerializer.SerializeToElement(new { label = "low" }), ["1"] = JsonSerializer.SerializeToElement("middle"), ["2"] = JsonSerializer.SerializeToElement("high") }, .6),
            ["allowed"] = new JevNoulAnswer(.75, JsonSerializer.SerializeToElement(new { probability = .75, future = true }))
        }, new JevUsage(9, null), "req", JsonSerializer.SerializeToElement(new { future = "kept" }));
        var client = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(response) };
        var tool = new JevDecisionFunction(client, "assess_ticket", "Assess a ticket.", DecisionFixtures.AssessmentRequest);
        using var source = new CancellationTokenSource();

        Assert.Equal("assess_ticket", tool.Name);
        Assert.Equal("Assess a ticket.", tool.Description);
        Assert.Equal("state", tool.JsonSchema.GetProperty("required")[0].GetString());
        Assert.False(tool.JsonSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Single(tool.JsonSchema.GetProperty("properties").EnumerateObject());
        Assert.NotNull(tool.ReturnJsonSchema);
        object? invocation = await tool.InvokeAsync(new AIFunctionArguments { ["state"] = JsonSerializer.SerializeToElement("ticket") }, source.Token);
        JsonElement result = Assert.IsType<JsonElement>(invocation);
        using JsonDocument serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
        JsonElement answers = serialized.RootElement.GetProperty("answers");

        Assert.Equal("ticket", client.Request!.State.GetString());
        Assert.Equal("jev-pinned", client.Request.Model);
        Assert.Equal(source.Token, client.Token);
        Assert.Equal("Exact Label", answers.GetProperty("category").GetProperty("choice").GetString());
        Assert.Equal(2, answers.GetProperty("category").GetProperty("probabilities").EnumerateObject().Count());
        Assert.Equal(1.37, answers.GetProperty("quality").GetProperty("score").GetDouble());
        Assert.Equal("low", answers.GetProperty("quality").GetProperty("legend").GetProperty("0").GetProperty("label").GetString());
        Assert.Equal(.75, answers.GetProperty("allowed").GetProperty("probability").GetDouble());
        Assert.False(answers.GetProperty("allowed").TryGetProperty("confidence", out _));
        Assert.True(answers.GetProperty("allowed").GetProperty("rawRepresentation").GetProperty("future").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("usage").GetProperty("outputTokens").ValueKind);
        Assert.Equal("req", result.GetProperty("requestId").GetString());
        Assert.Equal("kept", result.GetProperty("rawRepresentation").GetProperty("future").GetString());
    }

    [Fact]
    public async Task InvalidArgumentsNeverReachDecisionClient()
    {
        var client = new DecisionClientStub();
        var tool = new JevDecisionFunction(client, "assess", "Assess.", DecisionFixtures.AssessmentRequest);
        foreach (AIFunctionArguments arguments in new AIFunctionArguments[]
        {
            new(), new() { ["state"] = 1 }, new() { ["state"] = null },
            new() { ["state"] = JsonSerializer.SerializeToElement(new { a = 1 }) },
            new() { ["state"] = "text", ["model"] = "untrusted" }
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(arguments).AsTask());
        }

        Assert.Equal(0, client.Calls);
        Assert.Throws<ArgumentException>(() => new JevDecisionFunction(client, "invalid name", "Assess.", DecisionFixtures.AssessmentRequest));
    }

    [Fact]
    public async Task InvocationPropagatesCancellation()
    {
        using var source = new CancellationTokenSource();
        var client = new DecisionClientStub
        {
            Evaluate = async (_, token) =>
            {
                source.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return DecisionFixtures.Assessment();
            }
        };
        var tool = new JevDecisionFunction(client, "assess", "Assess.", DecisionFixtures.AssessmentRequest);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync(new() { ["state"] = "text" }, source.Token).AsTask());
        Assert.Equal(source.Token, client.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync(new() { ["state"] = "text" }, source.Token).AsTask());
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task StandardFunctionInvocationMiddlewareExecutesToolAndReceivesJsonResult()
    {
        var decisions = new DecisionClientStub { Evaluate = (_, _) => Task.FromResult(DecisionFixtures.Assessment()) };
        var tool = new JevDecisionFunction(decisions, "assess", "Assess.", DecisionFixtures.AssessmentRequest);
        int turn = 0;
        var chat = new ChatClientStub
        {
            Respond = (messages, _, _) =>
            {
                if (turn++ == 0)
                {
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?> { ["state"] = "ticket" })]))
                    { FinishReason = ChatFinishReason.ToolCalls });
                }

                FunctionResultContent result = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
                Assert.Equal("call-1", result.CallId);
                JsonElement json = Assert.IsType<JsonElement>(result.Result);
                Assert.Equal(.9, json.GetProperty("answers").GetProperty("allowed").GetProperty("probability").GetDouble());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "finished")) { FinishReason = ChatFinishReason.Stop });
            }
        };
        using IChatClient pipeline = chat.AsBuilder().UseFunctionInvocation().Build();

        ChatResponse response = await pipeline.GetResponseAsync(DecisionFixtures.Messages, new ChatOptions { Tools = [tool] });

        Assert.Equal("finished", response.Messages.Last().Text);
        Assert.Equal(1, decisions.Calls);
        Assert.Equal(2, chat.ResponseCalls);
    }
}
