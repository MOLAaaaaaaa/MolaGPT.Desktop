using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Net;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels;

/// <summary>
/// 设置 → 网络代理. Edits go to <see cref="AppProxy"/> and the settings table together,
/// a moment after typing stops: half a hostname is not a configuration, and a request
/// that starts mid-sentence should not be sent through it.
/// </summary>
public sealed partial class NetworkProxyViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Answers with the caller's exit region, so a passing test also says the proxy is the one being used.</summary>
    private const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    /// <summary>"socks5://127.0.0.1:7890" pasted into the host box.</summary>
    private static readonly Regex Endpoint = new(
        @"^\s*(?:(?<scheme>[a-z][a-z0-9+.-]*)://)?(?<host>[^\s:/@\[\]]+):(?<port>\d{1,5})/?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly AppProxy _proxy;
    private readonly SettingsRepository? _settings;
    private readonly CredentialStore? _credentials;
    private readonly Func<HttpClient>? _http;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private bool _loading;
    private CancellationTokenSource? _pending;
    private ProxyConfig? _dirty;

    /// <summary>Design-time only.</summary>
    public NetworkProxyViewModel() : this(new AppProxy(), null, null, null) { }

    public NetworkProxyViewModel(AppProxy proxy, SettingsRepository? settings, CredentialStore? credentials,
        Func<HttpClient>? http, Action<string>? log = null)
    {
        _proxy = proxy;
        _settings = settings;
        _credentials = credentials;
        _http = http;
        _log = log;

        // The same object the HTTP clients read, so there is nothing to disagree with.
        var config = proxy.Config;
        _loading = true;
        ModeIndex = (int)config.Mode;
        SchemeIndex = config.Scheme == ProxyScheme.Socks5 ? 1 : 0;
        Host = config.Host;
        Port = config.Port;
        Username = config.Username;
        Password = config.Password;
        Bypass = config.Bypass;
        ApplyToOfficial = config.ApplyToOfficial;
        _loading = false;
    }

    // Indexes match the ComboBox items in the page: 跟随系统 / 手动设置 / 直接连接.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    [NotifyPropertyChangedFor(nameof(ShowOfficial))]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private int _modeIndex;

    // HTTP / SOCKS5.
    [ObservableProperty] private int _schemeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private int _port;

    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private string? _bypass;
    [ObservableProperty] private bool _applyToOfficial = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestButtonText))]
    private bool _isTesting;

    [ObservableProperty] private string _testStatus = string.Empty;

    public bool IsManual => ModeIndex == (int)ProxyMode.Manual;

    /// <summary>With no proxy at all there is nothing for 官方服务 to be an exception to.</summary>
    public bool ShowOfficial => ModeIndex != (int)ProxyMode.None;

    public string TestButtonText => IsTesting ? "测试中…" : "测试连接";

    public string ModeHint => (ProxyMode)Math.Clamp(ModeIndex, 0, 2) switch
    {
        ProxyMode.System => "使用系统代理配置。",
        ProxyMode.Manual => "仅应用于 MolaGPT。",
        _ => "直接连接网络。"
    };

    public bool HasProblem => IsManual && !Build().IsManualUsable;

    public string Problem => "代理地址无效，当前直连。";

    partial void OnHostChanged(string? value)
    {
        if (_loading || value is null) return;
        var match = Endpoint.Match(value);
        if (!match.Success) return;

        if (match.Groups["scheme"].Success)
            SchemeIndex = match.Groups["scheme"].Value.StartsWith("socks", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);
        Host = match.Groups["host"].Value;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(ModeIndex):
            case nameof(SchemeIndex):
            case nameof(Host):
            case nameof(Port):
            case nameof(Username):
            case nameof(Password):
            case nameof(Bypass):
            case nameof(ApplyToOfficial):
                Schedule();
                break;
        }
    }

    private ProxyConfig Build() => new()
    {
        Mode = (ProxyMode)Math.Clamp(ModeIndex, 0, 2),
        Scheme = SchemeIndex == 1 ? ProxyScheme.Socks5 : ProxyScheme.Http,
        Host = Host?.Trim() ?? string.Empty,
        Port = Port,
        Username = Username?.Trim() ?? string.Empty,
        Password = Password ?? string.Empty,
        Bypass = Bypass?.Trim() ?? string.Empty,
        ApplyToOfficial = ApplyToOfficial
    };

    private void Schedule()
    {
        if (_loading) return;

        var config = Build();
        CancellationTokenSource timer;
        lock (_gate)
        {
            _dirty = config;
            _pending?.Cancel();
            _pending = timer = new CancellationTokenSource();
        }

        _ = Task.Delay(Debounce, timer.Token).ContinueWith(
            _ => Flush(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Applies and saves whatever is still waiting. Safe to call at any time —
    /// on closing the page, and before a test that must use what is on screen.</summary>
    public void Flush()
    {
        ProxyConfig? config;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = null;
            config = _dirty;
            _dirty = null;
        }

        if (config is null) return;

        // Live first: the running app should not depend on the disk keeping up.
        _proxy.Apply(config);
        if (_settings is null) return;
        try
        {
            ProxyConfigStore.Save(_settings, _credentials, config);
        }
        catch (Exception ex)
        {
            _log?.Invoke("保存代理设置失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        Flush();
        if (_http is null) return;

        var config = Build();
        if (config.Mode == ProxyMode.Manual && !config.IsManualUsable)
        {
            TestStatus = "代理地址无效。";
            return;
        }

        var route = config.Mode switch
        {
            ProxyMode.Manual => $"{config.Host}:{config.Port}",
            ProxyMode.None => "直连",
            _ => "系统代理"
        };

        IsTesting = true;
        TestStatus = "正在连接…";
        try
        {
            using var http = _http();
            using var timeout = new CancellationTokenSource(TestTimeout);
            var clock = Stopwatch.StartNew();
            using var response = await http.GetAsync(TraceUrl, timeout.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            clock.Stop();

            var region = Regex.Match(body, @"^loc=(\w+)", RegexOptions.Multiline);
            TestStatus = $"连接成功 · {route}"
                         + (region.Success ? $" · 出口 {region.Groups[1].Value}" : string.Empty)
                         + $" · {clock.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException)
        {
            TestStatus = $"连接超时 · {route}";
        }
        catch (Exception ex)
        {
            TestStatus = "连接失败：" + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }
}
