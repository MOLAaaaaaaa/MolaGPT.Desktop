using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Remote;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.Desktop.Services.Remote;

public sealed partial class PairedPhoneRow(PairedPhone phone, Action<PairedPhoneRow> remove) : ObservableObject
{
    public string PairId => phone.PairId;
    public string Name => phone.DeviceName;
    public string Detail => "配对时间 " + DateTimeOffset.FromUnixTimeMilliseconds(phone.CreatedAtMs).ToLocalTime().ToString("yyyy-MM-dd")
                            + " · 最近使用 " + DateTimeOffset.FromUnixTimeMilliseconds(phone.LastSeenAtMs).ToLocalTime().ToString("MM-dd HH:mm");

    [RelayCommand]
    private void Remove() => remove(this);
}

/// <summary>The 设置 → 远程控制 card for controlling MolaGPT conversations from a phone.</summary>
public sealed partial class RemoteWorkSettingsViewModel : ObservableObject
{
    public const string EnabledKey = "remote.work.enabled";
    private const string HintedKey = "remote.work.hinted";

    private readonly RemoteControlHost _host;
    private readonly SettingsRepository _settings;
    private readonly Action<Action> _post;
    private readonly Timer _countdown;

    public RemoteWorkSettingsViewModel(RemoteControlHost host, SettingsRepository settings, Action<Action> post)
    {
        _host = host;
        _settings = settings;
        _post = post;
        _isEnabled = settings.Get(EnabledKey) == "true";
        host.StateChanged += () => _post(RefreshState);
        host.PairingChanged += () => _post(RefreshPairing);
        host.Paired += (name, _) => _post(() => PairedMessage = $"已配对：{name}");
        host.Phones.Changed += () => _post(RefreshPhones);
        host.ConnectedPhonesChanged += () => _post(() => PhoneConnected = host.HasConnectedPhones);
        _countdown = new Timer(_ => _post(RefreshPairing), null, Timeout.Infinite, Timeout.Infinite);
        RefreshState();
        RefreshPhones();
    }

    /// <summary>Raised when the switch flips, so the app starts or stops the host.</summary>
    public event Action? EnabledChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _isEnabled;

    [ObservableProperty] private string _statusText = "已停用";
    [ObservableProperty] private string? _pairingCode;
    [ObservableProperty] private string? _pairingHint;
    [ObservableProperty] private string? _pairedMessage;

    /// <summary>A phone is talking to this desktop right now; the title bar shows a dot.</summary>
    [ObservableProperty] private bool _phoneConnected;

    public string ToggleLabel => IsEnabled ? "停用" : "启用";
    public bool IsPairing => PairingCode is not null;
    public bool CanPair => IsEnabled && _host.State == RemoteControlState.Online && !IsPairing;
    public ObservableCollection<PairedPhoneRow> Phones { get; } = new();
    public bool HasPhones => Phones.Count > 0;

    partial void OnPairingCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsPairing));
        OnPropertyChanged(nameof(CanPair));
    }

    /// <summary>
    /// True the first time it is asked, false ever after: the "use your phone" prompt
    /// shows once, and closing it is the same as never seeing it again.
    /// </summary>
    public bool TryClaimHint()
    {
        if (_settings.Get(HintedKey) == "true") return false;
        _settings.Set(HintedKey, "true");
        return true;
    }

    [RelayCommand]
    private void Toggle()
    {
        IsEnabled = !IsEnabled;
        _settings.Set(EnabledKey, IsEnabled ? "true" : "false");
        if (!IsEnabled) _host.CancelPairing();
        PairedMessage = null;
        EnabledChanged?.Invoke();
        RefreshState();
    }

    [RelayCommand]
    private void BeginPairing()
    {
        PairedMessage = null;
        _host.BeginPairing();
    }

    [RelayCommand]
    private void CancelPairing() => _host.CancelPairing();

    private void RefreshState()
    {
        StatusText = !IsEnabled ? "已停用" : _host.State switch
        {
            RemoteControlState.Online => "已连接",
            RemoteControlState.Connecting => "连接中…",
            RemoteControlState.Offline => "连接已断开，正在重连",
            _ => "未登录 MolaGPT 账号"
        };
        OnPropertyChanged(nameof(CanPair));
    }

    private void RefreshPairing()
    {
        var code = _host.PairingCode;
        PairingCode = code is null ? null : RemotePairing.FormatCode(code);
        if (_host.PairingExpiresAt is { } expires)
        {
            var left = expires - DateTimeOffset.UtcNow;
            PairingHint = $"在手机端「电脑」页面输入配对码 · {Math.Max(0, (int)left.TotalSeconds) / 60}:{Math.Max(0, (int)left.TotalSeconds) % 60:00} 后失效";
            _countdown.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
        else
        {
            PairingHint = null;
            _countdown.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void RefreshPhones()
    {
        Phones.Clear();
        foreach (var phone in _host.Phones.List().OrderByDescending(p => p.LastSeenAtMs))
            Phones.Add(new PairedPhoneRow(phone, row => _host.Unpair(row.PairId)));
        OnPropertyChanged(nameof(HasPhones));
    }
}
