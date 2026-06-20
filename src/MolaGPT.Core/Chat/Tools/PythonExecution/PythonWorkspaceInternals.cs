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

    public static bool IsReportableUserFile(
        string root,
        string file,
        IReadOnlyCollection<string>? runtimeScriptFileNames = null)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(file))
            return false;

        string relative;
        try
        {
            relative = Path.GetRelativePath(root, file);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || IsInternalPath(relative))
        {
            return false;
        }

        var name = Path.GetFileName(file);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (runtimeScriptFileNames?.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) == true)
            return false;

        return !IsEphemeralWorkspaceFileName(name);
    }

    private static bool IsEphemeralWorkspaceFileName(string name)
    {
        if (name.StartsWith("~$", StringComparison.Ordinal))
            return true;

        return name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
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
