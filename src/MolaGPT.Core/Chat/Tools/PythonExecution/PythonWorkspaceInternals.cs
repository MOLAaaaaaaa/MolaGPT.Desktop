namespace MolaGPT.Core.Chat.Tools.PythonExecution;

internal static class PythonWorkspaceInternals
{
    private static readonly HashSet<string> InternalDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".matplotlib",
        ".packages",
        ".pip-cache",
        ".uv-cache",
        ".tmp",
        ".appdata",
        ".localappdata",
        "__pycache__"
    };

    public static bool IsInternalPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var firstSeparator = normalized.IndexOf('/');
        var firstSegment = firstSeparator < 0 ? normalized : normalized[..firstSeparator];
        return InternalDirectoryNames.Contains(firstSegment);
    }

    /// <summary>
    /// Enumerates user-visible workspace files without descending into package,
    /// cache, and isolated-profile directories that can contain tens of thousands
    /// of implementation files.
    /// </summary>
    public static IEnumerable<string> EnumerateUserFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            foreach (var child in directories)
            {
                var relative = Path.GetRelativePath(root, child);
                if (!IsInternalPath(relative))
                    pending.Push(child);
            }
        }
    }
}
