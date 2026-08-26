using System.IO;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Models;

namespace MolaGPT.Desktop.Services;

/// <summary>What the current provider/model combination can accept.</summary>
public readonly record struct AttachmentIntakeCapabilities(bool AcceptsImages, bool AcceptsOpaqueFiles);

/// <summary>Either a queued attachment or the reason it was refused.</summary>
public sealed record AttachmentIntakeResult(Attachment? Attachment, string? Error)
{
    public static AttachmentIntakeResult Rejected(string error) => new(null, error);
}

/// <summary>
/// The single gate every attachment passes through — file picker, clipboard
/// paste and drag-and-drop all funnel here, so the size limit, format sniffing
/// and capability checks cannot drift apart between entry points.
/// </summary>
public static class AttachmentIntake
{
    /// <summary>Hard ceiling on a single attachment. Generous for a desktop app,
    /// but bounded: everything here is held in memory and copied into the
    /// content-addressed store.</summary>
    public const long MaxAttachmentBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Normalizes an image before it is queued: EXIF orientation, 2000px cap,
    /// JPEG fallback when the bytes are too big.
    ///
    /// Injected because decoding an image needs a UI framework's imaging stack —
    /// WPF's BitmapSource in the old shell, Skia in the Avalonia one — and this
    /// project deliberately has neither. The shell installs its own at startup.
    /// Left unset, images are passed through untouched rather than refused: a
    /// slightly oversized upload is a better failure than a dead attach button.
    /// </summary>
    public static Func<byte[], string?, string?, ProcessedImage>? ImageNormalizer { get; set; }

    public static AttachmentIntakeResult FromFile(string path, AttachmentIntakeCapabilities capabilities)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return AttachmentIntakeResult.Rejected($"文件不存在：{path}");
            if (info.Length == 0) return AttachmentIntakeResult.Rejected($"{info.Name} 是空文件。");
            if (info.Length > MaxAttachmentBytes)
                return AttachmentIntakeResult.Rejected(
                    $"{info.Name} 有 {FormatSize(info.Length)}，超过单个附件 {FormatSize(MaxAttachmentBytes)} 的上限。");

            return FromBytes(File.ReadAllBytes(path), info.Name, capabilities);
        }
        catch (Exception ex)
        {
            return AttachmentIntakeResult.Rejected($"无法读取 {path}：{ex.Message}");
        }
    }

    public static AttachmentIntakeResult FromBytes(
        byte[] bytes,
        string fileName,
        AttachmentIntakeCapabilities capabilities)
    {
        if (bytes is not { Length: > 0 })
            return AttachmentIntakeResult.Rejected($"{fileName} 是空文件。");

        if (AttachmentMime.SniffImageMime(bytes) is not null)
            return BuildImage(bytes, fileName, capabilities);

        return BuildFile(bytes, fileName, capabilities);
    }

    private static AttachmentIntakeResult BuildImage(
        byte[] bytes,
        string fileName,
        AttachmentIntakeCapabilities capabilities)
    {
        if (!capabilities.AcceptsImages)
        {
            return AttachmentIntakeResult.Rejected(
                "当前模型不支持图片识别。请在模型配置中开启「视觉」，或切换到支持多模态的模型。");
        }

        var processed = ImageNormalizer?.Invoke(bytes, null, fileName)
                        ?? new ProcessedImage(bytes, AttachmentMime.SniffImageMime(bytes) ?? "image/png");
        if (processed.Error is not null) return AttachmentIntakeResult.Rejected(processed.Error);

        return new AttachmentIntakeResult(
            new Attachment(AttachmentKind.Image, processed.MimeType, processed.Bytes, FileName: fileName),
            null);
    }

    private static AttachmentIntakeResult BuildFile(
        byte[] bytes,
        string fileName,
        AttachmentIntakeCapabilities capabilities)
    {
        var kind = AttachmentMime.ClassifyDocument(null, fileName, bytes);

        if (kind == AttachmentDocumentKind.LegacyOffice)
        {
            return AttachmentIntakeResult.Rejected(
                $"{fileName} 是 2007 年以前的 Office 二进制格式，无法可靠地提取文字。"
                + "请用 Office 另存为 .docx / .xlsx / .pptx 后再上传。");
        }

        if (kind == AttachmentDocumentKind.Opaque && !capabilities.AcceptsOpaqueFiles)
        {
            return AttachmentIntakeResult.Rejected(
                $"无法从 {fileName} 中提取文字。这类文件需要开启「Python 代码执行」工具才能处理，"
                + "或登录 MolaGPT 账号使用沙箱上传。文本、PDF、Word/Excel/PPT 可以直接上传。");
        }

        return new AttachmentIntakeResult(
            new Attachment(AttachmentKind.File, ResolveFileMime(kind, fileName), bytes, FileName: fileName),
            null);
    }

    private static string ResolveFileMime(AttachmentDocumentKind kind, string fileName) => kind switch
    {
        AttachmentDocumentKind.Pdf => "application/pdf",
        AttachmentDocumentKind.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        AttachmentDocumentKind.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        AttachmentDocumentKind.Pptx => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        AttachmentDocumentKind.Text => TextMime(fileName),
        _ => "application/octet-stream"
    };

    private static string TextMime(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" or ".jsonl" => "application/json",
            ".html" or ".htm" => "text/html",
            ".csv" => "text/csv",
            ".xml" or ".svg" => "text/xml",
            ".md" or ".markdown" => "text/markdown",
            _ => "text/plain"
        };

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MB"
        : $"{bytes / 1024d:0.#} KB";
}
