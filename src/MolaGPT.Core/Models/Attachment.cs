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
    string? SandboxPath = null,
    string? WorkspaceRelativePath = null)
{
    public bool IsImage => Kind == AttachmentKind.Image;

    /// <summary>True when this (BYOK) file attachment has been copied into the
    /// per-conversation Python workspace and should be referenced by path rather
    /// than inlined into the prompt. Binary documents (PDF/DOCX/…) and oversized
    /// text files take this route so the model reads them via the python tool.</summary>
    public bool IsWorkspaceFile => Kind == AttachmentKind.File && !string.IsNullOrWhiteSpace(WorkspaceRelativePath);
}

public enum AttachmentKind
{
    Image,
    File
}
