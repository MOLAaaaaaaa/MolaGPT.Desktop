using Avalonia;
using MolaGPT.App.Infrastructure;
using MolaGPT.CrashReporter;

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

        if (!SingleInstanceHost.TryAcquire(args)) return 0;
        try
        {
            CrashReportBootstrap.Initialize();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
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
            })
            .LogToTrace();
}
