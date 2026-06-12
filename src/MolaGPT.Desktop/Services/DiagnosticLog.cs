using System.IO;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Append-only diagnostic log at %LocalAppData%\MolaGPT\app.log. Used
/// to trace cross-process flows (URL scheme launch, single-instance
/// hand-off, OAuth deep link processing) where a debugger isn't
/// practical because two processes are involved and one of them dies
/// in milliseconds.
///
/// Best-effort by design: every failure path swallows so logging
/// never crashes the app. The file gets truncated when it grows past
/// ~256 KB so it doesn't accumulate forever.
/// </summary>
public static class DiagnosticLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly System.Threading.Lock Gate = new();
    private static string? _path;

    private static string Path
    {
        get
        {
            if (_path is not null) return _path;
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MolaGPT");
            try { Directory.CreateDirectory(dir); }
            catch { }
            _path = System.IO.Path.Combine(dir, "app.log");
            return _path;
        }
    }

    public static void Write(string tag, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    try { File.Delete(path); } catch { }
                }
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId,5}] {tag,-20} {message}\n";
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
