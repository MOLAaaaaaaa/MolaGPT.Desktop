namespace MolaGPT.Desktop.Services;

/// <summary>
/// Outcome of normalising a picked image for the wire.
///
/// The record moved here, away from ImageAttachmentProcessor, so that the
/// contract between "something normalized this image" and "something consumed
/// it" does not drag a UI framework's imaging stack along with it. The WPF
/// processor and the Avalonia/Skia one both produce this type.
/// </summary>
public sealed record ProcessedImage(byte[] Bytes, string MimeType)
{
    /// <summary>Set when the image could not be decoded — WebP/HEIC decoding
    /// depends on optional platform codecs, and guessing is worse than telling
    /// the user which conversion to do.</summary>
    public string? Error { get; init; }

    public static ProcessedImage Failed(string error) =>
        new(Array.Empty<byte>(), string.Empty) { Error = error };
}
