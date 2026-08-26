using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Desktop.Services;
using SkiaSharp;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// Skia port of MolaGPT.Desktop/Services/ImageAttachmentProcessor.
///
/// Same contract and same thresholds as the WPF original — 2000px longest edge,
/// 3.5 MB encoded ceiling, JPEG quality 88 — so an image queued from either
/// shell reaches the provider as the same bytes. Only the decoder differs:
/// BitmapFrame there, SKCodec here. Skia comes in with Avalonia, so this adds no
/// dependency.
///
/// One behavioural difference worth knowing: Skia applies EXIF orientation
/// during decode (SKCodec reports it and SKBitmap.Decode honours it), where WPF
/// required an explicit transform. The result is the same upright image; there
/// is simply no orientation branch here to get wrong.
/// </summary>
internal static class SkiaImageNormalizer
{
    public const int MaxDimension = 2000;
    public const int MaxEncodedBytes = 3_500_000;
    private const int JpegQuality = 88;

    public static ProcessedImage Process(byte[] bytes, string? declaredMime, string? fileName)
    {
        // The bytes decide the format. A .png that is really a JPEG, or a camera
        // .jpg that is really HEIC, would otherwise be announced wrongly.
        var mime = AttachmentMime.SniffImageMime(bytes) ?? declaredMime ?? "application/octet-stream";

        // Animated GIFs cannot survive a re-encode through a single frame, and
        // they are already an accepted inline format.
        if (mime == "image/gif") return new ProcessedImage(bytes, mime);

        SKBitmap? bitmap = null;
        try
        {
            bitmap = SKBitmap.Decode(bytes);
        }
        catch
        {
            bitmap = null;
        }

        if (bitmap is null)
        {
            // No usable codec. If the format is one providers accept we can still
            // pass the original through untouched; otherwise it is a dead end.
            return AttachmentMime.IsInlineSafeImageMime(mime)
                ? new ProcessedImage(bytes, mime)
                : ProcessedImage.Failed(DescribeDecodeFailure(mime, fileName));
        }

        using (bitmap)
        {
            var longest = Math.Max(bitmap.Width, bitmap.Height);
            var scale = longest > 0 ? Math.Min(1.0, (double)MaxDimension / longest) : 1.0;
            var needsTranscode = !AttachmentMime.IsInlineSafeImageMime(mime);

            if (scale >= 1.0 && !needsTranscode && bytes.Length <= MaxEncodedBytes)
                return new ProcessedImage(bytes, mime);

            using var scaled = scale < 1.0
                ? bitmap.Resize(
                    new SKImageInfo(
                        Math.Max(1, (int)Math.Round(bitmap.Width * scale)),
                        Math.Max(1, (int)Math.Round(bitmap.Height * scale))),
                    new SKSamplingOptions(SKCubicResampler.Mitchell))
                : bitmap.Copy();

            if (scaled is null)
                return new ProcessedImage(bytes, mime);

            // Alpha only survives in PNG, and only PNG is worth paying for when
            // the source actually has transparency; everything else goes to
            // JPEG, which is dramatically smaller for photographic content.
            var preferPng = scaled.Info.AlphaType != SKAlphaType.Opaque && mime != "image/jpeg";
            var encoded = Encode(scaled, preferPng);

            if (encoded is null) return new ProcessedImage(bytes, mime);

            // A re-encode that came out bigger is not an improvement.
            return encoded.Length < bytes.Length || needsTranscode || scale < 1.0
                ? new ProcessedImage(encoded, preferPng ? "image/png" : "image/jpeg")
                : new ProcessedImage(bytes, mime);
        }
    }

    private static byte[]? Encode(SKBitmap bitmap, bool png)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = png
            ? image.Encode(SKEncodedImageFormat.Png, 100)
            : image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data?.ToArray();
    }

    private static string DescribeDecodeFailure(string mime, string? fileName) => mime switch
    {
        "image/heic" or "image/heif" =>
            $"无法解码 {fileName ?? "该图片"}：系统缺少 HEIF 图像扩展。请先转换为 JPG/PNG 再上传。",
        "image/avif" =>
            $"无法解码 {fileName ?? "该图片"}：系统缺少 AVIF 图像扩展。请先转换为 JPG/PNG 再上传。",
        _ => $"无法解码 {fileName ?? "该图片"}（{mime}）。请先转换为 JPG/PNG 再上传。"
    };
}
