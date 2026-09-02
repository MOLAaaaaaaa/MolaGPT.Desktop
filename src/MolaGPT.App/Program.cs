using Avalonia;
using MolaGPT.App.Infrastructure;

namespace MolaGPT.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!SingleInstanceHost.TryAcquire(args)) return;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstanceHost.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
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
