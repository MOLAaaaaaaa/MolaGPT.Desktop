using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat.Agents;
using MolaGPT.Core.Chat.Agents.Relay;

namespace MolaGPT.ViewModels.Agents;

/// <summary>
/// Thin renderer over the headless <see cref="AgentBridgeService"/> for the
/// minimal desktop status surface: a session list with phase / attention badges,
/// a new-session row (backend + working folder), and a one-line send + interrupt
/// to drive a live turn for local verification. It shows NO transcript — the
/// transcript lives on the phone (Phase 3); here we only surface session state.
///
/// <see cref="AgentBridgeService.SessionsChanged"/> fires on the bridge's timer
/// thread, so updates are marshalled to the captured <see cref="SynchronizationContext"/>
/// (the Dispatcher sync context when the VM is constructed on the UI thread)
/// before touching the bound <see cref="Sessions"/> collection.
/// </summary>
public sealed partial class AgentBridgeStatusViewModel : ObservableObject
{
    private readonly AgentBridgeService _bridge;
    private readonly AgentRelayClient _relay;
    private readonly SynchronizationContext _sync;

    /// <summary>Bound session list (plain DTOs — phase/attention/title/cwd).</summary>
    public ObservableCollection<AgentSessionStateDto> Sessions { get; } = new();

    public ObservableCollection<RelayMobileDevice> MobileDevices { get; } = new();

    /// <summary>Backend choices for the new-session row.</summary>
    public IReadOnlyList<string> Backends { get; } = new[] { "claude-code", "codex" };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private AgentSessionStateDto? _selectedSession;

