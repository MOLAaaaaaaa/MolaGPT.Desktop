using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MolaGPT.CrashReporter;

namespace MolaGPT.App.Views;

public sealed partial class CrashReportWindow : MolaContentWindow
{
    private readonly CrashReportContext _context;
    private readonly DispatcherTimer _deleteDisarm;
    private CancellationTokenSource? _operation;
    private string? _exportedPath;
    private bool _deleted;
    private bool _busy;

    public CrashReportAction Result { get; private set; } = CrashReportAction.Exit;

    // Required by the XAML loader / previewer only.
    public CrashReportWindow() : this(null!) { }

    internal CrashReportWindow(CrashReportContext context)
    {
        _context = context;
        InitializeComponent();
        _deleteDisarm = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) => DisarmDelete());
        if (context is null) return;

        Populate(context.Report);
        PART_Restart.IsEnabled = context.CanRestart;
        if (!context.CanRestart) ToolTip.SetTip(PART_Restart, "找不到 MolaGPT 程序文件，无法自动重启。");

        PART_Export.Click += OnExportClick;
        PART_Delete.Click += OnDeleteClick;
        PART_Copy.Click += OnCopyClick;
        PART_Reveal.Click += OnRevealClick;
        PART_Close.Click += (_, _) => Finish(CrashReportAction.Exit);
        PART_HeaderClose.Click += (_, _) => Finish(CrashReportAction.Exit);
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Finish(CrashReportAction.Exit);
            args.Handled = true;
        };
        PART_Restart.Click += (_, _) => Finish(CrashReportAction.Restart);
        Closing += (_, args) =>
        {
            // Don't let a half-written export be abandoned silently by the title bar X.
            if (_busy) args.Cancel = true;
        };
        Closed += (_, _) => _operation?.Cancel();
        Opened += (_, _) => (context.CanRestart ? PART_Restart : PART_Close).Focus();
    }

    private void Finish(CrashReportAction action)
    {
        if (_busy) return;
        Result = action;
        Close();
    }

    private void Populate(CrashReportRecord report)
    {
        if (report.Source.StartsWith("ProcessExit", StringComparison.Ordinal))
            PART_Title.Text = "MolaGPT Desktop 意外终止";

        PART_Time.Text = report.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        PART_Version.Text = $"{report.AppVersion}  ·  {report.Architecture}";
        PART_ErrorType.Text = DescribeErrorType(report);
        PART_Message.Text = DescribeMessage(report);
        PART_Artifacts.Text = DescribeArtifacts(report);
        PART_Details.Text = BuildDetails(report);

        PART_Privacy.Text = report.MinidumpFileName is not null
            ? "导出的压缩包包含内存转储和最近的应用日志。内存转储可能含有对话内容、文件路径、账号令牌等个人信息。请只把它发送给你信任的开发者，并在不需要时删除。"
            : "导出的压缩包包含错误信息和最近的应用日志，其中可能含有文件路径或对话片段。请只把它发送给你信任的开发者。";
    }

    private static string DescribeErrorType(CrashReportRecord report)
    {
        if (!string.IsNullOrWhiteSpace(report.ExceptionType)) return report.ExceptionType!;
        if (report.NativeExceptionCode is { } code) return $"原生异常 0x{code:X8}{NativeCodeName(code)}";
        if (report.Source.StartsWith("ProcessExit", StringComparison.Ordinal) && report.ExitCode is { } exit)
            return $"进程退出 0x{unchecked((uint)exit):X8}{NativeCodeName(unchecked((uint)exit))}";
        return report.Source;
    }

    private static string DescribeMessage(CrashReportRecord report)
    {
        if (report.Source.StartsWith("ProcessExit", StringComparison.Ordinal))
            return "应用在没有报告异常的情况下退出，未能捕获更多细节。日志中可能有线索。";
        if (report.NativeExceptionCode is not null && report.ExceptionType is null)
            return report.NativeExceptionAddress is { } address
                ? $"原生代码在地址 0x{address:X} 处发生故障。"
                : "原生代码发生故障。";
        return string.IsNullOrWhiteSpace(report.Message) ? "（无错误信息）" : report.Message!;
    }

    private static string DescribeArtifacts(CrashReportRecord report)
    {
        var parts = new List<string> { "错误信息" };
        if (report.IncludeApplicationLog) parts.Add("最近日志");
        var text = string.Join("、", parts);
        return report.MinidumpFileName is not null
            ? text + "、内存转储"
            : text + (string.IsNullOrWhiteSpace(report.MinidumpError) ? "（无内存转储）" : $"（内存转储未捕获：{report.MinidumpError}）");
    }

    private static string NativeCodeName(uint code) => code switch
    {
        0xC0000005 => "（访问冲突）",
        0xC00000FD => "（栈溢出）",
        0xC0000409 => "（栈缓冲区溢出 / 快速失败）",
        0xC0000374 => "（堆损坏）",
        0xC000001D => "（非法指令）",
        0xC0000094 => "（整数除零）",
        0xE0434352 => "（.NET 异常）",
        0x80000003 => "（断点）",
        _ => string.Empty
    };

    private static string BuildDetails(CrashReportRecord report)
    {
        var text = new StringBuilder();
        void Line(string key, object? value)
        {
            if (value is null || value is string { Length: 0 }) return;
            text.Append(key).Append(": ").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
        }

        Line("Report", report.ReportId);
        Line("Captured (UTC)", report.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        Line("App", $"{report.AppName} {report.AppVersion}");
        Line("Source", report.Source);
        Line("Runtime", report.Runtime);
        Line("OS", $"{report.OperatingSystem} ({report.Architecture})");
        Line("Process", report.ProcessId);
        Line("Thread", report.FaultingThreadId);
        if (report.ExitCode is { } exit) Line("Exit code", $"0x{unchecked((uint)exit):X8} ({exit})");
        if (report.NativeExceptionCode is { } code) Line("Native code", $"0x{code:X8}");
        if (report.NativeExceptionAddress is { } address) Line("Native address", $"0x{address:X}");
        Line("Minidump", report.MinidumpFileName is not null ? "captured" : report.MinidumpError ?? "not captured");
        text.Append('\n');
        text.Append(report.StackTrace ?? report.Message ?? string.Empty);
        return text.ToString().TrimEnd();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _deleted || !StorageProvider.CanSave) return;
        DisarmDelete();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出崩溃报告",
            SuggestedFileName = $"MolaGPT-crash-{_context.Report.CapturedAtUtc.ToLocalTime():yyyyMMdd-HHmmss}.zip",
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("ZIP 压缩包") { Patterns = ["*.zip"] }]
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        file.Dispose();
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowStatus("无法导出到该位置，请选择本地文件夹。", isError: true);
            return;
        }

        await RunAsync(async token =>
        {
            await _context.ExportAsync(path, token);
            _exportedPath = Path.GetFullPath(path);
            ShowStatus($"已导出到 {_exportedPath}", isError: false, canReveal: OperatingSystem.IsWindows());
        }, "导出失败");
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _deleted) return;
        // Two-step delete inside the window: a nested modal on top of a crash
        // dialog is easy to dismiss by accident.
        if (!PART_Delete.Classes.Contains("armed"))
        {
            PART_Delete.Classes.Add("armed");
            PART_Delete.Content = "再次点击以确认删除";
            _deleteDisarm.Start();
            return;
        }

        DisarmDelete();
        await RunAsync(async token =>
        {
            await _context.DeleteAsync(token);
            _deleted = true;
            PART_Export.IsEnabled = false;
            PART_Delete.IsEnabled = false;
            ShowStatus(_exportedPath is null
                ? "本地崩溃报告已删除。"
                : "本地崩溃报告已删除。已导出的副本仍保留在你选择的位置。", isError: false);
        }, "删除失败");
    }

    private void DisarmDelete()
    {
        _deleteDisarm.Stop();
        PART_Delete.Classes.Remove("armed");
        PART_Delete.Content = "删除报告";
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(PART_Details.Text ?? string.Empty);
                PART_Copy.Content = "已复制";
                DispatcherTimer.RunOnce(() => PART_Copy.Content = "复制技术详情", TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Clipboard contention with another app; the text box remains selectable.
        }
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (_exportedPath is null || !OperatingSystem.IsWindows()) return;
        try
        {
            // explorer.exe parses its own command line; Windows paths cannot contain quotes.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_exportedPath}\"")
            {
                UseShellExecute = false
            })?.Dispose();
        }
        catch { }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, string failurePrefix)
    {
        _busy = true;
        _operation = new CancellationTokenSource();
        SetButtonsEnabled(false);
        try
        {
            await operation(_operation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowStatus($"{failurePrefix}：{ex.Message}", isError: true);
        }
        finally
        {
            _busy = false;
            _operation.Dispose();
            _operation = null;
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        PART_Export.IsEnabled = enabled && !_deleted;
        PART_Delete.IsEnabled = enabled && !_deleted;
        PART_Close.IsEnabled = enabled;
        PART_HeaderClose.IsEnabled = enabled;
        PART_Restart.IsEnabled = enabled && _context.CanRestart;
    }

    private void ShowStatus(string message, bool isError, bool canReveal = false)
    {
        PART_StatusPanel.IsVisible = true;
        PART_Status.Text = message;
        PART_Status.Foreground = (isError
            ? this.FindResource(ActualThemeVariant, "Brush.Error")
            : this.FindResource(ActualThemeVariant, "Brush.Success.Foreground")) as Avalonia.Media.IBrush;
        PART_Reveal.IsVisible = canReveal && !isError;
    }
}
