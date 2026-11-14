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

    internal CrashReportWindow(CrashReportContext context)
    {
        _context = context;
        InitializeComponent();
        _deleteDisarm = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) => DisarmDelete());

        Populate(context.Report);
        PART_Restart.IsEnabled = context.CanRestart;
        if (!context.CanRestart) ToolTip.SetTip(PART_Restart, "找不到 MolaGPT 程序文件，无法自动重启。");

        PART_DetailsToggle.Click += (_, _) => ToggleDetails();
        PART_Export.Click += OnExportClick;
        PART_Delete.Click += OnDeleteClick;
        PART_Copy.Click += OnCopyClick;
        PART_Reveal.Click += OnRevealClick;
        PART_Close.Click += (_, _) => Finish(CrashReportAction.Exit);
        PART_HeaderClose.Click += (_, _) => Finish(CrashReportAction.Exit);
        PART_Restart.Click += (_, _) => Finish(CrashReportAction.Restart);
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Finish(CrashReportAction.Exit);
            args.Handled = true;
        };
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
        var processExit = report.Source.StartsWith("ProcessExit", StringComparison.Ordinal);
        if (processExit) PART_Title.Text = "MolaGPT 意外终止";

        PART_ErrorType.Text = DescribeErrorType(report, processExit);
        var message = DescribeMessage(report, processExit);
        PART_Message.Text = message;
        PART_MessageScroll.IsVisible = message is not null;
        PART_Meta.Text = $"{report.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  ·  v{report.AppVersion}";
        PART_Details.Text = BuildDetails(report);
        PART_Hint.Text = report.MinidumpFileName is not null
            ? "导出的压缩包含内存转储，可能有对话内容和账号令牌，请只发给信任的开发者。"
            : "导出的压缩包含错误信息和最近日志，可能有文件路径或对话片段。";
    }

    private static string DescribeErrorType(CrashReportRecord report, bool processExit)
    {
        if (!string.IsNullOrWhiteSpace(report.ExceptionType))
        {
            // 完整命名空间留给技术详情，卡片里只放类名。
            var type = report.ExceptionType!;
            var dot = type.LastIndexOf('.');
            return dot >= 0 && dot < type.Length - 1 ? type[(dot + 1)..] : type;
        }
        if (report.NativeExceptionCode is { } code) return DescribeCode("原生崩溃", code);
        if (processExit && report.ExitCode is { } exit) return DescribeCode("进程异常退出", unchecked((uint)exit));
        return "未知错误";
    }

    private static string DescribeCode(string prefix, uint code) => NativeCodeName(code) is { } name
        ? $"{prefix}：{name}"
        : $"{prefix}（0x{code:X8}）";

    private static string? DescribeMessage(CrashReportRecord report, bool processExit)
    {
        if (processExit) return "没有捕获到异常信息。";
        if (report.ExceptionType is null) return null;
        return string.IsNullOrWhiteSpace(report.Message) ? null : report.Message;
    }

    private static string? NativeCodeName(uint code) => code switch
    {
        0xC0000005 => "访问冲突",
        0xC00000FD => "栈溢出",
        0xC0000409 => "快速失败",
        0xC0000374 => "堆损坏",
        0xC000001D => "非法指令",
        0xC0000094 => "整数除零",
        0xE0434352 => ".NET 异常",
        0x80000003 => "断点",
        _ => null
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

    private void ToggleDetails()
    {
        var showDetails = !PART_Details.IsVisible;
        PART_ErrorCard.IsVisible = !showDetails;
        PART_Details.IsVisible = showDetails;
        PART_DetailsBackIcon.IsVisible = showDetails;
        PART_DetailsChevron.IsVisible = !showDetails;
        PART_DetailsToggleText.Text = showDetails ? "返回概览" : "技术详情";
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
            ShowStatus($"已导出 {Path.GetFileName(_exportedPath)}", isError: false, canReveal: OperatingSystem.IsWindows());
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
            PART_Delete.Content = "确认删除";
            _deleteDisarm.Start();
            return;
        }

        DisarmDelete();
        await RunAsync(async token =>
        {
            await _context.DeleteAsync(token);
            _deleted = true;
            ShowStatus(_exportedPath is null ? "本地报告已删除。" : "本地报告已删除，导出的副本仍保留。",
                isError: false, canReveal: _exportedPath is not null && OperatingSystem.IsWindows());
        }, "删除失败");
    }

    private void DisarmDelete()
    {
        _deleteDisarm.Stop();
        PART_Delete.Classes.Remove("armed");
        PART_Delete.Content = "删除";
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(PART_Details.Text ?? string.Empty);
                PART_Copy.Content = "已复制";
                DispatcherTimer.RunOnce(() => PART_Copy.Content = "复制", TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Clipboard contention with another app; the details stay selectable.
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
        PART_Hint.Text = message;
        PART_Hint.Foreground = (isError
            ? this.FindResource(ActualThemeVariant, "Brush.Error")
            : this.FindResource(ActualThemeVariant, "Brush.Success.Foreground")) as Avalonia.Media.IBrush;
        PART_HintIcon.Text = isError ? "\uE783" : "\uE73E";
        PART_HintIcon.Foreground = PART_Hint.Foreground;
        PART_Reveal.IsVisible = canReveal && !isError;
    }
}
