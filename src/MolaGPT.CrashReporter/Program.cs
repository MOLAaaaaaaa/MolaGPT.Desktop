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
                "--list" => List(store),
                "--export" => Export(store, options),
                "--delete" => Delete(store, options),
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
        var logPath = options.GetValueOrDefault("--log-directory");

        var pipeName = options.GetValueOrDefault("--pipe");
        using var dumpPipe = string.IsNullOrWhiteSpace(pipeName)
            ? null
            : new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var dumpPath = Path.Combine(store.GetPendingDirectory(), sessionId + ".dmp");
        var dumpTask = dumpPipe is null
            ? Task.FromResult<(bool Requested, string? DumpError)>((false, null))
            : ServeDumpRequestAsync(dumpPipe, processId, dumpPath);

        int? exitCode = null;
        Task? recoverTask = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            _ = process.SafeHandle; // Retain a query handle so ExitCode works after WaitForExit().
            // Defer orphan recovery/cleanup until after the pipe is listening and the process handle is held.
            recoverTask = Task.Run(() => store.RecoverPending(sessionId));
            process.WaitForExit();
            try { exitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
        }
        catch (ArgumentException)
        {
            // The app may have exited before this monitor was scheduled.
            recoverTask = Task.Run(() => store.RecoverPending(sessionId));
        }

        if (recoverTask is not null)
        {
            try { recoverTask.GetAwaiter().GetResult(); }
            catch { }
        }

        dumpPipe?.Dispose();
        (bool dumpRequested, string? dumpError) dumpOutcome;
        try { dumpOutcome = dumpTask.GetAwaiter().GetResult(); }
        catch { dumpOutcome = (false, null); }

        var report = store.ReadPending(sessionId);
        if (report is not null && dumpOutcome.dumpRequested)
            report = report with { MinidumpError = dumpOutcome.dumpError };

        if (report is null && exitCode is { } code && IsAbnormalExitCode(code))
            report = CreateExitReport(sessionId, processId, version, code) with { LogDirectory = logPath };

        if (report is not null)
        {
            var saved = store.Save(report with { ExitCode = exitCode }, report.LogDirectory,
                File.Exists(dumpPath) ? dumpPath : null);
            Console.WriteLine($"Crash report {saved.ReportId} saved.");
            Present(store, saved, options, presenter);
        }

        return 0;
    }

    private static bool IsAbnormalExitCode(int exitCode) => exitCode < -1 || exitCode == 3;

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

    private static async Task<(bool Requested, string? DumpError)> ServeDumpRequestAsync(
        NamedPipeServerStream pipe,
        int processId,
        string dumpPath)
    {
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var line = await ReadBoundedLineAsync(pipe, 8192, deadline.Token).ConfigureAwait(false);
            if (line is null) return (false, null);
            var request = JsonSerializer.Deserialize<CrashCaptureRequest>(line);
            if (request is null) return (false, null);
            var dump = NativeMiniDumpWriter.TryWrite(processId, dumpPath, request);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CrashCaptureResult(dump.Success, dump.Error))
                    .AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            catch { } // Even if the app timed out, keep the durable minidump for archiving on exit.
            return (true, dump.Error);
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
        return (false, null);
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
        Console.WriteLine("  --export [--id <report-id|latest>] --output <path> [--data-dir <path>]");
        Console.WriteLine("  --delete --id <report-id> [--data-dir <path>]");
        Console.WriteLine("  --cleanup [--keep-days <days>] [--data-dir <path>]");
        Console.WriteLine("  --watch --pid <pid> --session <guid-n> --app-version <version> --pipe <name> [--log-directory <path>] [--data-dir <path>]");
    }
}
