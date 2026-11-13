using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;

namespace MolaGPT.CrashReporter;

public static class CrashReporterApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Command-line host argument that makes MolaGPT.Desktop.exe act as the crash reporter.</summary>
    public const string HostArgument = "--crash-reporter";

    /// <summary>Runs a reporter command (the arguments after <see cref="HostArgument"/>) inside the app executable.</summary>
    public static int RunHosted(string[] args, ICrashReportPresenter presenter)
    {
        ConsoleAttachment.TryAttachParent();
        return Run(args, presenter);
    }

    public static int Run(string[] args, ICrashReportPresenter? presenter = null)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            var options = ParseOptions(args);
            var store = new CrashReportStore(options.GetValueOrDefault("--data-dir") ?? CrashReportStore.DefaultDirectory);
            return args[0] switch
            {
                "--watch" => Watch(store, options, presenter),
                "--show" => Show(store, options, presenter),
                "--restart" => Restart(options),
                "--list" => List(store),
                "--export" => Export(store, options),
                "--delete" => Delete(store, options),
                "--symbolicate" => Symbolicate(store, options),
                "--cleanup" => Cleanup(store, options),
                _ => UsageError($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Crash reporter failed: {ex.Message}");
            return 2;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected a value after {args[i]}.");
            values[args[i]] = args[++i];
        }
        return values;
    }

    private static int Watch(CrashReportStore store, IReadOnlyDictionary<string, string> options, ICrashReportPresenter? presenter)
    {
        var sessionId = Require(options, "--session");
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("--session must be a GUID in N format.");
        var processId = int.Parse(Require(options, "--pid"), CultureInfo.InvariantCulture);
        var version = options.GetValueOrDefault("--app-version") ?? "unknown";
        var logPath = options.GetValueOrDefault("--log-file");
        store.RecoverPending(sessionId, logPath);

        var pipeName = options.GetValueOrDefault("--pipe");
        using var dumpPipe = string.IsNullOrWhiteSpace(pipeName)
            ? null
            : new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var dumpPath = Path.Combine(store.GetPendingDirectory(), sessionId + ".dmp");
        var dumpTask = dumpPipe is null
            ? Task.FromResult(false)
            : ServeDumpRequestAsync(dumpPipe, processId, sessionId, store, logPath, dumpPath);

        int? exitCode = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit();
            try { exitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
        }
        catch (ArgumentException)
        {
            // The app may have exited before this monitor was scheduled.
        }

        dumpPipe?.Dispose();
        bool captured;
        try { captured = dumpTask.GetAwaiter().GetResult(); }
        catch { captured = false; }

        if (captured)
        {
            // A managed UI may already have handled/deleted the report. Native faults
            // have no reliable in-process dispatcher: use the independent presenter.
            var saved = store.List().FirstOrDefault(item => item.ReportId == sessionId);
            if (exitCode is not null && saved is not null)
            {
                store.UpdateExitCode(sessionId, exitCode.Value);
                saved = saved with { ExitCode = exitCode.Value };
            }
            if (saved is not null && !saved.UseInProcessPresentation)
                Present(store, saved, options, presenter);
            return 0;
        }

        CrashReportRecord? report = null;
        for (var attempt = 0; attempt < 20 && report is null; attempt++)
        {
            report = store.ReadPending(sessionId);
            if (report is null) Thread.Sleep(100);
        }

        var existing = store.List().FirstOrDefault(item => item.ReportId == sessionId);
        if (existing is not null)
        {
            if (exitCode is not null) store.UpdateExitCode(sessionId, exitCode.Value);
            if (!existing.UseInProcessPresentation) Present(store, existing, options, presenter);
            return 0;
        }
        if (report is null && exitCode is not null and not 0)
        {
            report = CreateExitReport(sessionId, processId, version, exitCode.Value);
        }

        if (report is not null)
        {
            store.Save(report with { ExitCode = exitCode }, logPath,
                File.Exists(dumpPath) ? dumpPath : null);
            Console.WriteLine($"Crash report {report.ReportId} saved.");
            Present(store, store.List().First(item => item.ReportId == report.ReportId), options, presenter);
        }
        else if (exitCode is not null)
        {
            store.UpdateExitCode(sessionId, exitCode.Value);
        }

        return 0;
    }

    private static CrashReportRecord CreateExitReport(string sessionId, int processId, string version, int exitCode) => new()
    {
        ReportId = sessionId,
        SessionId = sessionId,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        AppName = "MolaGPT Desktop",
        AppVersion = version,
        ProcessId = processId,
        Source = "ProcessExit",
        Message = $"The application exited unexpectedly with code 0x{unchecked((uint)exitCode):X8} ({exitCode}).",
        ExitCode = exitCode,
        IncludeApplicationLog = true,
        Runtime = RuntimeInformation.FrameworkDescription,
        OperatingSystem = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString()
    };

    private static int List(CrashReportStore store)
    {
        Console.WriteLine(JsonSerializer.Serialize(store.List(), JsonOptions));
        return 0;
    }

    private static int Show(CrashReportStore store, IReadOnlyDictionary<string, string> options, ICrashReportPresenter? presenter)
    {
        var reportId = store.ResolveReportId(options.GetValueOrDefault("--id"));
        var report = store.List().FirstOrDefault(item => item.ReportId == reportId);
        if (report is null) return UsageError("Crash report was not found.");
        if (presenter is null) return UsageError("No crash UI presenter has been registered.");
        Present(store, report, options, presenter);
        return 0;
    }

    private static void Present(CrashReportStore store, CrashReportRecord report,
        IReadOnlyDictionary<string, string> options, ICrashReportPresenter? presenter)
    {
        if (presenter is null) return;
        var context = new CrashReportContext(store, report, options.GetValueOrDefault("--executable"));
        context.Apply(presenter.Show(context));
    }

    private static int Restart(IReadOnlyDictionary<string, string> options)
    {
        var processId = int.Parse(Require(options, "--pid"), CultureInfo.InvariantCulture);
        var executable = Path.GetFullPath(Require(options, "--executable"));
        if (!File.Exists(executable)) return UsageError("Restart executable was not found.");
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(30_000)) return UsageError("Application did not exit before the restart deadline.");
        }
        catch (ArgumentException) { }
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })?.Dispose();
        return 0;
    }

    private static int Export(CrashReportStore store, IReadOnlyDictionary<string, string> options)
    {
        var reportId = store.ResolveReportId(options.GetValueOrDefault("--id"));
        if (reportId is null) return UsageError("No crash reports are available to export.");
        var output = Require(options, "--output");
        if (!store.Export(reportId, output)) return UsageError($"Report archive not found: {reportId}");
        Console.WriteLine(Path.GetFullPath(output));
        return 0;
    }

    private static int Delete(CrashReportStore store, IReadOnlyDictionary<string, string> options)
    {
        var reportId = store.ResolveReportId(Require(options, "--id"));
        if (reportId is null || !store.Delete(reportId)) return UsageError("Crash report was not found.");
        Console.WriteLine(reportId);
        return 0;
    }

    private static int Symbolicate(CrashReportStore store, IReadOnlyDictionary<string, string> options)
    {
        var reportId = store.ResolveReportId(options.GetValueOrDefault("--id"));
        if (reportId is null) return UsageError("No crash reports are available to analyze.");
        var dumpPath = store.ExtractMinidump(reportId);
        if (dumpPath is null) return UsageError($"Report has no minidump: {reportId}");

        try
        {
            var result = CrashReportSymbolicator.Analyze(dumpPath, options.GetValueOrDefault("--symbol-path"),
                options.GetValueOrDefault("--dac"));
            var json = JsonSerializer.Serialize(result, JsonOptions);
            var output = options.GetValueOrDefault("--output");
            if (string.IsNullOrWhiteSpace(output))
            {
                Console.WriteLine(json);
            }
            else
            {
                var fullPath = Path.GetFullPath(output);
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(fullPath, json);
                Console.WriteLine(fullPath);
            }

            return result.HasStackFrames ? 0 : 3;
        }
        finally
        {
            try { File.Delete(dumpPath); }
            catch { }
        }
    }

    private static async Task<bool> ServeDumpRequestAsync(
        NamedPipeServerStream pipe,
        int processId,
        string sessionId,
        CrashReportStore store,
        string? logPath,
        string dumpPath)
    {
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var line = await ReadBoundedLineAsync(pipe, 8192, deadline.Token).ConfigureAwait(false);
            if (line is null) return false;
            var request = JsonSerializer.Deserialize<CrashCaptureRequest>(line);
            if (request is null) return false;
            var dump = NativeMiniDumpWriter.TryWrite(processId, dumpPath, request);
            var report = store.ReadPending(sessionId);
            if (report is null) return false;
            store.Save(report with { MinidumpError = dump.Error }, logPath, dump.Success ? dumpPath : null);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CrashCaptureResult(true, dump.Success, dump.Error))
                    .AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            catch { } // Even if the app timed out, keep the durable archive.
            return true;
        }
        catch (IOException)
        {
            // The app may have exited or abandoned the snapshot request.
        }
        catch (ObjectDisposedException)
        {
            // The watched process exited before a dump was requested.
        }
        catch (Exception) { }
        return false;
    }

    // StreamReader.ReadLineAsync buffers an unbounded line before any length check.
    private static async Task<string?> ReadBoundedLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes];
        var count = 0;
        while (count < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            if (buffer[count] == (byte)'\n') return Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\r');
            count++;
        }
        return null;
    }

    private static int Cleanup(CrashReportStore store, IReadOnlyDictionary<string, string> options)
    {
        var keepDays = int.Parse(options.GetValueOrDefault("--keep-days") ?? "30", CultureInfo.InvariantCulture);
        if (keepDays < 1) return UsageError("--keep-days must be at least 1.");
        Console.WriteLine(store.Cleanup(keepDays).ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option: {key}");

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("MolaGPT Crash Reporter (MolaGPT.Desktop.exe --crash-reporter <command>)");
        Console.WriteLine("  --list [--data-dir <path>]");
        Console.WriteLine("  --show [--id <report-id|latest>] [--executable <path>] [--data-dir <path>]");
        Console.WriteLine("  --restart --pid <pid> --executable <path>");
        Console.WriteLine("  --export [--id <report-id|latest>] --output <path> [--data-dir <path>]");
        Console.WriteLine("  --delete --id <report-id> [--data-dir <path>]");
        Console.WriteLine("  --symbolicate [--id <report-id|latest>] [--symbol-path <path>] [--dac <path>] [--output <path>] [--data-dir <path>]");
        Console.WriteLine("  --cleanup [--keep-days <days>] [--data-dir <path>]");
        Console.WriteLine("  --watch --pid <pid> --session <guid-n> --app-version <version> --pipe <name> [--log-file <path>] [--data-dir <path>]");
    }
}
