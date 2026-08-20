using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Builds the OpenAI-compatible multimodal <c>messages[].content</c> payload
/// used by MolaGPT web when uploads are present: plain messages stay strings,
/// attached messages become ordered text/image_url parts. File attachments are
/// collapsed into a single trailing text part built by
/// <see cref="AttachedFilePrompt"/>.
///
/// When <c>replaceImagesWithText</c> is set (non-vision model + vision proxy
/// enabled), images are emitted as <c>[图片#N]</c> placeholders. The number N
/// is a <b>global running ordinal across all messages</b> (threaded via the
/// <c>imageOrdinal</c> ref) so it matches the flat order in which
/// <see cref="Tools.Vision.VisionProxyTool"/> enumerates user-message images —
/// the model says "图#2" and the tool's <c>image_index 2</c> resolve to the
/// same picture even across multi-turn history. An image whose bytes are gone
/// still consumes its ordinal, so a lost picture shifts nothing.
/// </summary>
public static class OpenAiMessageContentBuilder
{
    public static object Build(ChatMessage message, bool replaceImagesWithText = false)
    {
        var ordinal = 0;
        return Build(message, replaceImagesWithText, ref ordinal);
    }

    public static object Build(ChatMessage message, bool replaceImagesWithText, ref int imageOrdinal) =>
        Build(message, replaceImagesWithText, ref imageOrdinal, AttachmentPromptOptions.Default);

    public static object Build(
        ChatMessage message,
        bool replaceImagesWithText,
        ref int imageOrdinal,
        AttachmentPromptOptions options)
    {
        if (message.Attachments is null || message.Attachments.Count == 0)
            return message.Content;

        var parts = new List<object>();
        var text = message.AsText();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(new { type = "text", text });

        foreach (var attachment in message.Attachments)
        {
            if (attachment.Kind != AttachmentKind.Image) continue;

            imageOrdinal++;
            if (attachment.IsUnavailable)
            {
                parts.Add(new
                {
                    type = "text",
                    text = UnavailableImageNote(attachment, imageOrdinal)
                });
                continue;
            }

            if (replaceImagesWithText)
            {
                parts.Add(new { type = "text", text = ImagePlaceholder(attachment, imageOrdinal) });
                continue;
            }

            var url = !string.IsNullOrWhiteSpace(attachment.RemoteUrl)
                ? attachment.RemoteUrl!
                : $"data:{attachment.MimeType};base64,{Convert.ToBase64String(attachment.Bytes)}";
            parts.Add(new { type = "image_url", image_url = new { url } });
        }

        var files = message.Attachments.Where(a => a.Kind == AttachmentKind.File).ToList();
        var fileSection = AttachedFilePrompt.Build(files, options);
        if (!string.IsNullOrWhiteSpace(fileSection))
            parts.Add(new { type = "text", text = fileSection });

        return parts.Count == 0 ? message.Content : parts;
    }

    /// <summary>Placeholder shown to a non-vision model that can reach the image
    /// through the vision proxy tool.</summary>
    public static string ImagePlaceholder(Attachment attachment, int ordinal) =>
        string.IsNullOrWhiteSpace(attachment.FileName)
            ? $"[图片#{ordinal}]"
            : $"[图片#{ordinal}: {attachment.FileName}]";

    /// <summary>
    /// Model-visible stand-in for an image whose bytes could not be loaded.
    /// Dropping it instead would leave the user looking at an attachment chip
    /// the model never received, with no way for either side to notice.
    /// </summary>
    public static string UnavailableImageNote(Attachment attachment, int ordinal) =>
        $"[图片#{ordinal}: {attachment.DisplayName} — 不可用：{attachment.UnavailableReason}]";
}
