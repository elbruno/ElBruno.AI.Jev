using ElBruno.AI.Jev;
using ElBruno.AI.Jev.ExtensionsAI;
using Jev.Samples;
using Microsoft.Extensions.AI;

return await SampleConfiguration.RunAsync(args, async (client, cancellationToken) =>
{
    Console.WriteLine("Chat generation is a deterministic demo. All output is buffered before the Jev assessment.");
    var violates = new JevQuestionKey<JevNoulAnswer>("violates");
    using var demoChat = new DemoChatClient("reviewed");
    using var reviewed = new JevAssessmentChatClient(demoChat, client, new JevAssessmentOptions
    {
        CreateRequest = text => new JevDecisionRequest(text)
            .WithQuestion(violates, new JevNoulQuestion("Does this text contain a personal insult?")),
        Allow = result => result.GetAnswer(violates).Probability < 0.5,
        Mode = JevAssessmentMode.BufferedOutputReview,
        MaxBufferedUpdates = 100,
        MaxBufferedCharacters = 10_000
    });
    await foreach (ChatResponseUpdate update in reviewed.GetStreamingResponseAsync(
        [new ChatMessage(ChatRole.User, "Please explain this demonstration.")], cancellationToken: cancellationToken))
        Console.Write(update.Text);
    Console.WriteLine();
    Console.WriteLine("Threshold 0.5 is demonstration-only. Output review is not input review or a security guarantee.");
});
