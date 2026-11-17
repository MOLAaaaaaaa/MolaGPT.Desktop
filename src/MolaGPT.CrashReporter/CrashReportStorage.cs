namespace MolaGPT.CrashReporter;

/// <summary>
/// What the crash reporter keeps on disk, for the app's storage page.
///
/// Only saved reports are listed. <c>pending\</c> is the live handshake between
/// the running app and its watcher — removing it would lose a crash that has not
/// been written yet — so it is never offered.
/// </summary>
public static class CrashReportStorage
{
    public static string RootDirectory => CrashReportStore.DefaultDirectory;

    public static IReadOnlyList<string> ListReportPaths()
    {
        var root = RootDirectory;
        if (!Directory.Exists(root)) return [];

        var paths = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                    paths.Add(directory);
            }

            // Reports from before the directory layout: <id>.zip beside <id>.json.
            foreach (var file in Directory.EnumerateFiles(root))
            {
                if (Path.GetExtension(file) is ".zip" or ".json"
                    && Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _))
                    paths.Add(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths;
    }
}
