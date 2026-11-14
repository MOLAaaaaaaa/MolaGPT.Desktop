using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MolaGPT.App.Views;
using MolaGPT.CrashReporter;

namespace MolaGPT.App.Infrastructure;

public sealed class CrashReporterUiApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// Shows <see cref="CrashReportWindow"/> in the crash-reporter process, after the
/// app has exited. Avalonia can be set up once per process; a reporter process
/// presents at most one report.
/// </summary>
internal sealed class CrashReportPresenter : ICrashReportPresenter
{
    private static bool _initialized;

    public CrashReportAction Show(CrashReportContext context)
    {
        try
        {
            if (!_initialized)
            {
                // Same platform options as the app, so MolaWindow's chrome behaves identically.
                Program.ConfigurePlatform(AppBuilder.Configure<CrashReporterUiApp>()).SetupWithoutStarting();
                _initialized = true;
            }

            var window = new CrashReportWindow(context);
            using var closed = new CancellationTokenSource();
            window.Closed += (_, _) => closed.Cancel();
            window.Show();
            window.Activate();
            Dispatcher.UIThread.MainLoop(closed.Token);
            return window.Result;
        }
        catch (Exception ex)
        {
            // No usable UI (no desktop session, GPU/driver failure…). The report is
            // already on disk and remains available through --show / --export.
            Console.Error.WriteLine($"Crash UI could not be shown: {ex.Message}");
            return CrashReportAction.Exit;
        }
    }
}
