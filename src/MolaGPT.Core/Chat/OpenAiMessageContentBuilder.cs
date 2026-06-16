using System.IO;
using System.Text;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Builds the OpenAI-compatible multimodal <c>messages[].content</c> payload
/// used by MolaGPT web when uploads are present: plain messages stay strings,
/// attached messages become ordered text/image_url parts.
///
/// When <c>replaceImagesWithText</c> is set (non-vision model + vision proxy
/// enabled), images are emitted as <c>[图片#N]</c> placeholders. The number N
/// is a <b>global running ordinal across all messages</b> (threaded via the
/// <c>imageOrdinal</c> ref) so it matches the flat order in which
/// <see cref="Tools.Vision.VisionProxyTool"/> enumerates user-message images —
/// the model says "图#2" and the tool's <c>image_index 2</c> resolve to the
/// same picture even across multi-turn history.
/// </summary>
public static class OpenAiMessageContentBuilder
{
    private const int MaxInlineTextAttachmentBytes = 128 * 1024;

    /// <summary>Byte ceiling for inlining a text attachment's content directly
    /// into the prompt. Larger text files are better read on demand via the tool;
    /// callers deciding inline-vs-workspace routing reuse this bound.</summary>
    public static int MaxInlineTextBytes => MaxInlineTextAttachmentBytes;

    public static object Build(ChatMessage message, bool replaceImagesWithText = false)
    {
        var ordinal = 0;
        return Build(message, replaceImagesWithText, ref ordinal);
    }

    public static object Build(ChatMessage message, bool replaceImagesWithText, ref int imageOrdinal)
    {
        if (message.Attachments is null || message.Attachments.Count == 0)
            return message.Content;

        var parts = new List<object>();
        var text = message.AsText();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(new { type = "text", text });

        foreach (var attachment in message.Attachments)
        {
            if (attachment.Kind == AttachmentKind.Image)
            {
                imageOrdinal++;
                if (replaceImagesWithText)
                {
                    var label = string.IsNullOrWhiteSpace(attachment.FileName)
                        ? $"[图片#{imageOrdinal}]"
                        : $"[图片#{imageOrdinal}: {attachment.FileName}]";
                    parts.Add(new
                    {
                        type = "text",
                        text = label
                    });
                    continue;
                }

                var url = !string.IsNullOrWhiteSpace(attachment.RemoteUrl)
                    ? attachment.RemoteUrl!
                    : $"data:{attachment.MimeType};base64,{Convert.ToBase64String(attachment.Bytes)}";
                parts.Add(new { type = "image_url", image_url = new { url } });
                continue;
            }

            parts.Add(new
            {
                type = "text",
                text = BuildFileTextPart(attachment)
            });
        }

        return parts;
    }

    public static string BuildFileTextPart(Attachment attachment)
    {
        var name = string.IsNullOrWhiteSpace(attachment.FileName) ? "附件" : attachment.FileName!;

        // Files copied into the per-conversation Python workspace are referenced
        // by path, not inlined: binary documents (PDF/DOCX/…) would be garbage as
        // UTF-8, and oversized text is better read on demand by the tool. The
        // model is told where the file lives so it can open it with python.
        if (attachment.IsWorkspaceFile)
        {
            return $"用户上传了文件：{name}（{attachment.MimeType}，{attachment.Bytes.Length} bytes）。"
                + $"该文件已复制到当前 Python 工作目录，相对路径为 {attachment.WorkspaceRelativePath}。"
                + "如需读取或处理它，请调用 execute_python_code 工具，用该相对路径 open() 即可。";
        }

        if (!IsTextLike(attachment.MimeType, name))
            return $"用户上传了文件：{name}（{attachment.MimeType}，{attachment.Bytes.Length} bytes）。";

        var bytes = attachment.Bytes;
        var truncated = bytes.Length > MaxInlineTextAttachmentBytes;
        if (truncated) bytes = bytes[..MaxInlineTextAttachmentBytes];

        var body = Encoding.UTF8.GetString(bytes);
        return truncated
            ? $"用户上传了文件：{name}\n\n{body}\n\n[文件内容过长，已截断]"
            : $"用户上传了文件：{name}\n\n{body}";
    }

    /// <summary>
    /// True when a file attachment's bytes can be sent to the model directly as
    /// UTF-8 text (markdown, source code, HTML, structured data). Used both for
    /// inlining here and by the composer to decide which BYOK files go inline vs.
    /// get copied into the Python workspace for tool-based reading.
    /// </summary>
    public static bool IsTextLike(string mimeType, string fileName)
    {
        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        if (mimeType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) return true;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".md" or ".txt" or ".json" or ".csv" or ".xml" or ".yaml" or ".yml"
            or ".html" or ".htm"
            or ".py" or ".js" or ".ts" or ".tsx" or ".jsx" or ".cs" or ".java"
            or ".go" or ".rs" or ".c" or ".cpp" or ".h" or ".hpp" or ".m";
    }
}
