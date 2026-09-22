using System.Text.Json;
using System.Text.Json.Nodes;

namespace ElBruno.AI.Jev.Tests.Native;

public sealed class NativeResponseTests
{
    [Fact]
    public async Task FractionalScoreRetainsDistributionLegendConfidenceAndRawFields()
    {
        string json = NativeFixtures.ScoreJson.Replace("\"score\":1.25", "\"score\":1.25,\"future\":{\"version\":[1,2]}", StringComparison.Ordinal);
        JevDecisionResponse response = await NativeFixtures.EvaluateAsync(json, NativeFixtures.ScoreRequest());
        JevScoreAnswer answer = response.GetAnswer(new JevQuestionKey<JevScoreAnswer>("quality"));
        Assert.Equal(1.25, answer.Score);
        Assert.Equal(0.7, answer.Confidence);
        Assert.Equal(new Dictionary<string, double> { ["0"] = 0.125, ["1"] = 0.5, ["2"] = 0.375 }, answer.Probabilities);
        Assert.Equal("fair", answer.Legend["1"].GetString());
        Assert.Equal(2, answer.RawRepresentation!.Value.GetProperty("future").GetProperty("version")[1].GetInt32());
        Assert.Equal("jev-1.13.0", response.Model);
        Assert.Equal("offline-request-id", response.RequestId);
    }

    [Fact]
    public async Task EqualExpectedScoresDoNotEraseDifferentUncertainty()
    {
        string concentrated = NativeFixtures.ScoreJson
            .Replace("\"score\":1.25", "\"score\":1", StringComparison.Ordinal)
            .Replace("\"0\":0.125,\"1\":0.5,\"2\":0.375", "\"0\":0,\"1\":1,\"2\":0", StringComparison.Ordinal);
        string polarized = concentrated.Replace("\"0\":0,\"1\":1,\"2\":0", "\"0\":0.5,\"1\":0,\"2\":0.5", StringComparison.Ordinal);
        JevScoreAnswer first = (await NativeFixtures.EvaluateAsync(concentrated, NativeFixtures.ScoreRequest())).GetAnswer(new JevQuestionKey<JevScoreAnswer>("quality"));
        JevScoreAnswer second = (await NativeFixtures.EvaluateAsync(polarized, NativeFixtures.ScoreRequest())).GetAnswer(new JevQuestionKey<JevScoreAnswer>("quality"));
        Assert.Equal(first.Score, second.Score);
        Assert.Equal(1, first.Probabilities["1"]);
        Assert.Equal(0, second.Probabilities["1"]);
        Assert.Equal(0.5, second.Probabilities["0"]);
        Assert.Equal(0.5, second.Probabilities["2"]);
    }

    [Fact]
    public async Task ChoiceConfidenceAndNoulProbabilityAreDistinctConcepts()
    {
        var request = NativeFixtures.ChoiceRequest().WithQuestion(new JevQuestionKey<JevNoulAnswer>("safe"), new JevNoulQuestion("safe?"));
        const string json = """
            {"model":"jev-future-resolved","answers":{
              "safe":{"type":"noul","noul":0.19},
              "route":{"type":"choice","choice":"fast","probabilities":{"fast":0.51,"careful":0.49},"confidence":0.01}},
              "usage":{"input_tokens":37,"output_tokens":4},"new_metadata":{"region":"offline"}}
            """;
        JevDecisionResponse response = await NativeFixtures.EvaluateAsync(json, request);
        var choice = response.GetAnswer(new JevQuestionKey<JevChoiceAnswer>("route"));
        var noul = response.GetAnswer(new JevQuestionKey<JevNoulAnswer>("safe"));
        Assert.Equal("fast", choice.Choice);
        Assert.Equal(0.51, choice.Probabilities["fast"]);
        Assert.Equal(0.01, choice.Confidence);
        Assert.Equal(0.19, noul.Probability);
        Assert.Null(typeof(JevNoulAnswer).GetProperty("Confidence"));
        Assert.Null(typeof(JevNoulAnswer).GetProperty("Value"));
        Assert.Equal("jev-future-resolved", response.Model);
        Assert.Equal(new JevUsage(37, 4), response.Usage);
        Assert.Equal("offline", response.RawRepresentation!.Value.GetProperty("new_metadata").GetProperty("region").GetString());
        Assert.Equal("choice", choice.RawRepresentation!.Value.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"usage\":null")]
    [InlineData(",\"usage\":{}")]
    [InlineData(",\"usage\":{\"future_tokens\":99}")]
    [InlineData(",\"usage\":{\"input_tokens\":null,\"output_tokens\":null}")]
    public async Task UnknownOrMissingUsageRemainsUnknownRatherThanZero(string suffix)
    {
        string json = NativeFixtures.NoulJson[..^1] + suffix + "}";
        JevDecisionResponse response = await NativeFixtures.EvaluateAsync(json, NativeFixtures.NoulRequest());
        Assert.Null(response.Usage.InputTokens);
        Assert.Null(response.Usage.OutputTokens);
    }

