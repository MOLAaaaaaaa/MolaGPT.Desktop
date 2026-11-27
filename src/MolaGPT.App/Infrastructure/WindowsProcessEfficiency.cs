using System.Runtime.InteropServices;

namespace MolaGPT.App.Infrastructure;

internal enum AppPowerMode
{
    Foreground = 0,
    BackgroundBusy = 1,
    BackgroundIdle = 2
}

internal readonly record struct EfficiencyApplyResult(
    bool Succeeded,
    int Win32Error,
    uint RequestedControl,
    uint RequestedState,
    bool ReadbackOk,
    uint ActualControl,
    uint ActualState,
    int ReadbackError)
{
    public bool ReadbackMismatch =>
        ReadbackOk && (ActualControl != RequestedControl || ActualState != RequestedState);

    public bool EffectiveSuccess => Succeeded && !ReadbackMismatch;
}

/// <summary>
/// Thin Win32 wrapper around <c>SetProcessInformation</c> / <c>GetProcessInformation</c>
/// for <c>ProcessPowerThrottling</c>. Phase 2 manages <c>EXECUTION_SPEED (0x1)</c>
/// exclusively on the current process and never changes process priority class.
/// </summary>
internal static class WindowsProcessEfficiency
{
    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1;
    internal const uint ExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public static EfficiencyApplyResult Apply(AppPowerMode mode)
    {
        var (control, state) = mode switch
        {
            AppPowerMode.BackgroundIdle => (ExecutionSpeed, ExecutionSpeed),
            AppPowerMode.BackgroundBusy => (ExecutionSpeed, 0u),
            _ => (0u, 0u)
        };

        if (!OperatingSystem.IsWindows())
            return new EfficiencyApplyResult(true, 0, control, state, false, 0, 0, 0);

        var requested = new ProcessPowerThrottlingState
        {
            Version = CurrentVersion,
            ControlMask = control,
            StateMask = state
        };

        var size = Marshal.SizeOf<ProcessPowerThrottlingState>();
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref requested, size))
        {
            var err = Marshal.GetLastWin32Error();
            return new EfficiencyApplyResult(false, err, control, state, false, 0, 0, 0);
        }

        var readbackOk = TryQuery(out var actual, out var queryErr);
        return new EfficiencyApplyResult(
            Succeeded: true,
            Win32Error: 0,
            RequestedControl: control,
            RequestedState: state,
            ReadbackOk: readbackOk,
            ActualControl: actual.ControlMask,
            ActualState: actual.StateMask,
            ReadbackError: queryErr);
    }

    public static bool TryQuery(out ProcessPowerThrottlingState state, out int win32Error)
    {
        state = new ProcessPowerThrottlingState { Version = CurrentVersion };
        if (!OperatingSystem.IsWindows())
        {
            win32Error = 0;
            return false;
        }

        var size = Marshal.SizeOf<ProcessPowerThrottlingState>();
        if (GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    public static bool TryMapMode(uint controlMask, uint stateMask, out AppPowerMode mode)
    {
        if (controlMask == 0 && stateMask == 0)
        {
            mode = AppPowerMode.Foreground;
            return true;
        }
        if (controlMask == ExecutionSpeed && stateMask == 0)
        {
            mode = AppPowerMode.BackgroundBusy;
            return true;
        }
        if (controlMask == ExecutionSpeed && stateMask == ExecutionSpeed)
        {
            mode = AppPowerMode.BackgroundIdle;
            return true;
        }
        mode = default;
        return false;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);
}
