using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MolaGPT.CrashReporter;

internal sealed class CrashReportStore(string rootDirectory)
{
    private const int MaxLogBytes = 256 * 1024;
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

    public void RecoverPending(string activeSessionId, string? logPath = null)
    {
        Cleanup(30);
        if (!Directory.Exists(PendingDirectory)) return;

        foreach (var path in Directory.EnumerateFiles(PendingDirectory, "*.json"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(path), activeSessionId, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var report = JsonSerializer.Deserialize<CrashReportRecord>(File.ReadAllBytes(path), JsonOptions);
                if (report is not null)
                {
                    var dumpPath = Path.Combine(PendingDirectory, report.SessionId + ".dmp");
                    Save(report, logPath, File.Exists(dumpPath) ? dumpPath : null);
                }
                File.Delete(path);
            }
            catch
            {
                // A damaged pending file should not prevent the current app session from being monitored.
            }
        }
    }

    public void Save(CrashReportRecord report, string? logPath, string? minidumpPath = null)
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
        var reportPath = Path.Combine(rootDirectory, report.ReportId + ".json");
        var archivePath = Path.Combine(rootDirectory, report.ReportId + ".zip");
        var temporaryJson = reportPath + ".tmp";
        var temporaryArchive = archivePath + ".tmp";

        var saved = false;
        try
        {
            File.WriteAllBytes(temporaryJson, reportJson);
            using (var file = new FileStream(temporaryArchive, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(archive, "report.json", reportJson);
                var logBytes = report.IncludeApplicationLog ? ReadLogTail(logPath) : null;
                if (logBytes is not null) WriteEntry(archive, "app.log", logBytes);
                if (report.MinidumpFileName is not null && minidumpPath is not null)
                {
                    var dump = archive.CreateEntry(report.MinidumpFileName, CompressionLevel.NoCompression);
                    using var destination = dump.Open();
                    using var source = new FileStream(minidumpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    source.CopyTo(destination);
                }
            }

            File.Move(temporaryJson, reportPath, overwrite: true);
            File.Move(temporaryArchive, archivePath, overwrite: true);
            saved = true;
        }
        finally
        {
            TryDelete(temporaryJson);
            TryDelete(temporaryArchive);
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
        if (!File.Exists(source)) return false;
        var destination = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.Copy(source, destination, overwrite: true);
        return true;
    }

    public bool Delete(string reportId)
    {
        var reportPath = Path.Combine(rootDirectory, reportId + ".json");
        var archivePath = Path.Combine(rootDirectory, reportId + ".zip");
        if (!File.Exists(reportPath) && !File.Exists(archivePath)) return false;
        TryDelete(reportPath);
        TryDelete(archivePath);
        return !File.Exists(reportPath) && !File.Exists(archivePath);
    }

    public void UpdateExitCode(string reportId, int exitCode)
    {
        var reportPath = Path.Combine(rootDirectory, reportId + ".json");
        if (!File.Exists(reportPath)) return;
        try
        {
            var report = JsonSerializer.Deserialize<CrashReportRecord>(File.ReadAllBytes(reportPath), JsonOptions);
            if (report is null) return;
            var updated = JsonSerializer.SerializeToUtf8Bytes(report with { ExitCode = exitCode }, JsonOptions);
            var temporaryPath = reportPath + ".tmp";
            File.WriteAllBytes(temporaryPath, updated);
            File.Move(temporaryPath, reportPath, overwrite: true);

            var archivePath = Path.Combine(rootDirectory, reportId + ".zip");
            if (!File.Exists(archivePath)) return;
            // ZipArchiveMode.Update buffers every entry (including a large dump) in
            // memory and rewrites the archive in place; an interruption would corrupt
            // the only copy of the dump. Stream into a new archive and swap atomically.
            var temporaryArchive = archivePath + ".tmp";
            try
            {
                using (var source = ZipFile.OpenRead(archivePath))
                using (var file = new FileStream(temporaryArchive, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (var destination = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
                {
                    WriteEntry(destination, "report.json", updated);
                    foreach (var entry in source.Entries)
                    {
                        if (entry.FullName == "report.json") continue;
                        var copy = destination.CreateEntry(entry.FullName,
                            entry.FullName == "crash.dmp" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                        using var from = entry.Open();
                        using var to = copy.Open();
                        from.CopyTo(to);
                    }
                }
                File.Move(temporaryArchive, archivePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryArchive);
            }
        }
        catch
        {
            // Exit-code enrichment is best effort and must not lose the captured dump.
        }
    }

    public string? ExtractMinidump(string reportId)
    {
        var archivePath = Path.Combine(rootDirectory, reportId + ".zip");
        if (!File.Exists(archivePath)) return null;

        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("crash.dmp");
        if (entry is null || entry.Length == 0) return null;
        Directory.CreateDirectory(PendingDirectory);
        var tempPath = Path.Combine(PendingDirectory, $"analysis-{reportId}-{Guid.NewGuid():N}.dmp");
        try
        {
            using (var source = entry.Open())
            using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(destination);
            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public int Cleanup(int keepDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-keepDays);
        var removed = 0;
        foreach (var report in List())
        {
            if (report.CapturedAtUtc >= cutoff) continue;
            TryDelete(Path.Combine(rootDirectory, report.ReportId + ".json"));
            TryDelete(Path.Combine(rootDirectory, report.ReportId + ".zip"));
            removed++;
        }

        if (Directory.Exists(PendingDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(PendingDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime
                        && Path.GetExtension(path) is ".json" or ".dmp")
                        TryDelete(path);
                }
                catch { }
            }
        }

        return removed;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[]? ReadLogTail(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return null;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, MaxLogBytes);
            stream.Seek(-length, SeekOrigin.End);
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes));
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
