using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MolaGPT.CrashReporter;
using MolaGPT.Desktop.Services;

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

    public static void Initialize()
    {
#if DEBUG
        if (Debugger.IsAttached && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOLAGPT_CRASH_TEST"))) return;
#else
        if (Debugger.IsAttached) return;
#endif
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        NativeCrashHooks.Initialize((pointers, threadId, code, address) =>
            WriteReport("Windows.UnhandledExceptionFilter", null, true, pointers, threadId, code, address));
        StartMonitor();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        DiagnosticLog.Write("unobserved-task", args.Exception.ToString());
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        WriteReport("AppDomain.UnhandledException", args.ExceptionObject as Exception, args.IsTerminating);
        if (args.IsTerminating && OperatingSystem.IsWindows())
            TerminateProcess(GetCurrentProcess(), unchecked((int)0xE0434352));
    }

    private static void WriteReport(string source, Exception? exception, bool terminating,
        IntPtr exceptionPointers = default, uint threadId = 0, uint? nativeCode = null, ulong? nativeAddress = null)
    {
        if (Interlocked.Exchange(ref _reportWritten, 1) != 0) return;

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
                LogDirectory = DiagnosticLog.SessionDirectory,
                FaultingThreadId = threadId == 0 && OperatingSystem.IsWindows() ? NativeCrashHooks.CurrentThreadId : threadId,
                NativeExceptionCode = nativeCode,
                NativeExceptionAddress = nativeAddress,
                Runtime = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString()
            };
            DiagnosticLog.Write("crash", $"{source}: {exception?.GetType().Name ?? "native exception"}");
            DiagnosticLog.Flush();
            var path = Path.Combine(ReportDirectory, "pending", SessionId + ".json");
            var temporaryPath = path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, report);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            RequestMinidump(new CrashCaptureRequest(report.FaultingThreadId ?? 0,
                unchecked((ulong)exceptionPointers.ToInt64())));
        }
        catch
        {
            // Crash reporting must not interfere with the runtime's exception shutdown path.
        }
    }

    private static void RequestMinidump(CrashCaptureRequest request)
    {
        if (!OperatingSystem.IsWindows() || !_monitorStarted) return;
        try
        {
            RequestMinidumpAsync(request).GetAwaiter().GetResult();
        }
        catch { }
    }

    private static async Task RequestMinidumpAsync(CrashCaptureRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
        await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint hProcess, int exitCode);

#if DEBUG
    public static void RunCrashTest()
    {
        var mode = Environment.GetEnvironmentVariable("MOLAGPT_CRASH_TEST");
        if (string.IsNullOrWhiteSpace(mode)) return;

        switch (mode.Trim().ToLowerInvariant())
        {
            case "ui":
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000).ConfigureAwait(false);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        throw new InvalidOperationException("Simulated unhandled UI exception (MOLAGPT_CRASH_TEST=ui)."));
                });
                break;

            case "thread":
                var thread = new Thread(() =>
                    throw new InvalidOperationException("Simulated unhandled background thread exception (MOLAGPT_CRASH_TEST=thread)."))
                {
                    IsBackground = true
                };
                thread.Start();
                Thread.Sleep(Timeout.Infinite);
                break;

            case "native":
                if (OperatingSystem.IsWindows())
                {
                    var ucrt = LoadLibrary("ucrtbase.dll");
                    var strlen = ucrt != 0 ? GetProcAddress(ucrt, "strlen") : 0;
                    if (strlen != 0)
                    {
                        CreateThread(0, 0, strlen, 0, 0, out _);
                        Thread.Sleep(Timeout.Infinite);
                    }
                }
                break;

            case "failfast":
                Thread.Sleep(1000);
                Environment.FailFast("Simulated Environment.FailFast crash (MOLAGPT_CRASH_TEST=failfast).");
                break;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibrary(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateThread(nint lpThreadAttributes, nuint dwStackSize, nint lpStartAddress,
        nint lpParameter, uint dwCreationFlags, out uint lpThreadId);
#endif

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
            processStart.ArgumentList.Add("--log-directory");
            processStart.ArgumentList.Add(DiagnosticLog.SessionDirectory);
            using var process = Process.Start(processStart);
            _monitorStarted = process is not null;
            if (!_monitorStarted) DiagnosticLog.Write("crash", "failed to start crash monitor");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("crash", $"failed to start crash monitor: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
