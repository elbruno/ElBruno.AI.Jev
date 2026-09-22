using System.Text.Json;
using Microsoft.Extensions.Configuration;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ElBruno.AI.Jev.IntegrationTests;

public sealed class OfficialServiceTests
{
    [LiveFact]
    public async Task DiscoveryAndMixedDecisionsPreserveTheNativeContract()
    {
        using JevClient client = CreateClient();
        JevModelList models = await client.ListModelsAsync();
        Assert.All(models.Models, model => Assert.False(string.IsNullOrWhiteSpace(model.Name)));

        var category = new JevQuestionKey<JevChoiceAnswer>("category");
        var urgency = new JevQuestionKey<JevScoreAnswer>("urgency");
        var review = new JevQuestionKey<JevNoulAnswer>("review");
        var request = new JevDecisionRequest("Synthetic test: a customer reports a duplicate invoice.")
            .WithQuestion(category, new JevChoiceQuestion("Select the team.", new Dictionary<string, string?> { ["billing"] = "Payments and invoices", ["support"] = "Technical support" }))
            .WithQuestion(urgency, new JevScoreQuestion("Assess urgency.", ["A routine nonurgent request.", "An urgent active outage."]))
            .WithQuestion(review, new JevNoulQuestion("Does this require human review?"));
        JevDecisionResponse response = await client.EvaluateAsync(request);

        Assert.False(string.IsNullOrWhiteSpace(response.Model));
        Assert.Equal(3, response.Answers.Count);
        Assert.Contains(response.GetAnswer(category).Choice, new[] { "billing", "support" });
        Assert.Equal(2, response.GetAnswer(category).Probabilities.Count);
        Assert.InRange(response.GetAnswer(urgency).Score, 0, 1);
        Assert.InRange(response.GetAnswer(review).Probability, 0, 1);
        Assert.NotNull(response.RawRepresentation);
    }

    [LiveFact]
    public async Task AdvancedStructuredAndNullableContractsAreExplicitlyProbed()
    {
        using JevClient client = CreateClient();
        var choice = new JevQuestionKey<JevChoiceAnswer>("choice");
        var score = new JevQuestionKey<JevScoreAnswer>("score");
        var request = new JevDecisionRequest(JevJson.Parse("""{"subject":"Synthetic invoice inquiry","tags":["billing"]}"""))
            .WithQuestion(choice, new JevChoiceQuestion(null, new Dictionary<string, JsonElement>
            {
                ["billing"] = JevJson.Parse("""{"scope":["payments","invoices"]}"""),
                ["other"] = JevJson.Parse("null")
            }))
            .WithQuestion(score, new JevScoreQuestion(JevJson.Text("Assess urgency."),
            [
                JevJson.Parse("""{"level":"Routine request without an outage"}"""),
                JevJson.Parse("""{"level":"Active business-critical outage"}""")
            ]));
        JevDecisionResponse response = await client.EvaluateAsync(request);
        Assert.Equal(2, response.GetAnswer(score).Legend.Count);
        Assert.All(response.GetAnswer(score).Legend.Values, value => Assert.Equal(JsonValueKind.Object, value.ValueKind));
        Assert.Equal(2, response.GetAnswer(choice).Probabilities.Count);
    }

    private static JevClient CreateClient()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets<OfficialServiceTests>(optional: true).AddEnvironmentVariables().Build();
        string key = configuration["Jev:ApiKey"] ??
            throw new InvalidOperationException("Live tests were explicitly enabled, but Jev:ApiKey is missing. Configure the shared user-secrets store.");
        return new JevClient(new JevClientOptions
        {
            ApiKey = key,
            DefaultModel = configuration["Jev:DefaultModel"] ?? JevModels.Version1_13_0,
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(30)
        });
    }
}

internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("JEV_RUN_LIVE") != "1")
            Skip = "Live Jev tests require explicit JEV_RUN_LIVE=1 and an official key in user-secrets.";
    }
}
