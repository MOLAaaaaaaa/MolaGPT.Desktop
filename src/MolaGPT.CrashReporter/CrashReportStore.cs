using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MolaGPT.CrashReporter;

internal sealed class CrashReportStore(string rootDirectory)
{
    private const int MaxLogBytes = 256 * 1024;
    private const int MaxRetainedReports = 10;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string PendingDirectory => Path.Combine(rootDirectory, "pending");
    public string GetPendingDirectory() => PendingDirectory;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT",
        "CrashReports");

    public string GetPendingPath(string sessionId) => Path.Combine(PendingDirectory, sessionId + ".json");

    public CrashReportRecord? ReadPending(string sessionId)
    {
        var path = GetPendingPath(sessionId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CrashReportRecord>(File.ReadAllBytes(path), JsonOptions); }
        catch { return null; }
    }

    public void RecoverPending(string activeSessionId)
    {
        Cleanup(30);
        if (!Directory.Exists(PendingDirectory)) return;

        foreach (var path in Directory.EnumerateFiles(PendingDirectory, "*.json"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(path), activeSessionId, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddSeconds(-30))
                    continue;

                var report = JsonSerializer.Deserialize<CrashReportRecord>(File.ReadAllBytes(path), JsonOptions);
                if (report is not null)
                {
                    var dumpPath = Path.Combine(PendingDirectory, report.SessionId + ".dmp");
                    Save(report, report.LogDirectory, File.Exists(dumpPath) ? dumpPath : null);
                }
                else
                {
                    TryDelete(path);
                }
            }
            catch
            {
                // A damaged pending file should not prevent the current app session from being monitored.
            }
        }
    }

    public CrashReportRecord Save(CrashReportRecord report, string? logPath, string? minidumpPath = null)
    {
        if (!Guid.TryParseExact(report.ReportId, "N", out _)
            || !Guid.TryParseExact(report.SessionId, "N", out _))
            throw new InvalidDataException("Crash report IDs must be GUIDs in N format.");

        report = report with
        {
            CaptureMinidump = minidumpPath is not null && File.Exists(minidumpPath),
            MinidumpFileName = minidumpPath is not null && File.Exists(minidumpPath) ? "crash.dmp" : null
        };
        Directory.CreateDirectory(rootDirectory);
        var reportJson = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        var reportDirectory = Path.Combine(rootDirectory, report.ReportId);
        var temporaryDirectory = reportDirectory + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var saved = false;
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            File.WriteAllBytes(Path.Combine(temporaryDirectory, "report.json"), reportJson);
            if (report.IncludeApplicationLog && logPath is not null && Directory.Exists(logPath))
            {
                var destination = Path.Combine(temporaryDirectory, "logs");
                foreach (var path in Directory.EnumerateFiles(logPath, "*.log"))
                {
                    var logBytes = ReadLogTail(path);
                    if (logBytes is null) continue;
                    Directory.CreateDirectory(destination);
                    File.WriteAllBytes(Path.Combine(destination, Path.GetFileName(path)), logBytes);
                }
            }
            if (report.MinidumpFileName is not null && minidumpPath is not null)
                File.Copy(minidumpPath, Path.Combine(temporaryDirectory, report.MinidumpFileName), true);
            if (!Directory.Exists(reportDirectory)) Directory.Move(temporaryDirectory, reportDirectory);
            saved = true;
            return report;
        }
        finally
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (saved)
            {
                TryDelete(GetPendingPath(report.SessionId));
                if (minidumpPath is not null) TryDelete(minidumpPath);
            }
        }
    }

    public IReadOnlyList<CrashReportRecord> List()
    {
        if (!Directory.Exists(rootDirectory)) return [];
        var reports = new List<CrashReportRecord>();
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var report = JsonSerializer.Deserialize<CrashReportRecord>(File.ReadAllBytes(path), JsonOptions);
                if (report is not null && Guid.TryParseExact(report.ReportId, "N", out _)) reports.Add(report);
            }
            catch { }
        }
        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                var report = JsonSerializer.Deserialize<CrashReportRecord>(File.ReadAllBytes(Path.Combine(directory, "report.json")), JsonOptions);
                if (report is not null) reports.Add(report);
            }
            catch { }
        }

        return reports.OrderByDescending(static report => report.CapturedAtUtc).ToArray();
    }

    public string? ResolveReportId(string? requestedId)
    {
        if (string.IsNullOrWhiteSpace(requestedId) || requestedId.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return List().FirstOrDefault()?.ReportId;

        return Guid.TryParseExact(requestedId, "N", out var id) ? id.ToString("N") : null;
    }

    public bool Export(string reportId, string outputPath)
    {
        var source = Path.Combine(rootDirectory, reportId + ".zip");
        var directory = Path.Combine(rootDirectory, reportId);
        if (!File.Exists(source) && !Directory.Exists(directory)) return false;
        var destination = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (Directory.Exists(directory))
        {
            using var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(path, Path.GetRelativePath(directory, path).Replace('\\', '/'), CompressionLevel.Fastest);
        }
        else File.Copy(source, destination, overwrite: true);
        return true;
    }

    public bool Delete(string reportId)
    {
        var reportPath = Path.Combine(rootDirectory, reportId + ".json");
        var archivePath = Path.Combine(rootDirectory, reportId + ".zip");
        var directory = Path.Combine(rootDirectory, reportId);
        if (!File.Exists(reportPath) && !File.Exists(archivePath) && !Directory.Exists(directory)) return false;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        TryDelete(reportPath);
        TryDelete(archivePath);
        return !File.Exists(reportPath) && !File.Exists(archivePath);
    }

    public int Cleanup(int keepDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-keepDays);
        var removed = 0;
        var reports = List();
        for (var i = 0; i < reports.Count; i++)
        {
            var report = reports[i];
            if (i < MaxRetainedReports && report.CapturedAtUtc >= cutoff) continue;
            Delete(report.ReportId);
            removed++;
        }

        if (Directory.Exists(rootDirectory))
        {
            foreach (var path in Directory.EnumerateDirectories(rootDirectory, "*.tmp"))
            {
                try { if (Directory.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime) Directory.Delete(path, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                        TryDelete(path);
                }
                catch { }
            }
        }

        if (Directory.Exists(PendingDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(PendingDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime
                        && Path.GetExtension(path) is ".json" or ".dmp" or ".tmp")
                        TryDelete(path);
                }
                catch { }
            }
        }

        return removed;
    }

    private static byte[]? ReadLogTail(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return null;
        try
        {
            var current = ReadFileTail(logPath, MaxLogBytes);
            var archivePath = logPath + ".1";
            var currentLength = current?.Length ?? 0;
            if (currentLength >= MaxLogBytes || !File.Exists(archivePath))
                return current;

            var previous = ReadFileTail(archivePath, MaxLogBytes - currentLength);
            if (previous is null or { Length: 0 }) return current;
            if (current is null or { Length: 0 }) return previous;

            var combined = new byte[previous.Length + current.Length];
            Buffer.BlockCopy(previous, 0, combined, 0, previous.Length);
            Buffer.BlockCopy(current, 0, combined, previous.Length, current.Length);
            return combined;
        }
        catch { return null; }
    }

    private static byte[]? ReadFileTail(string path, int maxBytes)
    {
        if (maxBytes <= 0 || !File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return [];
        var truncated = stream.Length > maxBytes;
        var length = (int)Math.Min(stream.Length, maxBytes);
        stream.Seek(-length, SeekOrigin.End);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        if (!truncated) return bytes;

        var newline = Array.IndexOf(bytes, (byte)'\n');
        return newline >= 0 && newline + 1 < bytes.Length ? bytes[(newline + 1)..] : bytes;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
