namespace MolaGPT.Core.Models;

/// <summary>
/// User-attached file/image. v1 supports image attachments via base64 image_url
/// (OpenAI/Anthropic/Gemini all accept this shape).
/// </summary>
public sealed record Attachment(
    AttachmentKind Kind,
    string MimeType,
    byte[] Bytes,
    string? FileName = null,
    string? RemoteUrl = null,
    string? SandboxPath = null)
{
    public bool IsImage => Kind == AttachmentKind.Image;
}

public enum AttachmentKind
{
    Image,
    File
}
