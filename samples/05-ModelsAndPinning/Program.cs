using ElBruno.AI.Jev;
using Jev.Samples;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    JevModelList models = await client.ListModelsAsync(cancellationToken);
    foreach (JevModelInfo model in models.Models)
        Console.WriteLine($"{model.Name}: {model.Description} ({model.ReleaseDate ?? "date not supplied"})");
    var relevant = new JevQuestionKey<JevNoulAnswer>("relevant");
    var request = new JevDecisionRequest("A question about C# interfaces.", JevModels.Version1_13_0)
        .WithQuestion(relevant, new JevNoulQuestion("Is this related to .NET?"));
    JevDecisionResponse response = await client.EvaluateAsync(request, cancellationToken);
    Console.WriteLine($"Requested: {request.Model}; resolved: {response.Model}");
    Console.WriteLine("Discovery may list aliases without every accepted pinned version. Recalibrate thresholds when upgrading models.");
});
