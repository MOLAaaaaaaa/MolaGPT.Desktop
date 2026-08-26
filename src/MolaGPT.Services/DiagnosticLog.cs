using System.IO;
using System.Text.RegularExpressions;

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
    private static readonly Regex AuthorizationRegex = new(
        @"\b(Authorization\s*[:=]\s*)(?:Bearer\s+)?[A-Za-z0-9._~+/\-]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BearerRegex = new(
        @"\b(Bearer)\s+[A-Za-z0-9._~+/\-]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QuerySecretRegex = new(
        @"([?&](?:code|token|jwt|access_token|refresh_token|api[_-]?key|apikey|password|secret)=)([^&\s\]""')]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AssignmentSecretRegex = new(
        @"\b((?:api[_-]?key|apikey|token|jwt|password|secret)\s*[:=]\s*)([^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ApiKeyRegex = new(
        @"\b(?:sk|sess)-[A-Za-z0-9_-]{12,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
                var safeTag = OneLine(Redact(tag));
                var safeMessage = OneLine(Redact(message));
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId,5}] {safeTag,-20} {safeMessage}\n";
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    private static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var redacted = AuthorizationRegex.Replace(value, "$1[redacted]");
        redacted = BearerRegex.Replace(redacted, "$1 [redacted]");
        redacted = QuerySecretRegex.Replace(redacted, "$1[redacted]");
        redacted = AssignmentSecretRegex.Replace(redacted, "$1[redacted]");
        redacted = JwtRegex.Replace(redacted, "[jwt:redacted]");
        redacted = ApiKeyRegex.Replace(redacted, "[api-key:redacted]");
        return redacted;
    }

    private static string OneLine(string value) =>
        value.Replace("\r", "\\r").Replace("\n", "\\n");
}
