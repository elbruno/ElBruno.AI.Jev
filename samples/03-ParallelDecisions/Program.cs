using ElBruno.AI.Jev;
using Jev.Samples;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    var category = new JevQuestionKey<JevChoiceAnswer>("category");
    var urgency = new JevQuestionKey<JevScoreAnswer>("urgency");
    var escalation = new JevQuestionKey<JevNoulAnswer>("escalation");
    var request = new JevDecisionRequest("We were charged twice and cannot complete today's purchase.")
        .WithQuestion(category, new JevChoiceQuestion("Choose a team.", new Dictionary<string, string?> { ["billing"] = "Payments", ["support"] = "Technical faults" }))
        .WithQuestion(urgency, new JevScoreQuestion("Assess urgency.", ["Can wait until next week.", "Needs attention today.", "Needs immediate attention."]))
        .WithQuestion(escalation, new JevNoulQuestion("Is human review needed?"));
    JevDecisionResponse response = await client.EvaluateAsync(request, cancellationToken);
    Console.WriteLine($"Team: {response.GetAnswer(category).Choice}");
    Console.WriteLine($"Urgency: {response.GetAnswer(urgency).Score:F3}; review probability: {response.GetAnswer(escalation).Probability:F3}");
    Console.WriteLine($"Input tokens: {response.Usage.InputTokens?.ToString() ?? "unknown"}; request: {response.RequestId ?? "not supplied"}");
    Console.WriteLine("These are independent questions over one state, not dependent execution steps.");
});
