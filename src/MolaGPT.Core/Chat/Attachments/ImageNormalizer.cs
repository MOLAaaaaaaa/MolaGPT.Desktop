namespace MolaGPT.Core.Chat.Attachments;

/// <summary>Bytes ready to put on the wire, or the reason they are not.</summary>
public sealed record NormalizedImage(byte[] Bytes, string MimeType, string? Error = null)
{
    public bool Failed => Error is not null;

    public static NormalizedImage Rejected(string error) => new(Array.Empty<byte>(), string.Empty, error);
}

/// <summary>
/// Platform hook for downscaling and transcoding an image before it is sent.
///
/// Core cannot do this itself — decoding pictures needs the host's imaging
/// stack (on Windows, WPF's <c>BitmapFrame</c> and whatever codecs are
/// installed). Tools that read images off disk therefore take one of these
/// instead of hard-coding a resize, and degrade to sending the original bytes
/// when the host supplies nothing.
/// </summary>
/// <param name="bytes">Raw file content.</param>
/// <param name="declaredMime">MIME sniffed from the content, when known.</param>
/// <param name="fileName">Used only for diagnostics in the returned error.</param>
public delegate NormalizedImage ImageNormalizer(byte[] bytes, string? declaredMime, string? fileName);
