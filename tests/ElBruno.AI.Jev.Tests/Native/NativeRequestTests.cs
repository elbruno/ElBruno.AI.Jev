using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElBruno.AI.Jev.Tests.Native;

public sealed class NativeRequestTests
{
    [Fact]
    public async Task ChoiceUsesOfficialWireAndPerRequestCredentials()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse()));
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("x-caller-header", "unchanged");
        using var client = new JevClient(http, NativeFixtures.Options());

        await client.EvaluateAsync(NativeFixtures.ChoiceRequest());

        RecordedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", sent.Uri!.AbsoluteUri);
        Assert.Equal($"Bearer {NativeFixtures.Credential}", sent.Authorization);
        Assert.Equal("application/json", sent.Accept);
        Assert.Equal("application/json; charset=utf-8", sent.ContentType);
        Assert.StartsWith("ElBruno.AI.Jev/", sent.UserAgent);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal("unchanged", Assert.Single(http.DefaultRequestHeaders.GetValues("x-caller-header")));
        NativeFixtures.AssertJson("""
            {"state":"private test state","model":"jev-latest","questions":{"route":{
              "type":"choice","instructions":"Pick a route.","criteria":{"fast":"Simple","careful":"Complex"}}}}
            """, sent.Body!);
    }

    [Fact]
    public async Task StructuredAndNullDescriptorsPreserveJsonTypesAndQuestionOrder()
    {
        var request = new JevDecisionRequest(JevJson.Parse("""{"conversation":["hello"],"attempt":3}"""), JevModels.Preview)
            .WithQuestion(new JevQuestionKey<JevScoreAnswer>("quality"), new JevScoreQuestion(
                JevJson.Parse("""{"goal":"quality"}"""), new[] { JevJson.Parse("null"), JevJson.Parse("""{"value":"good"}""") }))
            .WithQuestion(new JevQuestionKey<JevChoiceAnswer>("route"), new JevChoiceQuestion(
                (JsonElement?)null, new Dictionary<string, JsonElement> { ["fast"] = JevJson.Parse("null"), ["careful"] = JevJson.Parse("""["reason",1]""") }))
            .WithQuestion(new JevQuestionKey<JevNoulAnswer>("safe"), new JevNoulQuestion(
                JevJson.Parse("null"), JevJson.Parse("""{"risk":"low"}"""), JevJson.Parse("null")))
            .WithQuestion(new JevQuestionKey<JevNoulAnswer>("plain"), new JevNoulQuestion((string?)null));
        const string response = """
            {"model":"jev-future","answers":{
              "plain":{"type":"noul","noul":0.1},"safe":{"type":"noul","noul":0.2},
              "route":{"type":"choice","choice":"fast","probabilities":{"fast":0.7,"careful":0.3},"confidence":0.9},
              "quality":{"type":"score","score":0.5,"probabilities":{"0":0.5,"1":0.5},"legend":{"0":null,"1":{"value":"good"}},"confidence":0.9}}}
            """;
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(response)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        JevDecisionResponse result = await client.EvaluateAsync(request);

        string body = Assert.Single(handler.Requests).Body!;
        NativeFixtures.AssertJson("""
            {"state":{"conversation":["hello"],"attempt":3},"model":"jev-preview","questions":{
              "quality":{"type":"score","instructions":{"goal":"quality"},"criteria":[null,{"value":"good"}]},
              "route":{"type":"choice","criteria":{"fast":null,"careful":["reason",1]}},
              "safe":{"type":"noul","instructions":null,"criteria":{"true":{"risk":"low"},"false":null}},
              "plain":{"type":"noul"}}}
            """, body);
        Assert.Equal(new[] { "quality", "route", "safe", "plain" }, JevJson.Parse(body).GetProperty("questions").EnumerateObject().Select(p => p.Name));
        Assert.Equal(0.2, result.GetAnswer(new JevQuestionKey<JevNoulAnswer>("safe")).Probability);
    }

    [Theory]
    [InlineData("jev-latest")]
    [InlineData("jev-preview")]
    [InlineData("jev-1.13.0")]
    [InlineData("jev-2030-future-pin")]
    public async Task AcceptsAliasesPinnedAndFutureModels(string model)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse()));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        await client.EvaluateAsync(NativeFixtures.ChoiceRequest(model));
        Assert.Equal(model, JevJson.Parse(Assert.Single(handler.Requests).Body!).GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public async Task ChoiceAcceptsAndTransmitsBoundaryCounts(int count)
    {
        Dictionary<string, string?> criteria = Enumerable.Range(0, count).ToDictionary(i => $"label {i}", _ => (string?)null);
        var question = new JevChoiceQuestion(null, criteria);
        var request = new JevDecisionRequest("state").WithQuestion(new JevQuestionKey<JevChoiceAnswer>("route"), question);
        string probabilities = string.Join(",", Enumerable.Range(0, count).Select(i => $"\"label {i}\":{(i == 0 ? "1" : "0")}"));
        string response = $$$$"""{"model":"jev-latest","answers":{"route":{"type":"choice","choice":"label 0","probabilities":{ {{{{probabilities}}}} },"confidence":1}}}""";
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(response)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        var answer = (await client.EvaluateAsync(request)).GetAnswer(new JevQuestionKey<JevChoiceAnswer>("route"));
        Assert.Equal(count, answer.Probabilities.Count);
        Assert.Equal(count, JevJson.Parse(Assert.Single(handler.Requests).Body!).GetProperty("questions").GetProperty("route").GetProperty("criteria").EnumerateObject().Count());
        Assert.All(question.Criteria.Values, value => Assert.Equal(JsonValueKind.Null, value.ValueKind));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task ScoreAcceptsOrderedBoundaryRubrics(int count)
    {
        string[] criteria = Enumerable.Range(0, count).Select(i => $"level {i}").ToArray();
        var request = new JevDecisionRequest(JevJson.Parse("""["state"]""")).WithQuestion(
            new JevQuestionKey<JevScoreAnswer>("quality"), new JevScoreQuestion("rubric", criteria));
        string probabilities = string.Join(",", Enumerable.Range(0, count).Select(i => $"\"{i}\":{(i == 0 ? "1" : "0")}"));
        string legend = string.Join(",", criteria.Select((text, i) => $"\"{i}\":\"{text}\""));
        string response = $$$$"""{"model":"jev-latest","answers":{"quality":{"type":"score","score":0,"probabilities":{ {{{{probabilities}}}} },"legend":{ {{{{legend}}}} },"confidence":1}}}""";
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(response)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        await client.EvaluateAsync(request);
        Assert.Equal(criteria, JevJson.Parse(Assert.Single(handler.Requests).Body!).GetProperty("questions").GetProperty("quality").GetProperty("criteria").EnumerateArray().Select(v => v.GetString()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NoulOnlySendsExplicitOutcomeDescriptions(bool yes, bool no)
    {
        var request = new JevDecisionRequest("state").WithQuestion(new JevQuestionKey<JevNoulAnswer>("safe"),
            new JevNoulQuestion("safe?", yes ? "allowed" : null, no ? "denied" : null));
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(NativeFixtures.NoulJson)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        await client.EvaluateAsync(request);
        JsonElement wire = JevJson.Parse(Assert.Single(handler.Requests).Body!).GetProperty("questions").GetProperty("safe");
        Assert.Equal(yes || no, wire.TryGetProperty("criteria", out JsonElement criteria));
        if (yes || no)
        {
            Assert.Equal(yes, criteria.TryGetProperty("true", out _));
            Assert.Equal(no, criteria.TryGetProperty("false", out _));
        }
    }

    [Fact]
    public void InputsAreOwnedImmutableSnapshotsAfterDocumentDisposalAndCallerMutation()
    {
        JevDecisionRequest request;
        JevChoiceQuestion choice;
        JevScoreQuestion score;
        JevNoulQuestion noul;
        using (JsonDocument document = JsonDocument.Parse("""{"state":{"item":1},"instructions":["consider"],"descriptor":{"rank":2}}"""))
        {
            JsonElement root = document.RootElement;
            var choices = new Dictionary<string, JsonElement> { ["A"] = root.GetProperty("descriptor") };
            var scores = new[] { root.GetProperty("descriptor"), JevJson.Parse("null") };
            choice = new JevChoiceQuestion(root.GetProperty("instructions"), choices);
            score = new JevScoreQuestion(root.GetProperty("instructions"), scores);
            noul = new JevNoulQuestion(root.GetProperty("instructions"), root.GetProperty("descriptor"), JevJson.Parse("null"));
            var questions = new Dictionary<string, JevQuestion> { ["one"] = choice };
            request = new JevDecisionRequest(root.GetProperty("state"), questions);
            choices.Clear();
            scores[0] = JevJson.Text("mutated");
            questions.Clear();
        }

        Assert.Equal(1, request.State.GetProperty("item").GetInt32());
        Assert.Single(request.Questions);
        Assert.Equal(2, choice.Criteria["A"].GetProperty("rank").GetInt32());
        Assert.Equal("consider", choice.Instructions!.Value[0].GetString());
        Assert.Equal(2, score.Criteria[0].GetProperty("rank").GetInt32());
        Assert.Equal(2, noul.TrueCriteria!.Value.GetProperty("rank").GetInt32());
        Assert.Equal(JsonValueKind.Null, noul.FalseCriteria!.Value.ValueKind);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JsonElement>)choice.Criteria).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<JsonElement>)score.Criteria)[0] = JevJson.Text("changed"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JevQuestion>)request.Questions).Clear());
        var expanded = request.WithQuestion(new JevQuestionKey<JevNoulAnswer>("ONE"), noul);
        Assert.Single(request.Questions);
        Assert.Equal(new[] { "one", "ONE" }, expanded.Questions.Keys);
    }

    [Fact]
    public void TextAndStructuredHelpersDoNotConfuseTextWithJson()
    {
        Assert.Equal("""{"a":1}""", JevJson.Text("""{"a":1}""").GetString());
        Assert.Equal(1, JevJson.Parse("""{"a":1}""").GetProperty("a").GetInt32());
        Assert.Equal("typed", JevJson.From("typed", NativeTestJsonContext.Default.String).GetString());
        Assert.Throws<ArgumentNullException>(() => JevJson.Text(null!));
        Assert.Throws<ArgumentNullException>(() => JevJson.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => JevJson.From("x", null!));
        Assert.ThrowsAny<JsonException>(() => JevJson.Parse("{"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void ChoiceRejectsOutOfBoundsCounts(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevChoiceQuestion(null, Enumerable.Range(0, count).ToDictionary(i => $"l{i}", _ => (string?)null)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public void ScoreRejectsOutOfBoundsCounts(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevScoreQuestion(null, Enumerable.Repeat("level", count).ToArray()));

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    public void StateRejectsScalarsOtherThanStrings(string json) =>
        Assert.Throws<ArgumentException>(() => new JevDecisionRequest(JevJson.Parse(json)));

    [Fact]
    public async Task InvalidInputsAndDuplicateTypedKeysFailBeforeNetwork()
    {
        Assert.Throws<ArgumentException>(() => new JevQuestionKey<JevNoulAnswer>(" "));
        Assert.Throws<ArgumentNullException>(() => new JevQuestionKey<JevNoulAnswer>(null!));
        Assert.Throws<ArgumentNullException>(() => new JevDecisionRequest((string)null!));
        Assert.Throws<ArgumentException>(() => new JevDecisionRequest(default(JsonElement)));
        Assert.Throws<ArgumentException>(() => new JevDecisionRequest("state", " "));
        Assert.Throws<ArgumentNullException>(() => new JevDecisionRequest(JevJson.Text("state"), (IReadOnlyDictionary<string, JevQuestion>)null!));
        Assert.Throws<ArgumentException>(() => new JevDecisionRequest(JevJson.Text("state"), new Dictionary<string, JevQuestion> { [" "] = new JevNoulQuestion("x") }));
        Assert.Throws<ArgumentNullException>(() => new JevDecisionRequest(JevJson.Text("state"), new Dictionary<string, JevQuestion> { ["q"] = null! }));
        Assert.Throws<ArgumentNullException>(() => new JevChoiceQuestion(null, (IReadOnlyDictionary<string, string?>)null!));
        Assert.Throws<ArgumentNullException>(() => new JevChoiceQuestion(null, (IReadOnlyDictionary<string, JsonElement>)null!));
        Assert.Throws<ArgumentException>(() => new JevChoiceQuestion(null, new Dictionary<string, string?> { [" "] = "x" }));
        Assert.Throws<ArgumentException>(() => new JevChoiceQuestion(null, new Dictionary<string, JsonElement> { ["A"] = default }));
        Assert.Throws<ArgumentException>(() => new JevNoulQuestion(default(JsonElement), null, null));
        Assert.Throws<ArgumentException>(() => new JevNoulQuestion(null, default(JsonElement), null));
        Assert.Throws<ArgumentNullException>(() => new JevScoreQuestion(null, (IReadOnlyList<string>)null!));
        Assert.Throws<ArgumentNullException>(() => new JevScoreQuestion(null, (IReadOnlyList<JsonElement>)null!));
        Assert.Throws<ArgumentNullException>(() => new JevScoreQuestion(null, new[] { "a", null! }));
        Assert.Throws<ArgumentException>(() => new JevScoreQuestion(null, new[] { JevJson.Text("a"), default }));
        var request = NativeFixtures.NoulRequest();
        Assert.Throws<ArgumentException>(() => request.WithQuestion(new JevQuestionKey<JevNoulAnswer>("safe"), new JevNoulQuestion("duplicate")));
        Assert.Throws<ArgumentNullException>(() => request.WithQuestion<JevNoulAnswer>(null!, new JevNoulQuestion("x")));
        Assert.Throws<ArgumentNullException>(() => request.WithQuestion(new JevQuestionKey<JevNoulAnswer>("x"), null!));
        using var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.EvaluateAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EvaluateAsync(new JevDecisionRequest("empty")));
        Assert.Equal(0, handler.Calls);
    }
}

[JsonSerializable(typeof(string))]
internal partial class NativeTestJsonContext : JsonSerializerContext;
