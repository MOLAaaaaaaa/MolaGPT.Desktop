using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MolaGPT.Core.Chat.Attachments;

namespace MolaGPT.Desktop.Services;

/// <summary>Outcome of normalising a picked image for the wire.</summary>
public sealed record ProcessedImage(byte[] Bytes, string MimeType)
{
    /// <summary>Set when the image could not be decoded — WebP/HEIC decoding
    /// depends on optional Windows codec packages, and guessing is worse than
    /// telling the user which conversion to do.</summary>
    public string? Error { get; init; }

    public static ProcessedImage Failed(string error) => new(Array.Empty<byte>(), string.Empty) { Error = error };
}

/// <summary>
/// Prepares an image attachment for inline delivery: honours EXIF orientation,
/// caps the pixel and byte size, and transcodes formats providers do not accept.
///
/// This runs once, when the image is picked, so the normalised bytes are what
/// gets previewed, stored and re-sent on every later turn — the work is never
/// repeated per request.
/// </summary>
public static class ImageAttachmentProcessor
{
    /// <summary>Longest edge kept. Beyond this, providers downscale server-side
    /// anyway, so the extra pixels only cost upload time and tokens.</summary>
    public const int MaxDimension = 2000;

    /// <summary>Encoded-byte ceiling. Base64 inflates by ~4/3, which keeps the
    /// request under Anthropic's 5MB per-image limit.</summary>
    public const int MaxEncodedBytes = 3_500_000;

    private const int JpegQuality = 88;
    private const int JpegFallbackQuality = 78;

    public static ProcessedImage Process(byte[] bytes, string? declaredMime, string? fileName)
    {
        // The bytes decide the format. A .png that is really a JPEG, or a camera
        // .jpg that is really HEIC, would otherwise be announced wrongly to the
        // provider and rejected.
        var mime = AttachmentMime.SniffImageMime(bytes) ?? declaredMime ?? "application/octet-stream";

        // Animated GIFs cannot survive a re-encode through a single frame, and
        // they are already an accepted inline format.
        if (mime == "image/gif") return new ProcessedImage(bytes, mime);

        BitmapFrame frame;
        try
        {
            frame = Decode(bytes);
        }
        catch (Exception)
        {
            // No usable codec. If the format is one providers accept we can still
            // pass the original through untouched; otherwise it is a dead end.
            return AttachmentMime.IsInlineSafeImageMime(mime)
                ? new ProcessedImage(bytes, mime)
                : ProcessedImage.Failed(DescribeDecodeFailure(mime, fileName));
        }

        var orientation = ReadExifOrientation(frame);
        var scale = Math.Min(1.0, (double)MaxDimension / Math.Max(frame.PixelWidth, frame.PixelHeight));
        var needsTranscode = !AttachmentMime.IsInlineSafeImageMime(mime);

        if (orientation == 1 && scale >= 1.0 && !needsTranscode && bytes.Length <= MaxEncodedBytes)
            return new ProcessedImage(bytes, mime);

        BitmapSource image = frame;
        if (scale < 1.0)
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        image = ApplyOrientation(image, orientation);

        // Alpha only survives in PNG, and only PNG is worth paying for when the
        // source actually has transparency; everything else goes to JPEG, which
        // is dramatically smaller for photographic content.
        var preferPng = HasAlpha(frame.Format) && mime != "image/jpeg";
        var encoded = preferPng ? EncodePng(image) : EncodeJpeg(image, JpegQuality);
        var encodedMime = preferPng ? "image/png" : "image/jpeg";

        if (encoded.Length > MaxEncodedBytes)
        {
            var reduced = EncodeJpeg(image, JpegFallbackQuality);
            if (reduced.Length < encoded.Length)
            {
                encoded = reduced;
                encodedMime = "image/jpeg";
            }
        }

        // A re-encode is not guaranteed to be smaller (a screenshot PNG can beat
        // JPEG). Keep whichever is smaller, as long as the original was already
        // an accepted format and needed no geometry change.
        if (!needsTranscode && orientation == 1 && scale >= 1.0 && bytes.Length <= encoded.Length)
            return new ProcessedImage(bytes, mime);

        return new ProcessedImage(encoded, encodedMime);
    }

    private static BitmapFrame Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    /// <summary>
    /// Reads EXIF tag 274. Phone cameras store the sensor image unrotated and
    /// record the rotation here; without applying it a portrait photo arrives at
    /// the model lying on its side.
    /// </summary>
    private static int ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata) return 1;
            if (!metadata.ContainsQuery("/app1/ifd/{ushort=274}")) return 1;
            var value = metadata.GetQuery("/app1/ifd/{ushort=274}");
            return value is ushort orientation && orientation is >= 1 and <= 8 ? orientation : 1;
        }
        catch (Exception)
        {
            // Metadata queries throw on containers that do not carry an app1 block.
            return 1;
        }
    }

    private static BitmapSource ApplyOrientation(BitmapSource image, int orientation)
    {
        if (orientation == 1) return image;

        var transform = new TransformGroup();
        // Even orientations are mirrored; the pairs then differ only by rotation.
        if (orientation is 2 or 4 or 5 or 7)
            transform.Children.Add(new ScaleTransform(-1, 1));

        var angle = orientation switch
        {
            3 or 4 => 180,
            5 or 8 => 270,
            6 or 7 => 90,
            _ => 0
        };
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));

        return transform.Children.Count == 0 ? image : new TransformedBitmap(image, transform);
    }

    private static bool HasAlpha(PixelFormat format) =>
        format == PixelFormats.Bgra32
        || format == PixelFormats.Pbgra32
        || format == PixelFormats.Prgba64
        || format == PixelFormats.Rgba64
        || format == PixelFormats.Rgba128Float
        || format == PixelFormats.Indexed8
        || format == PixelFormats.Indexed4
        || format == PixelFormats.Indexed2
        || format == PixelFormats.Indexed1;

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        return Save(encoder);
    }

    private static byte[] EncodeJpeg(BitmapSource image, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        // JPEG has no alpha: composite onto white so transparent regions do not
        // come out black.
        encoder.Frames.Add(BitmapFrame.Create(Flatten(image)));
        return Save(encoder);
    }

    private static BitmapSource Flatten(BitmapSource image)
    {
        if (!HasAlpha(image.Format)) return image;

        var target = new RenderTargetBitmap(
            image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY, PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new System.Windows.Rect(0, 0, image.Width, image.Height);
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, bounds);
            context.DrawImage(image, bounds);
        }
        target.Render(visual);
        return target;
    }

    private static byte[] Save(BitmapEncoder encoder)
    {
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
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
