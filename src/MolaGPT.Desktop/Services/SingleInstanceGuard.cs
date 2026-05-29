using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Diagnostics;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// Cross-process single-instance lock backed by a named Mutex plus a
/// broadcast Win32 message. The first process owns the Mutex and
/// listens for our wake-up message; any later launch broadcasts the
/// message (which only the owning process recognizes; registered
/// messages are unique per session) and exits before initializing the
/// app host.
///
/// Why a broadcast message rather than a named pipe? The pipe approach
/// works but needs a server thread, async plumbing, and graceful
/// shutdown handling. A registered window message is one Win32 call to
/// send and a HwndSource hook to receive; simpler, and good enough
/// for "bring the existing window forward". For OAuth deep-link
/// payloads we layer WM_COPYDATA on top so the second instance can
/// hand its argv (the molagpt://... URL) to the first.
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexName = "Global\\MolaGPT.Desktop.SingleInstance.v1";
    private const string MessageName = "MolaGPT.Desktop.WakeUp.v1";
    private const int CopyDataPayloadId = 0x4D4F4C41; // 'MOLA'

    private static Mutex? _mutex;
    private static int _wakeUpMessage;

    /// <summary>
    /// Raised on the UI thread when another launch hands us a URL via
    /// the OAuth deep link (molagpt://oauth_callback?code=...). The host
    /// subscribes to feed the token into MolaGptAuthService.
    /// </summary>
    public static event Action<string>? DeepLinkReceived;

    /// <summary>
    /// Tries to acquire the single-instance lock. Returns true when the
    /// caller should proceed with normal startup; returns false when
    /// another instance was already running (the existing instance was
    /// poked, and any deep-link argv was forwarded).
    /// </summary>
    public static bool TryAcquire(string[] argv)
    {
        _wakeUpMessage = NativeMethods.RegisterWindowMessage(MessageName);
        DiagnosticLog.Write("SingleInstance", $"TryAcquire argv.len={argv.Length} wakeMsg={_wakeUpMessage}");

        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            _mutex = mutex;
            DiagnosticLog.Write("SingleInstance", "acquired (first instance)");
            return true;
        }

        try
        {
            DiagnosticLog.Write("SingleInstance", "second instance; forwarding to first");
            ForwardToFirstInstance(argv);
        }
        finally
        {
            mutex.Dispose();
        }
        return false;
    }

    private static void ForwardToFirstInstance(string[] argv)
    {
        if (_wakeUpMessage == 0) return;

        var candidateHwnds = FindMolaGptWindows();
        DiagnosticLog.Write("SingleInstance", $"FindMolaGptWindows count={candidateHwnds.Count}");

        if (candidateHwnds.Count > 0)
        {
            var deepLink = ExtractDeepLink(argv);
            DiagnosticLog.Write("SingleInstance",
                $"deepLink={(string.IsNullOrEmpty(deepLink) ? "<none>" : "molagpt://...len=" + deepLink.Length)}");
            if (!string.IsNullOrEmpty(deepLink))
            {
                foreach (var hwnd in candidateHwnds)
                {
                    var rc = SendCopyData(hwnd, deepLink);
                    DiagnosticLog.Write("SingleInstance", $"SendMessage hwnd=0x{hwnd.ToInt64():X} rc={rc.ToInt64()} lastErr={Marshal.GetLastWin32Error()}");
                    if (rc != IntPtr.Zero) break;
                }
            }
        }

        // Always poke the wake-up message so the existing window comes
        // to the foreground regardless of whether we sent a deep link.
        NativeMethods.PostMessage(
            NativeMethods.HWND_BROADCAST,
            _wakeUpMessage,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    /// <summary>
    /// Picks the first molagpt:// URL out of argv. Windows passes the
    /// URL as a single positional argument when the protocol is invoked.
    /// </summary>
    public static string? ExtractDeepLink(string[] argv)
    {
        foreach (var arg in argv)
        {
            if (!string.IsNullOrEmpty(arg) &&
                arg.StartsWith("molagpt://", StringComparison.OrdinalIgnoreCase))
                return arg;
        }
        return null;
    }

    private static List<IntPtr> FindMolaGptWindows()
    {
        var found = new List<IntPtr>();
        var currentPid = Environment.ProcessId;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == currentPid) return true;
            if (!IsMolaGptDesktopProcess(pid)) return true;

            // Dialogs can be enumerated before the main window while OAuth
            // is open. Send WM_COPYDATA to every visible app window; only
            // the main window handles and acknowledges the payload.
            if (NativeMethods.IsWindowVisible(hWnd))
                found.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsMolaGptDesktopProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            using var currentProcess = Process.GetCurrentProcess();
            return string.Equals(process.ProcessName, currentProcess.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr SendCopyData(IntPtr hwnd, string payload)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(payload);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var data = new NativeMethods.COPYDATASTRUCT
            {
                dwData = new IntPtr(CopyDataPayloadId),
                cbData = bytes.Length,
                lpData = ptr
            };
            return NativeMethods.SendMessage(hwnd, NativeMethods.WM_COPYDATA, IntPtr.Zero, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Hooks the wake-up message and WM_COPYDATA on the given window so
    /// that subsequent launches restore + activate it and forward
    /// OAuth deep links.
    /// </summary>
    public static void AttachActivator(Window window)
    {
        void Hook(object? _, EventArgs __)
        {
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle;
            DiagnosticLog.Write("SingleInstance", $"AttachActivator hwnd=0x{hwnd.ToInt64():X}");

            try
            {
                var ok1 = NativeMethods.ChangeWindowMessageFilterEx(
                    hwnd, NativeMethods.WM_COPYDATA, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);
                var ok2 = false;
                if (_wakeUpMessage != 0)
                {
                    ok2 = NativeMethods.ChangeWindowMessageFilterEx(
                        hwnd, _wakeUpMessage, NativeMethods.MSGFLT_ALLOW, IntPtr.Zero);
                }
                DiagnosticLog.Write("SingleInstance", $"UIPI filter copyData={ok1} wakeUp={ok2}");
            }
            catch (EntryPointNotFoundException)
            {
                DiagnosticLog.Write("SingleInstance", "ChangeWindowMessageFilterEx not available");
            }

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook((IntPtr handle, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (_wakeUpMessage != 0 && msg == _wakeUpMessage)
                {
                    DiagnosticLog.Write("SingleInstance", "wakeUp received");
                    BringToFront(window);
                    handled = true;
                    return IntPtr.Zero;
                }

                if (msg == NativeMethods.WM_COPYDATA)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
                    DiagnosticLog.Write("SingleInstance",
                        $"WM_COPYDATA dwData=0x{data.dwData.ToInt32():X} cbData={data.cbData}");
                    if (data.dwData.ToInt32() == CopyDataPayloadId && data.cbData > 0)
                    {
                        var payload = Marshal.PtrToStringUni(data.lpData, data.cbData / 2);
                        DiagnosticLog.Write("SingleInstance",
                            $"payload={(string.IsNullOrEmpty(payload) ? "<empty>" : "len=" + payload.Length)} subscribers={DeepLinkReceived?.GetInvocationList().Length ?? 0}");
                        if (!string.IsNullOrEmpty(payload))
                        {
                            BringToFront(window);
                            DeepLinkReceived?.Invoke(payload);
                            handled = true;
                            return new IntPtr(1);
                        }
                    }
                }
                return IntPtr.Zero;
            });
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Hook(null, EventArgs.Empty);
        else
            window.SourceInitialized += Hook;
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (!window.IsVisible)
            window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex was never owned by this thread; nothing to release.
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        public const int WM_COPYDATA = 0x004A;
        public const uint MSGFLT_ALLOW = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeWindowMessageFilterEx(
            IntPtr hWnd, int message, uint action, IntPtr pChangeFilterStruct);
    }
}
