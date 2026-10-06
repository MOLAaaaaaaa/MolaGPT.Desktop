using System.IO;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// The one way the managed Python runtime gets installed, and the two moments it
/// is offered.
///
/// 设置 → 代码与文件 used to own the install outright, so no banner could start
/// one, and anything that did would have had to remember to also switch the tool
/// on and point it at the new interpreter. Both now come through here.
/// </summary>
public sealed class PythonRuntimeSetup
{
    public const string NotificationKey = "python-runtime";

    // Formats whose original — not the extracted text the chat already gets —
    // takes code to work with: computing over a sheet, pulling a figure out of a
    // PDF, editing a document in place.
    private static readonly HashSet<string> OriginalNeedsPython = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xlsm", ".xls", ".csv", ".tsv", ".parquet",
        ".pdf",
        ".docx", ".docm", ".doc",
        ".pptx", ".pptm", ".ppt"
    };

    private readonly PythonRuntimeManager _runtime;
    private readonly SettingsViewModel _settings;
    private readonly NotificationCenter _notifications;
    private Task<PythonSetupResult>? _running;
    private bool _attachmentOfferShown;

    public PythonRuntimeSetup(
        PythonRuntimeManager runtime,
        SettingsViewModel settings,
        NotificationCenter notifications)
    {
        _runtime = runtime;
        _settings = settings;
        _notifications = notifications;
    }

    /// <summary>A managed runtime is installed, or the user picked an
    /// interpreter that still exists.</summary>
    public bool IsAvailable
    {
        get
        {
            if (_runtime.GetInstalledRuntime() is not null) return true;
            var external = _settings.PythonToolExecutablePath?.Trim().Trim('"');
            return !string.IsNullOrWhiteSpace(external) && File.Exists(external);
        }
    }

    private bool IsInstalling => _running is { IsCompleted: false };

    /// <summary>
    /// Download, install, switch the tool on and select the interpreter. A call
    /// while one is running joins it instead of starting a second download; the
    /// joiner's <paramref name="observer"/> is not attached, the banner carries
    /// the progress either way.
    /// </summary>
    public Task<PythonSetupResult> InstallAsync(IProgress<PythonRuntimeProgress>? observer = null)
    {
        if (_running is { IsCompleted: false } running) return running;
        return _running = RunAsync(observer);
    }

    private async Task<PythonSetupResult> RunAsync(IProgress<PythonRuntimeProgress>? observer)
    {
        // The banner is what makes this survive closing the settings window: the
        // row there only exists while that page is open, and the download
        // outlives it.
        _notifications.Progress(NotificationKey, "正在配置 Python 环境", "获取清单…");
        try
        {
            var progress = new Progress<PythonRuntimeProgress>(item =>
            {
                observer?.Report(item);
                _notifications.Progress(
                    NotificationKey,
                    string.IsNullOrWhiteSpace(item.Message) ? "正在配置 Python 环境" : item.Message,
                    string.IsNullOrWhiteSpace(item.Stage) ? null : item.Stage,
                    item.Progress > 0 ? item.Progress : null);
            });
            var runtime = await _runtime.DownloadAndInstallAsync(progress, CancellationToken.None);
            _settings.PythonToolEnabled = true;
            _settings.PythonToolExecutablePath = runtime.PythonExecutablePath;
            _notifications.Success("Python 环境已就绪", $"Python {runtime.Version}", NotificationKey);
            return new PythonSetupResult(runtime, null);
        }
        catch (Exception ex)
        {
            _notifications.Error("Python 环境配置失败", ex.Message, NotificationKey);
            return new PythonSetupResult(null, ex.Message);
        }
    }

    /// <summary>
    /// Once, right after the agent runtime first lands: that download is where a
    /// new user decides how much to set up, and Python is the optional other half.
    /// Sticky, because an offer that times out before it is read was never made.
    /// </summary>
    public void OfferAfterAgentRuntime()
    {
        if (_settings.PythonSetupOffered || IsInstalling || IsAvailable) return;

        _settings.PythonSetupOffered = true;
        _notifications.Notify(new AppNotification
        {
            Key = NotificationKey,
            Kind = NotifyKind.Info,
            Title = "安装 Python？",
            Body = "用于运行代码，处理表格、PDF 与 Office 文件等高级功能。",
            ActionText = "安装",
            Action = () => _ = InstallAsync(),
            Sticky = true
        });
    }

    /// <summary>
    /// When a file whose original takes code is attached and there is nothing to
    /// run that code. Once per run: the second spreadsheet tells the user nothing
    /// the first one did not.
    /// </summary>
    public void OfferForAttachments(IEnumerable<Attachment> added)
    {
        if (_attachmentOfferShown || IsInstalling) return;
        if (!added.Any(NeedsPython) || IsAvailable) return;

        _attachmentOfferShown = true;
        _notifications.Notify(new AppNotification
        {
            Key = NotificationKey,
            Kind = NotifyKind.Info,
            Title = "处理该文件需要 Python",
            Body = "安装后可以运行代码，处理表格、PDF 与 Office 文件等高级功能。",
            ActionText = "安装 Python",
            Action = () => _ = InstallAsync()
        });
    }

    private static bool NeedsPython(Attachment attachment) =>
        !attachment.IsImage
        && OriginalNeedsPython.Contains(Path.GetExtension(attachment.FileName ?? string.Empty));
}

public sealed record PythonSetupResult(InstalledPythonRuntime? Runtime, string? Error);
