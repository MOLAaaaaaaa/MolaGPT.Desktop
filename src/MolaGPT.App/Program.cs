using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;
using MolaGPT.App.Infrastructure;
using MolaGPT.CrashReporter;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Second process of this executable acting as the crash watcher/UI/CLI.
        // Must come before the single-instance mutex and before any app setup.
        if (args.Length > 0 && args[0] == CrashReporterApplication.HostArgument)
            return CrashReporterApplication.RunHosted(args[1..], new CrashReportPresenter());

        if (!SingleInstanceHost.TryAcquire(args))
        {
            DiagnosticLog.Flush();
            return 0;
        }
        try
        {
            Logger.Sink = new AvaloniaErrorLogSink();
            DiagnosticLog.Write("app", $"starting version={typeof(Program).Assembly.GetName().Version}");
            CrashReportBootstrap.Initialize();
#if DEBUG
            CrashReportBootstrap.RunCrashTest();
#endif
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            DiagnosticLog.Write("app", "shutting down");
            DiagnosticLog.Flush();
            SingleInstanceHost.Release();
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => ConfigurePlatform(AppBuilder.Configure<App>());

    /// <summary>Platform options shared by the app and the crash-reporter UI process.</summary>
    internal static AppBuilder ConfigurePlatform(AppBuilder builder) =>
        builder
            .UsePlatformDetect()
            // The DXGI path follows the display refresh rate and keeps the DWM
            // redirection bitmap used by native window transitions. Popups stay
            // in the parent overlay because this swap chain has no popup alpha.
            .With(new Win32PlatformOptions
            {
                OverlayPopups = true,
                CompositionMode =
                [
                    Win32CompositionMode.LowLatencyDxgiSwapChain,
                    Win32CompositionMode.RedirectionSurface
                ]
            });

    private sealed class AvaloniaErrorLogSink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) =>
#if DEBUG
            level >= LogEventLevel.Warning;
#else
            level >= LogEventLevel.Error;
#endif

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Emit(level, area, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Emit(level, area, messageTemplate, propertyValues);

        private static void Emit(LogEventLevel level, string area, string template, object?[] values)
        {
            var detail = values.Length == 0 ? template : $"{template} | {string.Join(", ", values)}";
            var line = $"[{level}/{area}] {detail}";
#if DEBUG
            Trace.WriteLine(line);
#endif
            if (level >= LogEventLevel.Error)
                DiagnosticLog.Write("avalonia", line);
        }
    }
}
