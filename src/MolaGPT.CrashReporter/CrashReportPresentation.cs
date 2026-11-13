using System.Diagnostics;

namespace MolaGPT.CrashReporter;

public enum CrashReportAction { Exit, Restart }

/// <summary>Implemented by the independently running crash UI; no UI toolkit is required by this contract.</summary>
public interface ICrashReportPresenter
{
    CrashReportAction Show(CrashReportContext context);
}

public sealed class CrashReportContext
{
    private readonly CrashReportStore _store;

    internal CrashReportContext(CrashReportStore store, CrashReportRecord report, string? executable)
    {
        _store = store;
        Report = report;
        RestartExecutable = executable;
    }

    public CrashReportRecord Report { get; }
    public string? RestartExecutable { get; }
    public bool CanRestart => !string.IsNullOrWhiteSpace(RestartExecutable) && File.Exists(RestartExecutable);

    // These run on a worker so a UI's dispatcher is never blocked by ZIP I/O.
    public Task ExportAsync(string outputPath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!_store.Export(Report.ReportId, outputPath)) throw new FileNotFoundException("Crash report archive was not found.");
    }, cancellationToken);

    public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!_store.Delete(Report.ReportId)) throw new IOException("Could not delete the local crash report.");
    }, cancellationToken);

    internal void Apply(CrashReportAction action)
    {
        if (action != CrashReportAction.Restart) return;
        if (!CanRestart) throw new InvalidOperationException("No restart executable is available.");
        Process.Start(new ProcessStartInfo(RestartExecutable!) { UseShellExecute = false })?.Dispose();
    }
}
