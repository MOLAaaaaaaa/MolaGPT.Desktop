using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using MolaGPT.CrashReporter;

namespace MolaGPT.App.Infrastructure;

internal static class CrashReportBootstrap
{
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static readonly string ReportDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT",
        "CrashReports");
    private static int _initialized;
    private static int _reportWritten;
    private static bool _monitorStarted;
    private static string PipeName => "MolaGPT.CrashReport." + SessionId;

    internal static void RequestRestart()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("App executable is not available.");
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { CrashReporterApplication.HostArgument, "--restart", "--pid", Environment.ProcessId.ToString(), "--executable", executable })
            startInfo.ArgumentList.Add(argument);
        Process.Start(startInfo)?.Dispose();
    }

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        NativeCrashHooks.Initialize((pointers, threadId, code, address) =>
            WriteReport("Windows.UnhandledExceptionFilter", null, true, pointers, threadId, code, address));
        StartMonitor();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var report = WriteReport("AppDomain.UnhandledException", args.ExceptionObject as Exception, args.IsTerminating);
        if (report is not null) CrashReportDialogHost.TryPresent(report, waitForDecision: true);
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs args)
    {
        var report = WriteReport("Avalonia.DispatcherUnhandledException", args.Exception, terminating: true);
        if (report is not null)
            args.Handled = CrashReportDialogHost.TryPresent(report, waitForDecision: false);
    }

    private static CrashReportRecord? WriteReport(string source, Exception? exception, bool terminating,
        IntPtr exceptionPointers = default, uint threadId = 0, uint? nativeCode = null, ulong? nativeAddress = null)
    {
        if (Interlocked.Exchange(ref _reportWritten, 1) != 0) return null;

        try
        {
            Directory.CreateDirectory(Path.Combine(ReportDirectory, "pending"));
            var report = new CrashReportRecord
            {
                ReportId = SessionId,
                SessionId = SessionId,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                AppName = "MolaGPT Desktop",
                AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                ProcessId = Environment.ProcessId,
                Source = terminating ? source : source + " (non-terminating)",
                ExceptionType = exception?.GetType().FullName,
                Message = exception?.Message ?? "No exception message was provided.",
                StackTrace = exception?.ToString(),
                IncludeApplicationLog = true,
                FaultingThreadId = threadId == 0 && OperatingSystem.IsWindows() ? NativeCrashHooks.CurrentThreadId : threadId,
                NativeExceptionCode = nativeCode,
                NativeExceptionAddress = nativeAddress,
                UseInProcessPresentation = CrashReportDialogHost.HasPresenter
                    && (source == "Avalonia.DispatcherUnhandledException"
                        || source == "AppDomain.UnhandledException" && !Dispatcher.UIThread.CheckAccess()),
                Runtime = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString()
            };
            var path = Path.Combine(ReportDirectory, "pending", SessionId + ".json");
            var temporaryPath = path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, report);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            var capture = RequestMinidump(new CrashCaptureRequest(report.FaultingThreadId ?? 0,
                unchecked((ulong)exceptionPointers.ToInt64())));
            return report with
            {
                CaptureMinidump = capture.DumpCaptured,
                MinidumpFileName = capture.DumpCaptured ? "crash.dmp" : null,
                MinidumpError = capture.DumpError
            };
        }
        catch
        {
            // Crash reporting must not interfere with the runtime's exception shutdown path.
            return null;
        }
    }

    private static CrashCaptureResult RequestMinidump(CrashCaptureRequest request)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, "Minidump capture requires Windows.");
        if (!_monitorStarted) return new(false, false, "Crash reporter executable is not available.");
        try
        {
            return RequestMinidumpAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }

    private static async Task<CrashCaptureResult> RequestMinidumpAsync(CrashCaptureRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
        var response = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        return response is null ? new(false, false, "Reporter disconnected before capture completed.")
            : JsonSerializer.Deserialize<CrashCaptureResult>(response) ?? new(false, false, "Invalid reporter response.");
    }

    private static void StartMonitor()
    {
        try
        {
            // The reporter is this same executable in a second process (see Program.Main).
            if (Environment.ProcessPath is not { } helperPath || !File.Exists(helperPath)) return;

            var processStart = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            processStart.ArgumentList.Add(CrashReporterApplication.HostArgument);
            processStart.ArgumentList.Add("--watch");
            processStart.ArgumentList.Add("--pid");
            processStart.ArgumentList.Add(Environment.ProcessId.ToString());
            processStart.ArgumentList.Add("--session");
            processStart.ArgumentList.Add(SessionId);
            processStart.ArgumentList.Add("--pipe");
            processStart.ArgumentList.Add(PipeName);
            processStart.ArgumentList.Add("--app-version");
            processStart.ArgumentList.Add(Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown");
            processStart.ArgumentList.Add("--data-dir");
            processStart.ArgumentList.Add(ReportDirectory);
            if (Environment.ProcessPath is { } executable)
            {
                processStart.ArgumentList.Add("--executable");
                processStart.ArgumentList.Add(executable);
            }
            processStart.ArgumentList.Add("--log-file");
            processStart.ArgumentList.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT", "app.log"));
            using var process = Process.Start(processStart);
            _monitorStarted = process is not null;
        }
        catch
        {
            // The durable pending record remains available for recovery on the next start.
        }
    }
}
