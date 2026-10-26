using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Remote;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.Desktop.Services.Remote;

public sealed partial class PairedPhoneRow(PairedPhone phone, Action<PairedPhoneRow> remove)
{
    public string PairId => phone.PairId;
    public string Name => phone.DeviceName;
    public long LastSeenAtMs => phone.LastSeenAtMs;
    public string Detail => "配对于 " + DateTimeOffset.FromUnixTimeMilliseconds(phone.CreatedAtMs).ToLocalTime().ToString("yyyy-MM-dd")
                            + (phone.LastSeenAtMs > phone.CreatedAtMs
                                ? " · 最近使用 " + DateTimeOffset.FromUnixTimeMilliseconds(phone.LastSeenAtMs).ToLocalTime().ToString("MM-dd HH:mm")
                                : string.Empty);

    [RelayCommand]
    private void Remove() => remove(this);
}

/// <summary>The 设置 → 远程控制 card. One switch: while on, phones signed in to
/// the same account pair with one tap, and any phone pairs with a code shown
/// here (the anonymous link, end-to-end encrypted).</summary>
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
        host.AnonymousPairingChanged += () => _post(RefreshPairing);
        host.Phones.Changed += () => _post(RefreshPhones);
        host.ConnectedPhonesChanged += () => _post(() => PhoneConnected = host.HasConnectedPhones);
        _countdown = new Timer(_ => _post(RefreshCountdown), null, Timeout.Infinite, Timeout.Infinite);
        RefreshState();
        RefreshPairing();
        RefreshPhones();
    }

    /// <summary>Raised when the switch flips, so the app starts or stops the host.</summary>
    public event Action? EnabledChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel), nameof(CanBeginPairing))]
    private bool _isEnabled;

    [ObservableProperty] private string _statusText = "已停用";
    [ObservableProperty] private string? _pairingCode;
    [ObservableProperty] private string? _pairingHint;
    [ObservableProperty] private string? _confirmName;
    [ObservableProperty] private string? _confirmSas;
    [ObservableProperty] private string? _error;

    /// <summary>A phone is talking to this desktop right now; the title bar shows a dot.</summary>
    [ObservableProperty] private bool _phoneConnected;

    public string ToggleLabel => IsEnabled ? "停用" : "启用";
    public bool IsPairing => PairingCode is not null;
    public bool HasConfirm => ConfirmSas is not null;
    public bool CanBeginPairing => IsEnabled && !IsPairing && !HasConfirm;
    public ObservableCollection<PairedPhoneRow> Phones { get; } = new();
    public bool HasPhones => Phones.Count > 0;

    partial void OnIsEnabledChanged(bool value)
    {
        _settings.Set(EnabledKey, value ? "true" : "false");
        if (!value) _host.CancelAnonymousPairing();
        Error = null;
        EnabledChanged?.Invoke();
        RefreshState();
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
    private void Toggle() => IsEnabled = !IsEnabled;

    [RelayCommand]
    private async Task BeginPairingAsync()
    {
        Error = null;
        try { await _host.BeginAnonymousPairingAsync(); }
        catch (Exception ex) { Error = "无法生成配对码：" + ex.Message; }
    }

    [RelayCommand]
    private void CancelPairing() => _host.CancelAnonymousPairing();

    [RelayCommand]
    private Task AllowPairing() => _host.ConfirmAnonymousPairingAsync(true);

    [RelayCommand]
    private Task DenyPairing() => _host.ConfirmAnonymousPairingAsync(false);

    private void RefreshState()
    {
        StatusText = !IsEnabled ? "已停用" : _host.State switch
        {
            RemoteControlState.Online => "已连接",
            RemoteControlState.Connecting => "连接中…",
            RemoteControlState.Offline => "连接中断，正在重连",
            _ => "等待配对",
        };
    }

    private void RefreshPairing()
    {
        var confirm = _host.AnonymousConfirm;
        ConfirmName = confirm?.PhoneName;
        ConfirmSas = confirm is null ? null : confirm.Sas is { Length: 6 } sas ? $"{sas[..3]} {sas[3..]}" : confirm.Sas;
        var room = confirm is null ? _host.AnonymousRoomCode : null;
        PairingCode = room is null ? null : RemotePairing.FormatCode(room);
        OnPropertyChanged(nameof(IsPairing));
        OnPropertyChanged(nameof(HasConfirm));
        OnPropertyChanged(nameof(CanBeginPairing));
        RefreshCountdown();
    }

    private void RefreshCountdown()
    {
        if (PairingCode is not null && _host.AnonymousRoomExpiresAt is { } expires)
        {
            var left = Math.Max(0, (int)(expires - DateTimeOffset.UtcNow).TotalSeconds);
            if (left == 0)
            {
                _host.CancelAnonymousPairing();
                return;
            }
            PairingHint = $"在手机端输入 · {left / 60}:{left % 60:00} 后失效";
            _countdown.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
        else PairingHint = null;
    }

    private void RefreshPhones()
    {
        Phones.Clear();
        var account = _host.Phones.List().Select(phone => new PairedPhoneRow(phone, row => _host.Unpair(row.PairId)));
        var anonymous = _host.Phones.AnonymousList().Where(p => p.State == PairedPhoneStore.Active)
            .Select(phone => new PairedPhoneRow(phone, row => _host.UnpairAnonymous(row.PairId)));
        foreach (var row in account.Concat(anonymous).OrderByDescending(row => row.LastSeenAtMs)) Phones.Add(row);
        OnPropertyChanged(nameof(HasPhones));
    }
}