    [Theory]
    [InlineData("\"input_tokens\":0", 0L, null)]
    [InlineData("\"output_tokens\":0", null, 0L)]
    [InlineData("\"input_tokens\":9223372036854775807,\"output_tokens\":2", long.MaxValue, 2L)]
    public async Task ValidUsageKeepsZeroLargeAndPartiallyKnownCounts(string usage, long? input, long? output)
    {
        string json = NativeFixtures.NoulJson[..^1] + ",\"usage\":{" + usage + "}}";
        JevDecisionResponse response = await NativeFixtures.EvaluateAsync(json, NativeFixtures.NoulRequest());
        Assert.Equal(input, response.Usage.InputTokens);
        Assert.Equal(output, response.Usage.OutputTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NoulAcceptsProbabilityEndpoints(double probability)
    {
        var answer = (await NativeFixtures.EvaluateAsync(NativeFixtures.NoulJson.Replace("0.8", probability.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), NativeFixtures.NoulRequest()))
            .GetAnswer(new JevQuestionKey<JevNoulAnswer>("safe"));
        Assert.Equal(probability, answer.Probability);
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task InvalidContractResponsesProduceOwnedRedactedProtocolExceptions(string scenario, string json, string requestKind)
    {
        JevDecisionRequest request = requestKind switch
        {
            "score" => NativeFixtures.ScoreRequest(),
            "noul" => NativeFixtures.NoulRequest(),
            _ => NativeFixtures.ChoiceRequest()
        };
        JevProtocolException exception = await Assert.ThrowsAsync<JevProtocolException>(() => NativeFixtures.EvaluateAsync(json, request));
        Assert.Equal("offline-request-id", exception.RequestId);
        Assert.NotNull(exception.RawRepresentation);
        Assert.Equal(JevJson.Parse(json).GetRawText(), exception.RawRepresentation.Value.GetRawText());
        Assert.DoesNotContain(NativeFixtures.Credential, exception.Message);
        Assert.DoesNotContain("private test state", exception.Message);
        Assert.False(string.IsNullOrWhiteSpace(scenario));
    }

    public static IEnumerable<object[]> InvalidResponses()
    {
        foreach (string json in new[] { "null", "[]", "\"response\"", "1", "true", "{}", """{"model":"jev-latest"}""", """{"answers":{}}""" })
            yield return ["invalid root or required root field", json, "choice"];
        yield return ["duplicate root field", NativeFixtures.ChoiceJson.Replace("\"model\":", "\"model\":\"first\",\"model\":", StringComparison.Ordinal), "choice"];
        yield return ["duplicate answer", NativeFixtures.ChoiceJson.Replace("\"route\":", "\"route\":{},\"route\":", StringComparison.Ordinal), "choice"];
        yield return ["duplicate answer field", NativeFixtures.ChoiceJson.Replace("\"confidence\":0.8", "\"confidence\":0.8,\"confidence\":0.8", StringComparison.Ordinal), "choice"];
        yield return ["duplicate probability", NativeFixtures.ChoiceJson.Replace("\"fast\":0.75", "\"fast\":0.75,\"fast\":0.75", StringComparison.Ordinal), "choice"];
        yield return ["unexpected identifier", NativeFixtures.ChoiceJson.Replace("\"route\":", "\"Route\":", StringComparison.Ordinal), "choice"];
        yield return ["extra identifier", NativeFixtures.ChoiceJson.Replace("\"route\":", "\"extra\":{},\"route\":", StringComparison.Ordinal), "choice"];
        yield return ["missing answer", """{"model":"jev-latest","answers":{}}""", "choice"];

        foreach ((string path, string? value) in new (string, string?)[]
        {
            ("model", "\"\""), ("model", "\" \""), ("model", "null"), ("model", "3"),
            ("answers", "[]"), ("answers", "null"), ("answers.route", "[]"), ("answers.route", "null"),
            ("answers.route.type", null), ("answers.route.type", "\"\""), ("answers.route.type", "\"score\""),
            ("answers.route.type", "\"future\""), ("answers.route.type", "1"),
            ("answers.route.choice", null), ("answers.route.choice", "null"), ("answers.route.choice", "1"),
            ("answers.route.choice", "\"\""), ("answers.route.choice", "\"FAST\""),
            ("answers.route.probabilities", null), ("answers.route.probabilities", "[]"),
            ("answers.route.probabilities", "{}"), ("answers.route.probabilities", """{"fast":1}"""),
            ("answers.route.probabilities", """{"fast":0.7,"careful":0.2,"other":0.1}"""),
            ("answers.route.probabilities.fast", "\"0.75\""), ("answers.route.probabilities.fast", "null"),
            ("answers.route.probabilities.fast", "-0.1"), ("answers.route.probabilities.fast", "1.1"),
            ("answers.route.probabilities.fast", "1e400"),
            ("answers.route.confidence", null), ("answers.route.confidence", "null"),
            ("answers.route.confidence", "\"0.8\""), ("answers.route.confidence", "-0.1"),
            ("answers.route.confidence", "1.1"), ("answers.route.confidence", "1e400"),
            ("usage", "[]"), ("usage", "3"), ("usage", """{"input_tokens":-1}"""),
            ("usage", """{"input_tokens":1.5}"""), ("usage", """{"input_tokens":"3"}"""),
            ("usage", """{"input_tokens":9223372036854775808}"""),
            ("usage", """{"output_tokens":-1}"""), ("usage", """{"output_tokens":true}"""),
            ("usage", """{"output_tokens":1e400}""")
        })
            yield return [$"invalid {path}: {value ?? "missing"}", Mutate(NativeFixtures.ChoiceJson, path, value), "choice"];
        yield return ["duplicate usage", NativeFixtures.ChoiceJson[..^1] + ""","usage":{"input_tokens":1,"input_tokens":2}}""", "choice"];

        foreach ((string path, string? value) in new (string, string?)[]
        {
            ("answers.quality.score", null), ("answers.quality.score", "-0.1"),
            ("answers.quality.score", "2.01"), ("answers.quality.score", "1e400"),
            ("answers.quality.score", "\"1.25\""),
            ("answers.quality.probabilities", """{"00":0.125,"1":0.5,"2":0.375}"""),
            ("answers.quality.probabilities", """{"1":0.125,"2":0.5,"3":0.375}"""),
            ("answers.quality.probabilities", """{"0":0.5,"1":0.5}"""),
            ("answers.quality.legend", null), ("answers.quality.legend", "[]"),
            ("answers.quality.legend", """{"0":"poor","1":"fair"}"""),
            ("answers.quality.legend", """{"00":"poor","1":"fair","2":"good"}"""),
            ("answers.quality.legend", """{"0":"poor","1":"fair","2":"good","3":"great"}"""),
            ("answers.quality.confidence", "2")
        })
            yield return [$"invalid {path}: {value ?? "missing"}", Mutate(NativeFixtures.ScoreJson, path, value), "score"];
        yield return ["duplicate legend", NativeFixtures.ScoreJson.Replace("\"0\":\"poor\"", "\"0\":\"poor\",\"0\":\"poor\"", StringComparison.Ordinal), "score"];

        foreach (string? value in new[] { null, "null", "\"0.8\"", "-0.1", "1.1", "1e400", "false" })
            yield return [$"invalid noul: {value ?? "missing"}", Mutate(NativeFixtures.NoulJson, "answers.safe.noul", value), "noul"];
    }

    private static string Mutate(string json, string path, string? replacement)
    {
        JsonNode root = JsonNode.Parse(json)!;
        string[] parts = path.Split('.');
        JsonObject target = root.AsObject();
        foreach (string part in parts[..^1]) target = target[part]!.AsObject();
        if (replacement is null) target.Remove(parts[^1]);
        else target[parts[^1]] = JsonNode.Parse(replacement);
        return root.ToJsonString();
    }

    [Fact]
    public void AnswerResponseModelAndExceptionConstructorsOwnCollectionsAndJson()
    {
        JevChoiceAnswer choice;
        JevScoreAnswer score;
        JevNoulAnswer noul;
        JevDecisionResponse response;
        JevModelList models;
        JevProtocolException exception;
        using (JsonDocument document = JsonDocument.Parse("""{"native":{"tag":"retained"}}"""))
        {
            var probabilities = new Dictionary<string, double> { ["0"] = 0.25, ["1"] = 0.75 };
            var legend = new Dictionary<string, JsonElement> { ["0"] = document.RootElement, ["1"] = JevJson.Parse("null") };
            choice = new JevChoiceAnswer("0", probabilities, 0.5, document.RootElement);
            score = new JevScoreAnswer(0.75, probabilities, legend, 0.4, document.RootElement);
            noul = new JevNoulAnswer(0.1, document.RootElement);
            var answers = new Dictionary<string, JevAnswer> { ["choice"] = choice, ["score"] = score, ["noul"] = noul };
            response = new JevDecisionResponse("future", answers, rawRepresentation: document.RootElement);
            var sourceModels = new List<JevModelInfo> { new("future", "description", "tomorrow") };
            models = new JevModelList(sourceModels, "id", document.RootElement);
            exception = new JevProtocolException("safe message", "id", document.RootElement);
            probabilities.Clear();
            legend.Clear();
            answers.Clear();
            sourceModels.Clear();
        }
        Assert.Equal(0.25, choice.Probabilities["0"]);
        Assert.Equal(0.75, score.Probabilities["1"]);
        Assert.Equal("retained", score.Legend["0"].GetProperty("native").GetProperty("tag").GetString());
        Assert.Equal(3, response.Answers.Count);
        Assert.Single(models.Models);
        Assert.Equal("id", models.RequestId);
        foreach (JsonElement? raw in new[] { choice.RawRepresentation, score.RawRepresentation, noul.RawRepresentation, response.RawRepresentation, models.RawRepresentation, exception.RawRepresentation })
            Assert.Equal("retained", raw!.Value.GetProperty("native").GetProperty("tag").GetString());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, double>)choice.Probabilities).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JsonElement>)score.Legend).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JevAnswer>)response.Answers).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<JevModelInfo>)models.Models).Clear());
        Assert.Same(choice, response.GetAnswer(new JevQuestionKey<JevChoiceAnswer>("choice")));
        Assert.Throws<KeyNotFoundException>(() => response.GetAnswer(new JevQuestionKey<JevChoiceAnswer>("Choice")));
        Assert.Throws<InvalidOperationException>(() => response.GetAnswer(new JevQuestionKey<JevNoulAnswer>("choice")));
        Assert.Throws<ArgumentNullException>(() => response.GetAnswer<JevChoiceAnswer>(null!));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void AnswerConstructorsRejectInvalidProbabilitiesAndConfidence(double value)
    {
        var valid = new Dictionary<string, double> { ["A"] = 1 };
        var invalid = new Dictionary<string, double> { ["A"] = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevChoiceAnswer("A", invalid, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevChoiceAnswer("A", valid, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevNoulAnswer(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevScoreAnswer(0, valid, new Dictionary<string, JsonElement>(), value));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void ScoreConstructorRejectsNonfiniteOrOutOfRangePositions(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevScoreAnswer(value,
            new Dictionary<string, double> { ["0"] = 0.5, ["1"] = 0.5 }, new Dictionary<string, JsonElement>(), 0.5));

    [Fact]
    public void PublicResultConstructorsValidateRequiredArguments()
    {
        var probabilities = new Dictionary<string, double> { ["A"] = 1 };
        Assert.Throws<ArgumentNullException>(() => new JevChoiceAnswer("A", null!, 1));
        Assert.Throws<ArgumentException>(() => new JevChoiceAnswer("A", new Dictionary<string, double>(), 1));
        Assert.Throws<ArgumentException>(() => new JevChoiceAnswer("B", probabilities, 1));
        Assert.Throws<ArgumentException>(() => new JevChoiceAnswer(" ", probabilities, 1));
        Assert.Throws<ArgumentNullException>(() => new JevScoreAnswer(0, probabilities, null!, 1));
        Assert.Throws<ArgumentNullException>(() => new JevDecisionResponse("model", null!));
        Assert.Throws<ArgumentException>(() => new JevDecisionResponse("", new Dictionary<string, JevAnswer>()));
        Assert.Throws<ArgumentException>(() => new JevDecisionResponse("model", new Dictionary<string, JevAnswer> { ["q"] = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevDecisionResponse("model", new Dictionary<string, JevAnswer>(), new JevUsage(-1, null)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JevDecisionResponse("model", new Dictionary<string, JevAnswer>(), new JevUsage(null, -1)));
        Assert.Throws<ArgumentNullException>(() => new JevModelList(null!));
        Assert.Null(new JevNoulAnswer(0).RawRepresentation);
        Assert.Null(new JevModelList([]).RawRepresentation);
        Assert.Null(new JevProtocolException("safe").RawRepresentation);
        Assert.Equal(new JevUsage(), new JevDecisionResponse("model", new Dictionary<string, JevAnswer>()).Usage);
    }

    [Fact]
    public async Task ModelDiscoveryUsesAuthenticatedGetAndKeepsFutureAndUnknownMetadata()
    {
        const string json = """
            {"models":[
              {"name":"jev-latest","description":"Stable","release_date":"2026-09-01","new_capability":{"score":true}},
              {"name":"jev-preview","description":null,"release_date":null},
              {"name":"jev-2031-future"}],"page_info":{"complete":true}}
            """;
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(json)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        JevModelList list = await client.ListModelsAsync();
        RecordedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://api.typesafe.ai/v1/models", sent.Uri!.AbsoluteUri);
        Assert.Null(sent.Body);
        Assert.Null(sent.ContentType);
        Assert.Equal($"Bearer {NativeFixtures.Credential}", sent.Authorization);
        Assert.Equal("application/json", sent.Accept);
        Assert.Equal(3, list.Models.Count);
        Assert.Equal(new JevModelInfo("jev-latest", "Stable", "2026-09-01"), list.Models[0]);
        Assert.Null(list.Models[1].Description);
        Assert.Null(list.Models[2].ReleaseDate);
        Assert.True(list.RawRepresentation!.Value.GetProperty("models")[0].GetProperty("new_capability").GetProperty("score").GetBoolean());
        Assert.True(list.RawRepresentation.Value.GetProperty("page_info").GetProperty("complete").GetBoolean());
        Assert.Equal("offline-request-id", list.RequestId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"models\":null}")]
    [InlineData("{\"models\":{}}")]
    [InlineData("{\"models\":[null]}")]
    [InlineData("{\"models\":[{}]}")]
    [InlineData("{\"models\":[{\"name\":\" \"}]}")]
    [InlineData("{\"models\":[{\"name\":1}]}")]
    [InlineData("{\"models\":[{\"name\":\"model\",\"description\":7}]}")]
    [InlineData("{\"models\":[{\"name\":\"model\",\"release_date\":false}]}")]
    [InlineData("{\"models\":[{\"name\":\"one\",\"name\":\"two\"}]}")]
    [InlineData("{\"models\":[],\"models\":[]}")]
    public async Task ModelDiscoveryRejectsInvalidContracts(string json)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse(json)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        JevProtocolException exception = await Assert.ThrowsAsync<JevProtocolException>(() => client.ListModelsAsync());
        Assert.Equal("offline-request-id", exception.RequestId);
        Assert.Equal(json, exception.RawRepresentation!.Value.GetRawText());
    }

    [Fact]
    public async Task EmptyDiscoveryAndAbsentRequestIdAreValid()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(NativeFixtures.JsonResponse("""{"models":[]}""", requestId: null)));
        using var http = new HttpClient(handler);
        using var client = new JevClient(http, NativeFixtures.Options());
        JevModelList result = await client.ListModelsAsync();
        Assert.Empty(result.Models);
        Assert.Null(result.RequestId);
    }
}
