using MolaGPT.Core.Runtime;

namespace MolaGPT.App.Infrastructure;

/// <summary>
/// Coordinates the desktop host's three power states (<see cref="AppPowerMode"/>):
/// <list type="bullet">
///   <item><c>Foreground</c>: any app window is visible and not minimized (OS auto QoS).</item>
///   <item><c>BackgroundBusy</c>: all windows hidden/minimized while active work or a
///   short interactive cooldown is in flight (explicit HighQoS to stop Windows 11 from
///   throttling background turns or remote control).</item>
///   <item><c>BackgroundIdle</c>: all windows hidden/minimized with zero leases and
///   expired cooldown after the settle delay (explicit EcoQoS).</item>
/// </list>
///
/// Lock hierarchy: <see cref="_nativeGate"/> (outer) → <see cref="_stateGate"/> (inner).
/// External callbacks, UI dispatchers and Win32 calls are never invoked under
/// <see cref="_stateGate"/>.
/// </summary>
internal sealed class AppPowerModeCoordinator : IActivityTracker, IDisposable
{
    public const string DisableEnvVar = "MOLAGPT_DISABLE_IDLE_QOS";
    private static readonly TimeSpan DefaultIdleConfirmDelay = TimeSpan.FromSeconds(8);

    private readonly object _stateGate = new();
    private readonly object _nativeGate = new();
    private readonly Action<string> _log;
    private readonly TimeSpan _idleConfirmDelay;
    private readonly Func<AppPowerMode, EfficiencyApplyResult> _applyNative;
    private readonly bool _disabledByEnv;

    private readonly Dictionary<string, int> _leaseCounts = new(StringComparer.Ordinal);
    private bool _hasVisibleWindow = true;
    private bool _isLogicalBackgroundIdle;
    private int _activeLeaseCount;
    private long _pulseUntilTickMs;
    private long _generation;
    private AppPowerMode _desiredMode = AppPowerMode.Foreground;
    private AppPowerMode _appliedMode = AppPowerMode.Foreground;
    private int _appliedModeVolatile = (int)AppPowerMode.Foreground;
    private int _nativeApplyInFlight;
    private bool _idleEntryLatchedOff;
    private bool _disposed;
    private Timer? _idleTimer;

    /// <summary>Raised off-lock when the process settles into <see cref="AppPowerMode.BackgroundIdle"/>.
    /// Carries the idle generation so subscribers can abort work if activity resumed.</summary>
    public event Action<long>? EnteredBackgroundIdle;

    public AppPowerModeCoordinator(
        Action<string>? log = null,
        TimeSpan? idleConfirmDelay = null,
        Func<AppPowerMode, EfficiencyApplyResult>? applyNative = null,
        bool? disabledOverride = null)
    {
        _log = log ?? (_ => { });
        _idleConfirmDelay = idleConfirmDelay ?? DefaultIdleConfirmDelay;
        _applyNative = applyNative ?? WindowsProcessEfficiency.Apply;
        _disabledByEnv = disabledOverride ?? IsDisabledByEnvironment();
        if (_disabledByEnv)
            _log($"idle QoS disabled via {DisableEnvVar}");
    }

    public AppPowerMode CurrentAppliedMode => (AppPowerMode)Volatile.Read(ref _appliedModeVolatile);

    public bool HasActiveWork
    {
        get
        {
            lock (_stateGate)
                return _activeLeaseCount > 0 || Environment.TickCount64 < _pulseUntilTickMs;
        }
    }

    public bool IsIdleAtGeneration(out long generation)
    {
        lock (_stateGate)
        {
            generation = _generation;
            return !_disposed
                && _isLogicalBackgroundIdle
                && !_hasVisibleWindow
                && _activeLeaseCount == 0
                && Environment.TickCount64 >= _pulseUntilTickMs;
        }
    }

