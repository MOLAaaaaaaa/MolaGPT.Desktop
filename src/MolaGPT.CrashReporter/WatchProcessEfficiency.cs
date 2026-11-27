using System.Runtime.InteropServices;

namespace MolaGPT.CrashReporter;

/// <summary>
/// Places the passive <c>--watch</c> crash-monitor process into Windows Efficiency Mode
/// (<c>IDLE_PRIORITY_CLASS</c> + <c>ProcessPowerThrottling</c> <c>EXECUTION_SPEED</c>)
/// while waiting for the watched app to exit or request a minidump, and restores normal
/// priority and OS-managed QoS before minidump capture, report archiving, or crash UI display.
/// </summary>
internal sealed class WatchEfficiencyScope : IDisposable
{
    private const string DisableEnvVar = "MOLAGPT_DISABLE_IDLE_QOS";
    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const uint NormalPriorityClass = 0x00000020;
    private const uint IdlePriorityClass = 0x00000040;
    private const uint BelowNormalPriorityClass = 0x00004000;

    private readonly object _gate = new();
    private uint _restorePriority = NormalPriorityClass;
    private bool _entered;
    private bool _restored;
    private bool _qosApplied;
    private bool _priorityApplied;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public void Enter()
    {
        if (!OperatingSystem.IsWindows() || IsDisabledByEnvironment()) return;

        lock (_gate)
        {
            if (_restored || _entered) return;

            try
            {
                var current = GetCurrentProcess();
                var prior = GetPriorityClass(current);
                _restorePriority = prior is 0 or IdlePriorityClass or BelowNormalPriorityClass
                    ? NormalPriorityClass
                    : prior;

                var state = new ProcessPowerThrottlingState
                {
                    Version = CurrentVersion,
                    ControlMask = ExecutionSpeed,
                    StateMask = ExecutionSpeed
                };
                _qosApplied = SetProcessInformation(current, ProcessPowerThrottling, ref state,
                    Marshal.SizeOf<ProcessPowerThrottlingState>());
                _priorityApplied = SetPriorityClass(current, IdlePriorityClass);
                _entered = _qosApplied || _priorityApplied;
            }
            catch
            {
                // Best-effort only: crash watching must never fail due to QoS/priority APIs.
            }
        }
    }

    public void Restore()
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (_gate)
        {
            if (_restored) return;
            _restored = true;
            if (!_entered) return;

            try
            {
                var current = GetCurrentProcess();
                if (_priorityApplied)
                    SetPriorityClass(current, _restorePriority);

                if (_qosApplied)
                {
                    var reset = new ProcessPowerThrottlingState
                    {
                        Version = CurrentVersion,
                        ControlMask = 0,
                        StateMask = 0
                    };
                    SetProcessInformation(current, ProcessPowerThrottling, ref reset,
                        Marshal.SizeOf<ProcessPowerThrottlingState>());
                }
            }
            catch
            {
                // Best-effort restore.
            }
        }
    }

    public void Dispose() => Restore();

    private static bool IsDisabledByEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(DisableEnvVar)?.Trim();
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);
}
