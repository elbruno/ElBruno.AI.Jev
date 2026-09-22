using ElBruno.AI.Jev;
using Jev.Samples;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    var urgency = new JevQuestionKey<JevScoreAnswer>("urgency");
    var escalation = new JevQuestionKey<JevNoulAnswer>("escalation");
    var request = new JevDecisionRequest("Our checkout has been unavailable for fifteen minutes.")
        .WithQuestion(urgency, new JevScoreQuestion("Assess urgency.",
            ["A minor inconvenience with a workaround.", "A material disruption with partial service.", "An active outage preventing purchases."]))
        .WithQuestion(escalation, new JevNoulQuestion("Does this need a human incident responder?"));
    JevDecisionResponse response = await client.EvaluateAsync(request, cancellationToken);
    Console.WriteLine($"Expected rubric position: {response.GetAnswer(urgency).Score:F3} (not a percentage).");
    double probability = response.GetAnswer(escalation).Probability;
    Console.WriteLine($"Probability of escalation: {probability:F3}");
    Console.WriteLine($"Illustrative application threshold 0.8: {probability >= 0.8}. Calibrate your own threshold.");
});
