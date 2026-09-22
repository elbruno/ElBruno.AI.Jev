using ElBruno.AI.Jev;
using ElBruno.AI.Jev.ExtensionsAI;
using Jev.Samples;
using Microsoft.Extensions.AI;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    Console.WriteLine("Both downstream chat clients are deterministic demos; only the Jev decision can be live.");
    using var fast = new DemoChatClient("fast");
    using var deep = new DemoChatClient("specialist");
    using var router = new JevRoutingChatClient(client,
        new Dictionary<string, IChatClient> { ["Fast"] = fast, ["Deep"] = deep },
        new JevRoutingOptions
        {
            Model = JevModels.Version1_13_0,
            Question = new JevChoiceQuestion("Select the appropriate assistant.", new Dictionary<string, string?>
            {
                ["Fast"] = "Short routine questions",
                ["Deep"] = "Complex multi-step reasoning"
            }),
            MinimumConfidence = 0.6
        });
    ChatResponse response = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "What is a C# interface?")],
        new ChatOptions { MaxOutputTokens = 100 }, cancellationToken);
    Console.WriteLine(response.Text);
    foreach (JevChatDecisionMetadata decision in JevChatMetadata.GetDecisions(response.AdditionalProperties))
        Console.WriteLine($"Selected route: {decision.SelectedRoute}; decision model: {decision.Decision.Model}; chat model: {response.ModelId}");
    Console.WriteLine("The demonstration confidence threshold is application policy. Unknown/low-confidence routes throw; no automatic fallback.");
});
