using System.IO;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

/// <summary>
/// Enumerates the artifacts currently sitting in a conversation's Python working
/// directory (the folder <see cref="PythonExecutionTool"/> runs in). Unlike the
/// tool's per-run <c>ScanArtifacts</c> — which filters by run-start time so each
/// call only reports what it just produced — this scanner reports the whole
/// conversation's accumulated artifacts so a session-level UI panel can list
/// everything generated (or uploaded) across the conversation.
///
/// Reuses the tool's artifact extension allow-list and scaffolding exclusions so
/// the panel shows the same kinds of files the model is told about.
/// </summary>
public static class WorkspaceArtifactScanner
{
    private const long MaxReportableBytes = 200L * 1024L * 1024L;

    /// <summary>Scans the working directory for the given conversation. Returns an
    /// empty list when the directory does not exist yet (no python run / upload
    /// has happened). Never throws on a single unreadable file — it is skipped.</summary>
    public static IReadOnlyList<WorkspaceArtifact> Scan(string? conversationId)
    {
        var sessionDir = PythonExecutionTool.GetSessionDirectory(conversationId);
        return ScanDirectory(sessionDir);
    }

    /// <summary>Directory-level scan, separated so tests can target an arbitrary
    /// folder without depending on the LocalAppData session layout.</summary>
    public static IReadOnlyList<WorkspaceArtifact> ScanDirectory(string sessionDir)
    {
        if (string.IsNullOrWhiteSpace(sessionDir) || !Directory.Exists(sessionDir))
            return Array.Empty<WorkspaceArtifact>();

        var results = new List<WorkspaceArtifact>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sessionDir, "*", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return Array.Empty<WorkspaceArtifact>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<WorkspaceArtifact>();
        }

        foreach (var file in files)
        {
            string relative;
            try
            {
                relative = Path.GetRelativePath(sessionDir, file);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (IsInternalRuntimeArtifact(relative))
                continue;

            var name = Path.GetFileName(file);
            if (IsRuntimeScript(name))
                continue;

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (!IsArtifactExtension(info.Extension))
                continue;
            if (info.Length > MaxReportableBytes)
                continue;

            results.Add(new WorkspaceArtifact(
                name,
                relative.Replace('\\', '/'),
                file,
                info.Length,
                info.LastWriteTimeUtc,
                IsImageExtension(info.Extension)));
        }

        // Newest first so freshly generated artifacts surface at the top.
        return results
            .OrderByDescending(a => a.LastWriteUtc)
            .ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsRuntimeScript(string name) =>
        PythonExecutionTool.RuntimeScriptFileNames
            .Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsArtifactExtension(string extension) =>
        extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"
            or ".svg" or ".csv" or ".tsv" or ".txt" or ".json" or ".xlsx"
            or ".html" or ".htm" or ".pdf" or ".parquet"
            // Session-level panel also surfaces these common doc/data outputs and
            // user uploads that the per-run scanner intentionally ignores.
            or ".md" or ".docx" or ".xls" or ".xml" or ".yaml" or ".yml" or ".zip";

    private static bool IsImageExtension(string extension) =>
        extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg";

    private static bool IsInternalRuntimeArtifact(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith(".matplotlib/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("__pycache__/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A single artifact surfaced to the session-level artifact panel.</summary>
public sealed record WorkspaceArtifact(
    string Name,
    string RelativePath,
    string FullPath,
    long Bytes,
    DateTime LastWriteUtc,
    bool IsImage);