    public bool MatchesIdleGeneration(long expectedGeneration)
    {
        lock (_stateGate)
        {
            return !_disposed
                && _isLogicalBackgroundIdle
                && _generation == expectedGeneration
                && !_hasVisibleWindow
                && _activeLeaseCount == 0
                && Environment.TickCount64 >= _pulseUntilTickMs;
        }
    }

    public IDisposable Acquire(string source)
    {
        var key = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
        bool needNativeSync;
        lock (_stateGate)
        {
            if (_disposed) return NullActivityTracker.Instance.Acquire(key);
            _isLogicalBackgroundIdle = false;
            _activeLeaseCount++;
            _leaseCounts[key] = _leaseCounts.GetValueOrDefault(key) + 1;
            _generation++;
            CancelTimerLocked();
            _desiredMode = _hasVisibleWindow ? AppPowerMode.Foreground : AppPowerMode.BackgroundBusy;
            needNativeSync = NeedsImmediateNativeSyncLocked(_desiredMode);
        }

        if (needNativeSync)
            ApplyDesiredNative("acquire:" + key);

        return new Lease(this, key);
    }

    public void Pulse(string source, TimeSpan hold)
    {
        if (hold <= TimeSpan.Zero) return;
        var key = string.IsNullOrWhiteSpace(source) ? "pulse" : source;
        var holdMs = Math.Max(1L, (long)hold.TotalMilliseconds);
        var deadline = Environment.TickCount64 + holdMs;
        bool needNativeSync;
        lock (_stateGate)
        {
            if (_disposed) return;
            _isLogicalBackgroundIdle = false;
            if (deadline > _pulseUntilTickMs) _pulseUntilTickMs = deadline;
            _generation++;
            CancelTimerLocked();
            _desiredMode = _hasVisibleWindow ? AppPowerMode.Foreground : AppPowerMode.BackgroundBusy;
            needNativeSync = NeedsImmediateNativeSyncLocked(_desiredMode);
            if (!_hasVisibleWindow && _activeLeaseCount == 0)
                ArmIdleTimerLocked(_generation, Math.Max(1L, _pulseUntilTickMs - Environment.TickCount64));
        }

        if (needNativeSync)
            ApplyDesiredNative("pulse:" + key);
    }

    /// <summary>Synchronously promotes QoS to <see cref="AppPowerMode.Foreground"/> when an
    /// explicit user wake occurs (tray restore, deep link, notification click, window activation).</summary>
    public void NotifyForegroundIntent(string reason)
    {
        bool needNativeSync;
        lock (_stateGate)
        {
            if (_disposed) return;
            _hasVisibleWindow = true;
            _isLogicalBackgroundIdle = false;
            _generation++;
            CancelTimerLocked();
            _desiredMode = AppPowerMode.Foreground;
            needNativeSync = NeedsImmediateNativeSyncLocked(_desiredMode);
        }

        if (needNativeSync)
            ApplyDesiredNative("foreground:" + reason);
    }

    /// <summary>Updates window visibility snapshot. Becoming visible exits idle immediately;
    /// becoming hidden schedules delayed entry to <see cref="AppPowerMode.BackgroundIdle"/>
    /// once leases and cooldowns drain.</summary>
    public void SetHasVisibleWindow(bool hasVisibleWindow, string reason)
    {
        bool needNativeSync;
        lock (_stateGate)
        {
            if (_disposed || _hasVisibleWindow == hasVisibleWindow) return;
            _hasVisibleWindow = hasVisibleWindow;
            _isLogicalBackgroundIdle = false;
            _generation++;
            CancelTimerLocked();
            if (hasVisibleWindow)
            {
                _desiredMode = AppPowerMode.Foreground;
                needNativeSync = NeedsImmediateNativeSyncLocked(_desiredMode);
            }
            else
            {
                // Always enter BackgroundBusy first (HighQoS) while hidden; only move to
                // BackgroundIdle after the settle delay with zero active work.
                _desiredMode = AppPowerMode.BackgroundBusy;
                needNativeSync = NeedsImmediateNativeSyncLocked(_desiredMode);
                if (_activeLeaseCount == 0)
                {
                    var remainingPulse = Math.Max(0L, _pulseUntilTickMs - Environment.TickCount64);
                    var delayMs = Math.Max((long)_idleConfirmDelay.TotalMilliseconds, remainingPulse);
                    ArmIdleTimerLocked(_generation, delayMs);
                }
            }
        }

        if (needNativeSync)
            ApplyDesiredNative(reason);
    }

