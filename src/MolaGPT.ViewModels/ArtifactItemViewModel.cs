using MolaGPT.Core.Chat.Tools.PythonExecution;

namespace MolaGPT.ViewModels;

/// <summary>
/// One entry in the session-level artifact panel: a file produced (or uploaded)
/// in the conversation's Python working directory. Images expose their path for
/// inline thumbnailing (XAML binds an <c>Image</c> to <see cref="FullPath"/> via
/// a file URI); other types fall back to a glyph + filename.
/// </summary>
public sealed class ArtifactItemViewModel
{
    public ArtifactItemViewModel(WorkspaceArtifact artifact)
    {
        FileName = artifact.Name;
        RelativePath = artifact.RelativePath;
        FullPath = artifact.FullPath;
        Bytes = artifact.Bytes;
        IsImage = artifact.IsImage;
        Extension = System.IO.Path.GetExtension(artifact.Name).TrimStart('.').ToUpperInvariant();
    }

    public string FileName { get; }
    public string RelativePath { get; }
    public string FullPath { get; }
    public long Bytes { get; }
    public bool IsImage { get; }

    /// <summary>Upper-cased extension without the dot, e.g. "PNG", "CSV". Empty
    /// when the file has no extension. Drives the non-image type badge.</summary>
    public string Extension { get; }

    /// <summary>Human-readable size, e.g. "12.3 KB". Shown under the file name.</summary>
    public string SizeLabel => FormatSize(Bytes);

    public string ToolTip => $"{FileName}\n{SizeLabel}\n{FullPath}";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024d;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024d:0.#} GB";
    }
}
