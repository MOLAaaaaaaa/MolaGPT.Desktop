using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat.Tools.Browser;

namespace MolaGPT.ViewModels;

/// <summary>
/// The browser settings page's live view of the local Kimi bridge.
///
/// Four states matter and they are not the same problem: never checked, not
/// installed (the user needs the extension), installed but not listening (one
/// click fixes it), and listening with no browser attached (the extension is off
/// or the browser is closed). Collapsing them into "不可用" is what sends people
/// to a support thread, so each gets its own sentence, its own colour, and its
/// own next step.
/// </summary>
public sealed partial class BrowserBridgeStatusViewModel : ObservableObject
{
    private readonly WebBridgeClient _client;
    private readonly Func<DateTimeOffset> _now;

    public BrowserBridgeStatusViewModel(WebBridgeClient client, Func<DateTimeOffset>? now = null)
    {
        _client = client;
        _now = now ?? (() => DateTimeOffset.Now);
        _daemonUrl = WebBridgeAddress.Resolve();
    }

    public const string InstallUrl = "https://www.kimi.ai/zh-hans/products/kimi-webbridge";
    public const string HelpUrl = "https://www.kimi.ai/zh-hans/help/kimi-webbridge";
    public const string ChromeStoreUrl = "https://chromewebstore.google.com/detail/kimi/fldmhceldgbpfpkbgopacenieobmligc";
    public const string EdgeStoreUrl = "https://microsoftedge.microsoft.com/addons/detail/kimi/bnlffdbcfnanfbknnlaflhlhkocccckg";

    [ObservableProperty] private string _statusTitle = "尚未检测";
    [ObservableProperty] private string _statusDetail = "检测本地服务与浏览器扩展的连接状态。";
    [ObservableProperty] private string _daemonUrl;

    /// <summary>"刚刚检测 · 23:41"，或检测进行中的字样。空串表示还没检测过。</summary>
    [ObservableProperty] private string _lastCheckedText = string.Empty;

    [ObservableProperty] private bool _isBusy;

    // Tone. Exactly one is true once a check has run; all false before that, so
    // the dot reads as "unknown" rather than as a passing result.
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _isWarn;
    [ObservableProperty] private bool _isError;

    /// <summary>Daemon answered and a browser is attached.</summary>
    [ObservableProperty] private bool _isReady;

    /// <summary>Binary on disk, nothing listening — offer to start it.</summary>
    [ObservableProperty] private bool _canStart;

    /// <summary>Nothing installed — the install guide is the only useful next step.</summary>
    [ObservableProperty] private bool _needsInstall;

    [RelayCommand]
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusTitle = "检测中";
        StatusDetail = "正在连接本地服务…";
        LastCheckedText = string.Empty;
        try
        {
            DaemonUrl = WebBridgeAddress.Resolve();
            var status = await _client
                .GetStatusAsync(DaemonUrl, TimeSpan.FromSeconds(6), ct)
                .ConfigureAwait(true);
            Apply(status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Kimi's documented install line, shown verbatim so the user can
    /// see and copy what MolaGPT is about to run. (Named …Text because the
    /// generated command for <see cref="InstallAsync"/> owns "InstallCommand".)</summary>
    public string InstallCommandText => WebBridgeInstaller.DisplayCommand;

    /// <summary>Live output from the installer; empty when idle.</summary>
    [ObservableProperty] private string _installLog = string.Empty;

    [ObservableProperty] private bool _isInstalling;

    [RelayCommand]
    public async Task InstallAsync(CancellationToken ct = default)
    {
        if (IsInstalling) return;
        IsInstalling = true;
        InstallLog = "正在下载安装脚本…";
        try
        {
            var progress = new Progress<string>(line => InstallLog = line);
            var result = await WebBridgeInstaller.RunAsync(progress, ct).ConfigureAwait(true);
            InstallLog = result.Success ? "本地服务已安装。" : result.Error ?? "配置失败。";
            if (result.Success) await CheckAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusTitle = "正在启动";
        StatusDetail = "启动本地服务…";
        try
        {
            var started = await WebBridgeClient.TryStartInstalledDaemonAsync(ct).ConfigureAwait(true);
            if (!started)
            {
                SetTone(error: true);
                StatusTitle = "启动失败";
                StatusDetail = "无法启动本地服务，可在终端运行 kimi-webbridge start 查看原因。";
                Stamp();
                return;
            }

            var status = await _client
                .GetStatusAsync(DaemonUrl, TimeSpan.FromSeconds(8), ct)
                .ConfigureAwait(true);
            Apply(status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(WebBridgeStatus status)
    {
        var installed = WebBridgeAddress.IsInstalled;
        IsReady = status.Running && status.ExtensionConnected;
        CanStart = !status.Running && installed;
        NeedsInstall = !status.Running && !installed;
        Stamp();

        if (IsReady)
        {
            SetTone(ok: true);
            var version = status.ExtensionVersion is { Length: > 0 } ext ? $"扩展 {ext} · " : string.Empty;
            StatusTitle = "已连接";
            StatusDetail = $"{version}{status.DaemonUrl} · 连接测试正常";
            return;
        }

        if (status.Running)
        {
            // Half-working is its own colour: the part the user installed is
            // fine, so pointing them at the installer again would be wrong.
            SetTone(warn: true);
            StatusTitle = "浏览器未连接";
            StatusDetail = "本地服务已运行，未检测到浏览器接入。打开 Chrome/Edge 并确认扩展已启用后重新检测。";
            return;
        }

        SetTone(error: true);
        if (installed)
        {
            StatusTitle = "服务未启动";
            StatusDetail = $"{status.DaemonUrl} 无响应，可尝试单击「启动服务」。";
            return;
        }

        StatusTitle = "未配置";
        StatusDetail = "需要 Kimi 浏览器扩展与本地服务，见「配置指引」。";
    }

    private void SetTone(bool ok = false, bool warn = false, bool error = false)
    {
        IsOk = ok;
        IsWarn = warn;
        IsError = error;
    }

    private void Stamp() => LastCheckedText = $"最近检测 {_now():HH:mm:ss}";
}
