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
            // DWM needs a redirection surface for native window transitions.
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            })
            .LogToTrace();
}
