using System.Diagnostics;
using Avalonia.Threading;
using MolaGPT.CrashReporter;

namespace MolaGPT.App.Infrastructure;

public enum CrashReportDialogAction
{
    Exit,
    Restart
}

public sealed class CrashReportDialogRequest
{
    internal CrashReportDialogRequest(CrashReportRecord report)
    {
        Report = report;
        ReportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MolaGPT", "CrashReports");
    }

    public CrashReportRecord Report { get; }
    public string ReportDirectory { get; }
    public bool HasMinidump => Report.MinidumpFileName is not null;
    public bool IncludesApplicationLog => Report.IncludeApplicationLog;

    public async Task<string> ExportAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("An output path is required.", nameof(outputPath));
        await RunReporterAsync(["--export", "--id", Report.ReportId, "--output", Path.GetFullPath(outputPath)], cancellationToken)
            .ConfigureAwait(false);
        return Path.GetFullPath(outputPath);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default) =>
        await RunReporterAsync(["--delete", "--id", Report.ReportId], cancellationToken).ConfigureAwait(false);

    private static async Task RunReporterAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var reporterPath = Environment.ProcessPath;
        if (reporterPath is null || !File.Exists(reporterPath))
            throw new FileNotFoundException("App executable was not found.", reporterPath);

        var startInfo = new ProcessStartInfo(reporterPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(CrashReporterApplication.HostArgument);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the crash reporter.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var error = await errorTask.ConfigureAwait(false);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Crash reporter failed with exit code {process.ExitCode}."
                : error.Trim());
        }
    }
}

public static class CrashReportDialogHost
{
    private static Func<CrashReportDialogRequest, CancellationToken, Task<CrashReportDialogAction>>? _presenter;
    private static readonly TimeSpan AppDomainDialogTimeout = TimeSpan.FromMinutes(10);
    internal static bool HasPresenter => Volatile.Read(ref _presenter) is not null;

    public static IDisposable RegisterPresenter(
        Func<CrashReportDialogRequest, CancellationToken, Task<CrashReportDialogAction>> presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        Interlocked.Exchange(ref _presenter, presenter);
        return new PresenterRegistration(presenter);
    }

    internal static bool TryPresent(CrashReportRecord report, bool waitForDecision)
    {
        var presenter = Volatile.Read(ref _presenter);
        if (presenter is null) return false;

        var request = new CrashReportDialogRequest(report);
        if (Dispatcher.UIThread.CheckAccess() && waitForDecision)
            return false;

        var completion = new TaskCompletionSource<CrashReportDialogAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var action = await presenter(request, CancellationToken.None);
                completion.TrySetResult(action);
                if (!waitForDecision) ApplyDecision(action);
            }
            catch (Exception ex)
            {
                if (waitForDecision) completion.TrySetException(ex);
                else ApplyDecision(CrashReportDialogAction.Exit);
            }
        });

        if (!waitForDecision) return true;

        CrashReportDialogAction decision;
        try
        {
            if (!completion.Task.Wait(AppDomainDialogTimeout))
                decision = CrashReportDialogAction.Exit;
            else
                decision = completion.Task.GetAwaiter().GetResult();
        }
        catch
        {
            decision = CrashReportDialogAction.Exit;
        }

        ApplyDecision(decision);
        return true;
    }

    internal static void ApplyDecision(CrashReportDialogAction action)
    {
        if (action == CrashReportDialogAction.Restart)
            CrashReportBootstrap.RequestRestart();

        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(action == CrashReportDialogAction.Restart ? 0 : 1);
            else
                Environment.Exit(1);
        });
    }

    private sealed class PresenterRegistration(
        Func<CrashReportDialogRequest, CancellationToken, Task<CrashReportDialogAction>> presenter) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.CompareExchange(ref _presenter, null, presenter);
        }
    }
}
