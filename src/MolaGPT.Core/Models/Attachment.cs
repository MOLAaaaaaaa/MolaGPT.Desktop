namespace MolaGPT.Core.Models;

/// <summary>
/// Text extracted from a file attachment, plus everything the model needs to
/// judge how complete that text is. A note and a body are not exclusive: a PDF
/// can yield text <em>and</em> a warning that six of its pages were scans.
/// </summary>
/// <param name="Body">Extracted text, or null when extraction produced nothing.</param>
/// <param name="PageCount">Source page count, when the format has one.</param>
/// <param name="Note">Model-visible explanation of a failure or partial result.</param>
/// <param name="TextFileRelativePath">Workspace-relative path of the full extracted
/// text, written as a sidecar when the body is too large to inline so the model can
/// page through the remainder with <c>read_file</c>.</param>
public sealed record AttachmentText(
    string? Body,
    int? PageCount = null,
    string? Note = null,
    string? TextFileRelativePath = null)
{
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
}

/// <summary>
/// User-attached file/image. Images travel as base64 (or, in MolaGPT-account
/// mode, as an uploaded URL); files travel as extracted text plus a workspace
/// path the model can reach with the local file and Python tools.
/// </summary>
public sealed record Attachment(
    AttachmentKind Kind,
    string MimeType,
    byte[] Bytes,
    string? FileName = null,
    string? RemoteUrl = null,
    string? SandboxPath = null,
    string? WorkspaceRelativePath = null,
    AttachmentText? Text = null,
    string? UnavailableReason = null)
{
    public bool IsImage => Kind == AttachmentKind.Image;

    /// <summary>True when the attachment's bytes could not be recovered (the
    /// local copy was deleted, the store was cleared). Such an attachment is
    /// still carried through the request so the model is told it is missing —
    /// and, for images, so the global <c>[图片#N]</c> numbering does not shift.</summary>
    public bool IsUnavailable => !string.IsNullOrWhiteSpace(UnavailableReason);

    /// <summary>True when a copy of this file lives in the per-conversation
    /// Python workspace and can therefore be reached by path from
    /// <c>read_file</c> / <c>execute_python_code</c>.</summary>
    public bool IsWorkspaceFile => Kind == AttachmentKind.File && !string.IsNullOrWhiteSpace(WorkspaceRelativePath);

    public string DisplayName => string.IsNullOrWhiteSpace(FileName) ? "附件" : FileName!;
}

public enum AttachmentKind
{
    Image,
    File
}
