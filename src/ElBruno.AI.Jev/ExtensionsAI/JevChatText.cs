using Microsoft.Extensions.AI;

namespace ElBruno.AI.Jev.ExtensionsAI;

/// <summary>Explicit textual projection for decision state; never silently drops nontext content.</summary>
public static class JevChatText
{
    /// <summary>Extracts every message's text with its role. Rejects images, tool calls/results, reasoning and other nontext content.</summary>
    /// <remarks>
    /// Supply a custom selector to a wrapper when an application deliberately supports a different projection,
    /// for example selecting the latest user text from a conversation containing tool history.
    /// The original content is still forwarded to the chat client. Role prefixes are context, not a security boundary.
    /// </remarks>
    /// <exception cref="NotSupportedException">A message contains anything other than <see cref="TextContent"/>.</exception>
    public static string Extract(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var parts = new List<string>(messages.Count);
        foreach (ChatMessage message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Contents.Any(content => content is not TextContent))
            {
                throw new NotSupportedException(
                    "The default Jev text selector only supports TextContent. Configure an explicit selector for other content.");
            }

            parts.Add($"{message.Role}: {message.Text}");
        }

        return string.Join("\n", parts);
    }
}

internal static class JevChatSnapshots
{
    internal static IReadOnlyList<ChatMessage> Messages(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return Array.AsReadOnly(messages.Select(message =>
        {
            ArgumentNullException.ThrowIfNull(message);
            ChatMessage copy = message.Clone();
            copy.Contents = Contents(message.Contents);
            copy.AdditionalProperties = message.AdditionalProperties?.Clone();
            return copy;
        }).ToArray());
    }

    internal static ChatResponseUpdate Update(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ChatResponseUpdate copy = update.Clone();
        copy.Contents = Contents(update.Contents);
        copy.AdditionalProperties = update.AdditionalProperties?.Clone();
        return copy;
    }

    internal static ChatResponse Response(ChatResponse response) => new(Messages(response.Messages).ToList())
    {
        ResponseId = response.ResponseId,
        ConversationId = response.ConversationId,
        ModelId = response.ModelId,
        CreatedAt = response.CreatedAt,
        FinishReason = response.FinishReason,
        Usage = response.Usage,
        ContinuationToken = response.ContinuationToken,
        RawRepresentation = response.RawRepresentation,
        AdditionalProperties = response.AdditionalProperties?.Clone()
    };

    private static List<AIContent> Contents(IEnumerable<AIContent> contents) =>
        contents.Select(content => content is TextContent text
            ? new TextContent(text.Text)
            {
                Annotations = text.Annotations?.ToList(),
                RawRepresentation = text.RawRepresentation,
                AdditionalProperties = text.AdditionalProperties?.Clone()
            }
            : content).ToList();
}
