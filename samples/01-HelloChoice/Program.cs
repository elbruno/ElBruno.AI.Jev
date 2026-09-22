using ElBruno.AI.Jev;
using Jev.Samples;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    var category = new JevQuestionKey<JevChoiceAnswer>("category");
    var request = new JevDecisionRequest("Please correct the invoice for my order.")
        .WithQuestion(category, new JevChoiceQuestion("Choose the responsible team.",
            new Dictionary<string, string?>
            {
                ["billing"] = "Invoices, payments, and refunds",
                ["support"] = "Technical support",
                ["other"] = "None of these teams applies"
            }));
    JevDecisionResponse response = await client.EvaluateAsync(request, cancellationToken);
    JevChoiceAnswer answer = response.GetAnswer(category);
    Console.WriteLine($"Model: {response.Model}; choice: {answer.Choice}; confidence: {answer.Confidence:F3}");
    foreach ((string label, double probability) in answer.Probabilities)
        Console.WriteLine($"  {label}: {probability:F3}");
});
