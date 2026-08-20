using System.IO;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Chat.Tools.PythonExecution;

namespace MolaGPT.Core.Chat.Tools.Vision;

/// <summary>
/// Turns the file name a model passed to <see cref="ImageAnalysisTool"/> into
/// bytes — or into an error that tells it what it could have asked for instead.
///
/// Inside the working directory this resolves and reads without ceremony. Outside
/// it, the file is located but the decision is not made here: the permission layer
/// sees the resolved path, asks the user, and only then is anything read. That
/// split matters for this tool in particular, because it does not just read — it
/// uploads the bytes to a configured vision endpoint, so the path argument is the
/// difference between "look at my chart" and "send that file somewhere". What is
/// still refused outright, with no prompt available, is the runtime's own internal
/// directories: those hold no user content and nothing there is worth uploading.
/// </summary>
public static class WorkspaceImageResolver
{
    /// <summary>Ceiling on what we will read off disk. Well above any plot or
    /// extracted figure; a match for the intake limit on user uploads.</summary>
    public const long MaxImageBytes = 32L * 1024 * 1024;

    /// <summary>Header bytes read per candidate when listing. Enough for every
    /// signature <see cref="AttachmentMime.SniffImageMime"/> looks at.</summary>
    private const int HeaderProbeBytes = 64;

    /// <summary>Names offered back when a lookup misses. Long enough to be a real
    /// answer, short enough not to turn an error into a directory dump.</summary>
    private const int ListingLimit = 12;

    /// <summary>Bound on the directory walk behind a listing, so a workspace that
    /// picked up a huge tree cannot stall a tool call.</summary>
    private const int ScanLimit = 400;

    public sealed record Resolution(
        string? RelativePath,
        byte[]? Bytes,
        string? MimeType,
        string? Error)
    {
        public bool Success => Bytes is not null;
        public static Resolution Failed(string error) => new(null, null, null, error);
    }

    /// <summary>
    /// The absolute file an <c>analyze_image</c> call will read, for the permission
    /// layer to judge before anything is uploaded. Null when the call names nothing
    /// that exists — the tool itself will say so a moment later.
    ///
    /// Shares <see cref="Locate"/> with <see cref="Resolve"/> on purpose: if the
    /// two resolved separately they could disagree, and the approval dialog would
    /// then be showing the user a different file from the one being sent.
    /// </summary>
    public static string? ResolveApprovalTarget(string? workspaceRoot, string? requestedPath) =>
        Locate(workspaceRoot, requestedPath).Path;

    /// <summary>Finds the file without reading it. Returns exactly one of a path
    /// or a model-facing error.</summary>
    private static (string? Path, string? Error) Locate(string? workspaceRoot, string? requestedPath)
    {
        var hasWorkspace = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot);
        var root = hasWorkspace ? System.IO.Path.GetFullPath(workspaceRoot!) : null;

