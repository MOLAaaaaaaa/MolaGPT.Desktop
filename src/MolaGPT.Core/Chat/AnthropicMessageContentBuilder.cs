using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Builds the Anthropic <c>messages[].content</c> payload — the sibling of
/// <see cref="OpenAiMessageContentBuilder"/> for the <c>v1/messages</c> shape,
/// where an image is a <c>{ type: "image", source: { type: "base64", … } }</c>
/// block rather than an <c>image_url</c> part.
///
/// Lives here rather than inside the provider because two callers now need it —
/// the streaming provider and <see cref="OneShotCompletionClient"/> — and a
/// second hand-rolled copy of the image-part shape is exactly how the two would
/// drift into disagreeing about what an unavailable attachment looks like.
/// </summary>
public static class AnthropicMessageContentBuilder
{
    public static object Build(ChatMessage message)
    {
        if (message.Attachments is null || message.Attachments.Count == 0)
            return message.Content;

        var parts = new List<object>();
        var text = message.AsText();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(new { type = "text", text });

        var imageOrdinal = 0;
        foreach (var attachment in message.Attachments)
        {
            if (attachment.Kind != AttachmentKind.Image) continue;

            imageOrdinal++;
            if (attachment.IsUnavailable)
            {
                parts.Add(new
                {
                    type = "text",
                    text = OpenAiMessageContentBuilder.UnavailableImageNote(attachment, imageOrdinal)
                });
                continue;
            }

            parts.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = attachment.MimeType,
                    data = Convert.ToBase64String(attachment.Bytes)
                }
            });
        }

        var fileSection = AttachedFilePrompt.Build(
            message.Attachments.Where(a => a.Kind == AttachmentKind.File).ToList());
        if (!string.IsNullOrWhiteSpace(fileSection))
            parts.Add(new { type = "text", text = fileSection });

        return parts;
    }
}
