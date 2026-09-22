using System.Text.Json;
using System.Text.Json.Serialization;
using ElBruno.AI.Jev;
using Jev.Samples;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    JsonElement state = JevJson.From(new Ticket("Duplicate payment", ["invoice", "refund"]), SampleJsonContext.Default.Ticket);
    var category = new JevQuestionKey<JevChoiceAnswer>("category");
    var request = new JevDecisionRequest(state).WithQuestion(category,
        new JevChoiceQuestion(JevJson.Parse("""{"task":"Choose a team","policy":"Use the supplied evidence"}"""),
            new Dictionary<string, JsonElement>
            {
                ["billing"] = JevJson.Parse("""{"owns":["invoices","payments","refunds"]}"""),
                ["other"] = JevJson.Parse("null")
            }));
    Console.WriteLine((await client.EvaluateAsync(request, cancellationToken)).GetAnswer(category).Choice);
});

internal sealed record Ticket(string Subject, string[] Tags);

[JsonSerializable(typeof(Ticket))]
internal partial class SampleJsonContext : JsonSerializerContext;