        var raw = requestedPath?.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, root is null
                ? "analyze_image 需要 path 参数：图片文件名。"
                : WithListing("analyze_image 需要 path 参数：工作目录下的图片文件名。", root));
        }

        // An absolute path is allowed to name anything on disk; whether it is
        // actually read is the user's call, taken at the approval dialog.
        if (System.IO.Path.IsPathRooted(raw))
        {
            string absolute;
            try { absolute = System.IO.Path.GetFullPath(raw!); }
            catch (Exception ex) { return (null, $"无效路径：{ex.Message}"); }

            return File.Exists(absolute)
                ? (absolute, null)
                : (null, $"没有找到 {absolute}。");
        }

        if (root is null)
            return (null, "当前对话还没有工作目录；相对路径无从解析，请给出完整路径。");

        string full;
        try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, raw!)); }
        catch (Exception ex) { return (null, $"无效路径：{ex.Message}"); }

        var relative = SafeRelativePath(root, full);
        if (relative is not null && PythonWorkspaceInternals.IsInternalPath(relative))
            return (null, $"{relative} 属于运行时内部目录，不是可分析的内容。");

        if (File.Exists(full)) return (full, null);

        // Models routinely pass a bare file name for something nested, or get
        // the case wrong on a path they only ever saw in prose. Recovering
        // from that is worth more than being pedantic about it.
        var recovered = RecoverByName(root, raw!);
        return recovered is not null
            ? (recovered, null)
            : (null, WithListing($"工作目录下没有找到 {raw}。", root));
    }

    public static Resolution Resolve(string? workspaceRoot, string? requestedPath)
    {
        var (full, error) = Locate(workspaceRoot, requestedPath);
        if (full is null)
            return Resolution.Failed(error!);

        // Named by workspace-relative path when it is in the workspace, by full
        // path when it is not — in both cases the name the user was shown.
        var root = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? Path.GetFullPath(workspaceRoot!)
            : null;
        var relative = (root is not null ? SafeRelativePath(root, full) : null) ?? full;

        try
        {
            var info = new FileInfo(full);
            if (info.Length == 0)
                return Resolution.Failed($"{relative} 是空文件。");
            if (info.Length > MaxImageBytes)
                return Resolution.Failed($"{relative} 有 {FormatSize(info.Length)}，超过 {FormatSize(MaxImageBytes)} 的上限。");

            var bytes = File.ReadAllBytes(full);
            var mime = AttachmentMime.SniffImageMime(bytes);
            if (mime is null)
            {
                return Resolution.Failed(WithListing(
                    $"{relative} 不是可识别的图片格式（按文件内容判断，不看扩展名）。", root));
            }

            return new Resolution(relative, bytes, mime, null);
        }
        catch (Exception ex)
        {
            return Resolution.Failed($"读取 {relative} 失败：{ex.Message}");
        }
    }

    /// <summary>Workspace-relative names of the images actually present, decided
    /// by content rather than extension so a mislabelled <c>.png</c> is not
    /// advertised as something this tool can read.</summary>
    public static IReadOnlyList<string> ListImages(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return Array.Empty<string>();

        var root = Path.GetFullPath(workspaceRoot!);
        var found = new List<string>();
        var scanned = 0;

        foreach (var file in PythonWorkspaceInternals.EnumerateUserFiles(root))
        {
            if (++scanned > ScanLimit || found.Count >= ListingLimit) break;
            if (!PythonWorkspaceInternals.IsReportableUserFile(root, file, PythonExecutionTool.RuntimeScriptFileNames))
                continue;
            if (!HasImageHeader(file)) continue;

            var relative = SafeRelativePath(root, file);
            if (relative is not null) found.Add(relative);
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>Appends the available image names to a message, so a miss is a
    /// usable answer instead of a dead end the model has to guess its way out
    /// of. Silent when the workspace holds no images at all — offering an empty
    /// list would only invite another blind attempt.</summary>
    private static string WithListing(string message, string? root)
    {
        // Nothing to list, and no workspace to describe — a note about an empty
        // working directory would just be noise on an error about a file
        // somewhere else entirely.
        if (root is null) return message;

        var images = ListImages(root);
        if (images.Count == 0)
        {
            return string.IsNullOrEmpty(message)
                ? "工作目录里目前没有图片文件。"
                : message + " 工作目录里目前没有图片文件。";
        }

        var listed = $"工作目录下可分析的图片：{string.Join("、", images)}"
                     + (images.Count >= ListingLimit ? "（仅列出前若干个）" : string.Empty);
        return string.IsNullOrEmpty(message) ? listed : message + " " + listed;
    }

    /// <summary>Last-resort lookup by file name alone, case-insensitively. Only
    /// accepted when exactly one file matches — two candidates mean we would be
    /// guessing which picture the model meant, and analysing the wrong one is a
    /// worse outcome than saying so.</summary>
    private static string? RecoverByName(string root, string requested)
    {
        var wanted = Path.GetFileName(requested.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(wanted)) return null;

        string? match = null;
        var scanned = 0;
        foreach (var file in PythonWorkspaceInternals.EnumerateUserFiles(root))
        {
            if (++scanned > ScanLimit) break;
            if (!string.Equals(Path.GetFileName(file), wanted, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = SafeRelativePath(root, file);
            if (relative is null || PythonWorkspaceInternals.IsInternalPath(relative)) continue;

            if (match is not null) return null;
            match = file;
        }

        return match;
    }

    /// <summary>Relative path in forward-slash form, or null when
    /// <paramref name="full"/> escapes <paramref name="root"/>.</summary>
    private static string? SafeRelativePath(string root, string full)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, full);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }

    private static bool HasImageHeader(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var header = new byte[HeaderProbeBytes];
            var read = stream.ReadAtLeast(header, HeaderProbeBytes, throwOnEndOfStream: false);
            return read >= 12 && AttachmentMime.SniffImageMime(header[..read]) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MB"
        : $"{bytes / 1024d:0.#} KB";
}
