using System.Text.Json;
using ElBruno.AI.Jev;
using ElBruno.AI.Jev.ExtensionsAI;
using Jev.Samples;
using Microsoft.Extensions.AI;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    var review = new JevQuestionKey<JevNoulAnswer>("review");
    var tool = new JevDecisionFunction(client, "review_ticket", "Assess whether a ticket needs human review.",
        text => new JevDecisionRequest(text).WithQuestion(review, new JevNoulQuestion("Does this ticket need human review?")));
    Console.WriteLine($"AI function: {tool.Name}; input schema: {tool.JsonSchema}");
    object? result = await tool.InvokeAsync(new AIFunctionArguments { ["state"] = "A customer disputes an invoice." }, cancellationToken);
    if (result is not JsonElement json) throw new InvalidOperationException("The tool did not return its documented JSON result.");
    Console.WriteLine(json.GetRawText());
    Console.WriteLine("The same AIFunction can be supplied in ChatOptions.Tools to an independently configured chat client.");
});
