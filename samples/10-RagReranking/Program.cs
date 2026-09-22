using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ElBruno.AI.Jev;
using Jev.Samples;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    const string query = "How do I register an IChatClient with dependency injection?";
    var candidates = new[]
    {
        (Id: "doc-a", Text: "Microsoft.Extensions.AI chat clients can be registered with the service collection."),
        (Id: "doc-b", Text: "A guide to growing tomatoes in winter."),
        (Id: "doc-c", Text: "Generic Host provides dependency injection and application configuration.")
    };
    var scored = new ConcurrentBag<(string Id, double Probability)>();
    await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
        async (candidate, token) =>
        {
            var relevant = new JevQuestionKey<JevNoulAnswer>("relevant");
            var state = new JsonObject { ["query"] = query, ["passage"] = candidate.Text };
            JevDecisionResponse response = await client.EvaluateAsync(
                new JevDecisionRequest(JevJson.Parse(state.ToJsonString()))
                    .WithQuestion(relevant, new JevNoulQuestion("Is the passage useful for answering the query?")), token);
            scored.Add((candidate.Id, response.GetAnswer(relevant).Probability));
        });
    foreach (var item in scored.OrderByDescending(item => item.Probability).ThenBy(item => item.Id, StringComparer.Ordinal))
        Console.WriteLine($"{item.Id}: {item.Probability:F3}");
    Console.WriteLine("Three independent decisions, at most two in flight. No native rerank or embedding endpoint.");
});