    private void Release(string source)
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            if (_activeLeaseCount > 0) _activeLeaseCount--;
            if (_leaseCounts.TryGetValue(source, out var count))
            {
                if (count <= 1) _leaseCounts.Remove(source);
                else _leaseCounts[source] = count - 1;
            }
            _generation++;
            if (!_hasVisibleWindow && _activeLeaseCount == 0)
            {
                var remainingPulse = Math.Max(0L, _pulseUntilTickMs - Environment.TickCount64);
                var delayMs = Math.Max((long)_idleConfirmDelay.TotalMilliseconds, remainingPulse);
                ArmIdleTimerLocked(_generation, delayMs);
            }
        }
    }

    private bool NeedsImmediateNativeSyncLocked(AppPowerMode target)
    {
        if (_disabledByEnv) return false;
        return Volatile.Read(ref _appliedModeVolatile) != (int)target
            || Volatile.Read(ref _nativeApplyInFlight) != 0;
    }

    private void ArmIdleTimerLocked(long expectedGeneration, long delayMs)
    {
        CancelTimerLocked();
        var due = (int)Math.Clamp(delayMs, 1L, int.MaxValue - 1);
        _idleTimer = new Timer(
            static state =>
            {
                var (self, gen) = ((AppPowerModeCoordinator, long))state!;
                self.OnIdleTimerElapsed(gen);
            },
            (this, expectedGeneration),
            due,
            Timeout.Infinite);
    }

    private void CancelTimerLocked()
    {
        var timer = _idleTimer;
        _idleTimer = null;
        timer?.Dispose();
    }

    private void OnIdleTimerElapsed(long expectedGeneration)
    {
        var applyNativeIdle = false;
        lock (_stateGate)
        {
            if (_disposed || _generation != expectedGeneration) return;
            if (_hasVisibleWindow || _activeLeaseCount > 0) return;

            var remainingPulse = _pulseUntilTickMs - Environment.TickCount64;
            if (remainingPulse > 0)
            {
                ArmIdleTimerLocked(expectedGeneration, remainingPulse);
                return;
            }

            CancelTimerLocked();
            _isLogicalBackgroundIdle = true;
            if (!_idleEntryLatchedOff && !_disabledByEnv)
            {
                _desiredMode = AppPowerMode.BackgroundIdle;
                applyNativeIdle = true;
            }
        }

        if (applyNativeIdle)
            ApplyDesiredNative($"idle-settle(gen={expectedGeneration})");

        if (MatchesIdleGeneration(expectedGeneration) && EnteredBackgroundIdle is { } handler)
            _ = Task.Run(() => handler(expectedGeneration));
    }

    private void ApplyDesiredNative(string reason)
    {
        if (_disabledByEnv) return;

        lock (_nativeGate)
        {
            Volatile.Write(ref _nativeApplyInFlight, 1);
            try
            {
                // Re-read latest generation and desired state INSIDE nativeGate so a late
                // timer thread can never overwrite a newer Foreground/BackgroundBusy state.
                AppPowerMode target;
                long gen;
                bool latchedOff;
                bool visible;
                lock (_stateGate)
                {
                    if (_disposed) return;
                    gen = _generation;
                    target = _desiredMode;
                    latchedOff = _idleEntryLatchedOff;
                    visible = _hasVisibleWindow;
                }

                if (latchedOff && target == AppPowerMode.BackgroundIdle)
                    target = visible ? AppPowerMode.Foreground : AppPowerMode.BackgroundBusy;

                if (target == _appliedMode) return;

                var previous = _appliedMode;
                var result = _applyNative(target);
                if (result.EffectiveSuccess)
                {
                    SetAppliedModeUnderNativeGate(target);
                    var readback = result.ReadbackOk
                        ? $"({result.ActualControl},{result.ActualState})"
                        : $"unverified(err={result.ReadbackError})";
                    _log($"mode {previous} -> {target} gen={gen} reason={reason} set=({result.RequestedControl},{result.RequestedState}) get={readback}");
                }
                else if (result.Succeeded && result.ReadbackMismatch)
                {
                    if (WindowsProcessEfficiency.TryMapMode(result.ActualControl, result.ActualState, out var actualMode))
                        SetAppliedModeUnderNativeGate(actualMode);
                    _log($"mode MISMATCH {previous} -> {target} gen={gen} reason={reason} set=({result.RequestedControl},{result.RequestedState}) get=({result.ActualControl},{result.ActualState})");

                    LatchIdleOff();
                    // If we tried to exit EcoQoS to BackgroundBusy (1,0) and readback remained
                    // throttled, try one reset to Foreground (0,0) without looping.
                    if (target == AppPowerMode.BackgroundBusy && result.ActualState != 0)
                    {
                        var fallback = _applyNative(AppPowerMode.Foreground);
                        if (fallback.EffectiveSuccess)
                        {
                            SetAppliedModeUnderNativeGate(AppPowerMode.Foreground);
                            _log($"mode mismatch fallback -> Foreground gen={gen} get=({fallback.ActualControl},{fallback.ActualState})");
                        }
                        else if (fallback.ReadbackOk && WindowsProcessEfficiency.TryMapMode(fallback.ActualControl, fallback.ActualState, out var fbMode))
                        {
                            SetAppliedModeUnderNativeGate(fbMode);
                        }
                    }
                }
                else
                {
                    _log($"mode FAILED {previous} -> {target} gen={gen} reason={reason} err={result.Win32Error}");
                    LatchIdleOff();
                }
            }
            finally
            {
                Volatile.Write(ref _nativeApplyInFlight, 0);
            }
        }
    }

    private void SetAppliedModeUnderNativeGate(AppPowerMode mode)
    {
        _appliedMode = mode;
        Volatile.Write(ref _appliedModeVolatile, (int)mode);
    }

    private void LatchIdleOff()
    {
        bool newlyLatched;
        lock (_stateGate)
        {
            newlyLatched = !_idleEntryLatchedOff;
            _idleEntryLatchedOff = true;
            if (_desiredMode == AppPowerMode.BackgroundIdle)
                _desiredMode = _hasVisibleWindow ? AppPowerMode.Foreground : AppPowerMode.BackgroundBusy;
        }
        if (newlyLatched)
            _log("idle QoS latched OFF due to native apply/readback failure");
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _isLogicalBackgroundIdle = false;
            _generation++;
            CancelTimerLocked();
            _desiredMode = AppPowerMode.Foreground;
        }

        if (_disabledByEnv) return;

        // Always acquire _nativeGate: an in-flight ApplyDesiredNative may have checked
        // !_disposed just before Dispose ran, so only _nativeGate guarantees we observe
        // its final _appliedMode and reset back to Foreground.
        lock (_nativeGate)
        {
            if (_appliedMode == AppPowerMode.Foreground) return;
            var result = _applyNative(AppPowerMode.Foreground);
            if (result.EffectiveSuccess)
            {
                SetAppliedModeUnderNativeGate(AppPowerMode.Foreground);
            }
            else if (result.ReadbackOk && WindowsProcessEfficiency.TryMapMode(result.ActualControl, result.ActualState, out var actualMode))
            {
                SetAppliedModeUnderNativeGate(actualMode);
            }
            _log($"dispose reset -> Foreground ok={result.EffectiveSuccess} err={result.Win32Error}");
        }
    }

    private static bool IsDisabledByEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(DisableEnvVar)?.Trim();
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Lease(AppPowerModeCoordinator owner, string source) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(source);
        }
    }
}