    [ObservableProperty] private string _selectedBackend = "claude-code";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = string.Empty;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _bridgeStatusText = "已停用";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BridgeToggleLabel))]
    private bool _isBridgeEnabled;

    /// <summary>Label for the enable/disable button in the settings card.</summary>
    public string BridgeToggleLabel => IsBridgeEnabled ? "停用" : "启用";

    /// <summary>Desktop-wired: show the privacy disclosure and return the user's
    /// consent. Enabling is gated on this — null means consent is assumed (tests).</summary>
    public Func<Task<bool>>? ConfirmEnableAsync { get; set; }

    /// <summary>Desktop-wired: persist the flag and start/stop the relay loop.</summary>
    public Action<bool>? ApplyBridgeEnabled { get; set; }

    [ObservableProperty] private int _sessionCount;
    [ObservableProperty] private int _mobileDeviceCount;
    [ObservableProperty] private bool _hasMobileDevices;
    [ObservableProperty] private int _projectedSessionCount;
    [ObservableProperty] private int _queuedProjectionCount;
    [ObservableProperty] private int _activeProjectionCount;

    /// <summary>Folder-picker hook wired by the Desktop layer (returns chosen dir or null).</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public AgentBridgeStatusViewModel(AgentBridgeService bridge, AgentRelayClient relay)
    {
        _bridge = bridge;
        _relay = relay;
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();
        bridge.SessionsChanged += OnSessionsChanged;
        _ = LoadAsync();
    }

    private void OnSessionsChanged(IReadOnlyList<AgentSessionStateDto> snap)
    {
        _sync.Post(_ => Replace(snap), null);
        _ = RefreshRelayStatusAsync();
    }

    /// <summary>Replace the session list while preserving the current selection.</summary>
    private void Replace(IReadOnlyList<AgentSessionStateDto> snap)
    {
        var selId = SelectedSession?.ConversationId;
        Sessions.Clear();
        foreach (var s in snap) Sessions.Add(s);
        SessionCount = Sessions.Count;
        if (selId is not null)
            SelectedSession = Sessions.FirstOrDefault(s => s.ConversationId == selId)
                              ?? Sessions.FirstOrDefault();
        else
            SelectedSession ??= Sessions.FirstOrDefault();
    }

    /// <summary>Refresh from the bridge (live + on-disk history sessions).</summary>
    public async Task LoadAsync()
    {
        try
        {
            var snap = await _bridge.ListSessionsAsync().ConfigureAwait(false);
            _sync.Post(_ =>
            {
                Replace(snap);
                BridgeStatusText = IsBridgeEnabled ? "运行中" : "已停用";
            }, null);
            await RefreshRelayStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _sync.Post(_ => BridgeStatusText = "异常：" + ex.Message, null);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync().ConfigureAwait(false);

    /// <summary>Seed the toggle from the persisted setting (Desktop layer, at startup).</summary>
    public void InitializeBridgeEnabled(bool enabled)
    {
        IsBridgeEnabled = enabled;
        BridgeStatusText = enabled ? "运行中" : "已停用";
    }

    /// <summary>Enable/disable the cloud relay bridge. Enabling requires explicit
    /// consent via <see cref="ConfirmEnableAsync"/> (privacy disclosure); the actual
    /// start/stop + persistence is delegated to <see cref="ApplyBridgeEnabled"/>.</summary>
    [RelayCommand]
    private async Task ToggleBridgeAsync()
    {
        if (!IsBridgeEnabled)
        {
            var consent = ConfirmEnableAsync is null || await ConfirmEnableAsync().ConfigureAwait(true);
            if (!consent) return;
            IsBridgeEnabled = true;
            BridgeStatusText = "运行中";
            ApplyBridgeEnabled?.Invoke(true);
        }
        else
        {
            IsBridgeEnabled = false;
            BridgeStatusText = "已停用";
            ApplyBridgeEnabled?.Invoke(false);
            await RefreshRelayStatusAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshRelayStatusAsync()
    {
        if (!IsBridgeEnabled)
        {
            // Disabled: don't query the relay; surface zeroed counts so the UI doesn't
            // imply an active connection.
            _sync.Post(_ =>
            {
                ProjectedSessionCount = 0;
                QueuedProjectionCount = 0;
                ActiveProjectionCount = 0;
                MobileDevices.Clear();
                MobileDeviceCount = 0;
                HasMobileDevices = false;
            }, null);
            return;
        }

        var status = _relay.GetProjectionStatus();
        IReadOnlyList<RelayMobileDevice> devices = Array.Empty<RelayMobileDevice>();
        try
        {
            devices = await _relay.ListMobileDevicesAsync().ConfigureAwait(false);
        }
        catch
        {
            // Status remains useful even if the relay endpoint is offline.
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var connected = devices
            .Where(d => d.LastSeenAtMs <= 0 || nowMs - d.LastSeenAtMs <= TimeSpan.FromMinutes(2).TotalMilliseconds)
            .OrderByDescending(d => d.LastSeenAtMs)
            .ToList();

        _sync.Post(_ =>
        {
            ProjectedSessionCount = status.ProjectedSessions;
            QueuedProjectionCount = status.QueuedProjections;
            ActiveProjectionCount = status.ActiveProjections;
            MobileDevices.Clear();
            foreach (var device in connected)
                MobileDevices.Add(device);
            MobileDeviceCount = MobileDevices.Count;
            HasMobileDevices = MobileDevices.Count > 0;
        }, null);
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (PickFolderAsync is null) return;
        var cwd = await PickFolderAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(cwd)) return;

        try
        {
            var state = await _bridge.NewSessionAsync(
                SelectedBackend, cwd,
                model: null, reasoningEffort: null, permissionMode: null, approvalPolicy: null).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
            SelectedSession = Sessions.FirstOrDefault(s => s.ConversationId == state.ConversationId);
        }
        catch (Exception ex)
        {
            StatusText = "新建失败：" + ex.Message;
        }
    }

    private bool CanSend() => SelectedSession is not null && !IsRunning && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (SelectedSession is null) return;
        var convId = SelectedSession.ConversationId;
        var text = Input;
        Input = string.Empty;
        IsRunning = true;
        StatusText = "运行中…";
        try
        {
            // CancellationToken.None lets the bridge own interruption via its
            // linked cts + InterruptAsync. The bridge swallows the turn
            // cancellation and converges phase itself, so the VM never sees an
            // OperationCanceledException — interrupt simply lets this await
            // return, then the finally clears IsRunning.
            await _bridge.SendAsync(convId, text, CancellationToken.None).ConfigureAwait(true);
            StatusText = "完成";
        }
        catch (Exception ex)
        {
            StatusText = "失败：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task InterruptAsync()
    {
        if (SelectedSession is null) return;
        try { await _bridge.InterruptAsync(SelectedSession.ConversationId).ConfigureAwait(true); }
        catch { /* best-effort */ }
    }

    /// <summary>Short phase label for a row badge.</summary>
    public static string PhaseLabel(AgentSessionPhase p) => p switch
    {
        AgentSessionPhase.Idle => "空闲",
        AgentSessionPhase.Spawning => "启动中",
        AgentSessionPhase.Running => "运行中",
        AgentSessionPhase.Waiting => "待处理",
        AgentSessionPhase.Completed => "完成",
        AgentSessionPhase.Failed => "失败",
        _ => p.ToString()
    };

    /// <summary>Backend display name for a row badge.</summary>
    public static string BackendLabel(string id) => id == "codex" ? "Codex" : "Claude Code";
}
