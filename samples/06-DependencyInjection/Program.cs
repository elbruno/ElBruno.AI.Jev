using ElBruno.AI.Jev;
using Jev.Samples;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    // SampleConfiguration registers AddJev on Generic Host and resolves IJevDecisionClient.
    var service = new TicketService(client);
    Console.WriteLine($"Review probability: {await service.NeedsReviewAsync(cancellationToken):F3}");
});

internal sealed class TicketService(IJevDecisionClient decisions)
{
    internal async Task<double> NeedsReviewAsync(CancellationToken cancellationToken)
    {
        var review = new JevQuestionKey<JevNoulAnswer>("review");
        var response = await decisions.EvaluateAsync(new JevDecisionRequest("A customer disputes a charge.")
            .WithQuestion(review, new JevNoulQuestion("Does this need human review?")), cancellationToken);
        return response.GetAnswer(review).Probability;
    }
}
