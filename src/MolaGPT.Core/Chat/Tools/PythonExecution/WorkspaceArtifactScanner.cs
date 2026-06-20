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
/// Uses the shared workspace-file policy from <see cref="PythonWorkspaceInternals"/>
/// so the panel does not miss legitimate outputs just because their extension is
/// new to us. Runtime scaffolding, caches, and transient lock files stay hidden.
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
            files = PythonWorkspaceInternals.EnumerateUserFiles(sessionDir);
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

            if (!PythonWorkspaceInternals.IsReportableUserFile(
                    sessionDir,
                    file,
                    PythonExecutionTool.RuntimeScriptFileNames))
            {
                continue;
            }

            var name = Path.GetFileName(file);

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (IOException)
            {
                continue;
            }

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

    private static bool IsImageExtension(string extension) =>
        extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg";

}

/// <summary>A single artifact surfaced to the session-level artifact panel.</summary>
public sealed record WorkspaceArtifact(
    string Name,
    string RelativePath,
    string FullPath,
    long Bytes,
    DateTime LastWriteUtc,
    bool IsImage);
