using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace MolaGPT.App.Rendering.Canvas;

/// <summary>
/// A WebView2 on a plain child window that Avalonia positions.
///
/// Driven through <see cref="CoreWebView2Controller"/> directly: the WinForms
/// wrapper this replaced pulled the whole WinForms runtime into the app for
/// one control, and its global usings collided with Avalonia's. Here the only
/// dependency is the Core API; our window class forwards WM_SIZE so the
/// controller always fills whatever rectangle Avalonia gave the host.
///
/// Like every native child, it draws above Avalonia content in its bounds —
/// nothing Avalonia renders can sit on top of it. Callers show status text by
/// hiding this control, never by layering over it.
/// </summary>
internal sealed class CanvasWebView : NativeControlHost
{
    // Far enough left to be outside any window, near enough to stay inside the
    // 16-bit coordinates Win32 still packs into WM_MOVE at 250% scaling.
    private static readonly Thickness Offstage = new(-8000, 0, 8000, 0);

    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private TaskCompletionSource<CoreWebView2> _ready = NewReady();
    private System.Drawing.Color _background = System.Drawing.Color.White;

    /// <summary>Completes with the CoreWebView2 once the controller exists, or
    /// faults when the runtime is missing or broken.</summary>
    public Task<CoreWebView2> Ready => _ready.Task;

    public CoreWebView2? Core => _controller?.CoreWebView2;

    /// <summary>Laid out at full size but outside the window, while a page loads.</summary>
    public bool IsParked => Margin == Offstage;

    /// <summary>
    /// Loads offstage instead of hidden. A hidden native control is parked at
    /// 1×1 px, and a page that sizes itself on load (a chart, a canvas sized to
    /// the window) would lay itself out for that and stay tiny.
    /// </summary>
    public void Park()
    {
        Margin = Offstage;
        IsVisible = true;
    }

    public void Unpark() => Margin = default;

    public void SetBackground(Avalonia.Media.Color color)
    {
        _background = System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);
        CanvasWindowClass.SetBackground(_hwnd, _background);
        if (_controller is not null) _controller.DefaultBackgroundColor = _background;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (_ready.Task.IsCompleted) _ready = NewReady();
        _hwnd = CanvasWindowClass.Create(parent.Handle, OnResized);
        CanvasWindowClass.SetBackground(_hwnd, _background);
        _ = InitializeAsync(_hwnd);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            _controller?.Close();
        }
        catch (COMException)
        {
        }

        _controller = null;
        CanvasWindowClass.Destroy(_hwnd);
        _hwnd = IntPtr.Zero;
        _ready.TrySetException(new ObjectDisposedException(nameof(CanvasWebView)));
    }

    private async Task InitializeAsync(IntPtr hwnd)
    {
        try
        {
            var environment = await CanvasEnvironment.GetAsync();
            var controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            if (hwnd != _hwnd)
            {
                // Destroyed while the controller was being created.
                controller.Close();
                return;
            }

            _controller = controller;
            controller.DefaultBackgroundColor = _background;
            FitToWindow();
            controller.IsVisible = true;
            _ready.TrySetResult(controller.CoreWebView2);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    private void OnResized(int width, int height)
    {
        if (_controller is null) return;
        try
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        }
        catch (COMException)
        {
        }
    }

    private void FitToWindow()
    {
        if (_controller is null || _hwnd == IntPtr.Zero) return;
        if (CanvasWindowClass.GetClientSize(_hwnd) is { } size)
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, size.Width, size.Height);
    }

    private static TaskCompletionSource<CoreWebView2> NewReady() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The window class behind <see cref="CanvasWebView"/>: tells the controller
/// when Avalonia resizes it, and paints the theme background wherever the
/// browser has not drawn yet.
///
/// The painting is not cosmetic, and it has to be synchronous. Avalonia puts
/// every native child in a layered holder window, and a layered window nothing
/// has painted is not black but absent: the desktop behind the app shows
/// through. An ordinary WM_PAINT does not help — it is the lowest-priority
/// message, and while the panel animates open the UI thread never gets to it.
/// So the holder is watched, and the moment Avalonia shows, moves or resizes
/// it, this window paints itself before returning.
/// </summary>
internal static class CanvasWindowClass
{
    private const string ClassName = "MolaCanvasHost";
    private const int GWLP_WNDPROC = -4;
    private const int WM_SIZE = 0x0005;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_NCDESTROY = 0x0082;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_NOCHILDREN = 0x0040;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ERASENOW = 0x0200;
    private const uint CS_VREDRAW = 0x0001;
    private const uint CS_HREDRAW = 0x0002;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Kept alive for the process: the OS holds raw pointers to them.
    private static readonly WndProc Procedure = OnMessage;
    private static readonly WndProc HolderProcedure = OnHolderMessage;
    private static readonly Dictionary<IntPtr, Action<int, int>> SizeHandlers = new();
    private static readonly Dictionary<IntPtr, uint> Backgrounds = new();
    private static readonly Dictionary<IntPtr, (IntPtr Previous, IntPtr Child)> Holders = new();
    private static bool _registered;

    public static IntPtr Create(IntPtr parent, Action<int, int> onSize)
    {
        EnsureRegistered();
        // Deliberately without WS_CLIPCHILDREN: the browser's own child window
        // covers all of this one, and when it has no frame to show (starting
        // up, or just back from offstage) what shows through it is whatever
        // this window painted underneath. The browser draws through
        // DirectComposition above that, so filling under it never flickers.
        var hwnd = CreateWindowExW(0, ClassName, string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 1, 1, parent, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("无法创建画布窗口");
        SizeHandlers[hwnd] = onSize;
        WatchHolder(parent, hwnd);
        return hwnd;
    }

    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SizeHandlers.Remove(hwnd);
        Backgrounds.Remove(hwnd);
        DestroyWindow(hwnd);
    }

    public static void SetBackground(IntPtr hwnd, System.Drawing.Color color)
    {
        if (hwnd == IntPtr.Zero) return;
        Backgrounds[hwnd] = (uint)(color.R | (color.G << 8) | (color.B << 16));
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    public static (int Width, int Height)? GetClientSize(IntPtr hwnd) =>
        GetClientRect(hwnd, out var rect) ? (rect.Right - rect.Left, rect.Bottom - rect.Top) : null;

    private static void EnsureRegistered()
    {
        if (_registered) return;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            // Repaint all of it on resize: the part the browser has not
            // caught up with yet is ours to fill.
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* already registered */)
            throw new InvalidOperationException("无法注册画布窗口类");
        _registered = true;
    }

    private static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_WINDOWPOSCHANGED)
        {
            // Avalonia sizes this window just before it shows the holder, and
            // may move it into a new holder when the control is re-hosted.
            WatchHolder(GetParent(hwnd), hwnd);
        }
        else if (msg == WM_SIZE && SizeHandlers.TryGetValue(hwnd, out var handler))
        {
            var packed = lParam.ToInt64();
            handler((int)(packed & 0xFFFF), (int)((packed >> 16) & 0xFFFF));
        }
        else if (msg == WM_ERASEBKGND && Backgrounds.TryGetValue(hwnd, out var color) && GetClientRect(hwnd, out var rect))
        {
            var brush = CreateSolidBrush(color);
            FillRect(wParam, ref rect, brush);
            DeleteObject(brush);
            return 1;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void WatchHolder(IntPtr holder, IntPtr child)
    {
        if (holder == IntPtr.Zero || Holders.ContainsKey(holder)) return;
        var previous = SetWindowProc(holder, Marshal.GetFunctionPointerForDelegate(HolderProcedure));
        if (previous == IntPtr.Zero) return;
        Holders[holder] = (previous, child);
    }

    private static IntPtr OnHolderMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Holders.TryGetValue(hwnd, out var entry)) return DefWindowProcW(hwnd, msg, wParam, lParam);

        var result = CallWindowProcW(entry.Previous, hwnd, msg, wParam, lParam);
        if (msg == WM_WINDOWPOSCHANGED)
        {
            var flags = Marshal.PtrToStructure<WINDOWPOS>(lParam).flags;
            var appeared = (flags & SWP_SHOWWINDOW) != 0;
            var moved = (flags & SWP_NOMOVE) == 0;
            var resized = (flags & SWP_NOSIZE) == 0;
            if ((appeared || moved || resized) && Backgrounds.ContainsKey(entry.Child))
                RedrawWindow(entry.Child, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ERASENOW | RDW_UPDATENOW | RDW_NOCHILDREN);
        }
        else if (msg == WM_NCDESTROY)
        {
            Holders.Remove(hwnd);
            SetWindowProc(hwnd, entry.Previous);
        }

        return result;
    }

    private static IntPtr SetWindowProc(IntPtr hwnd, IntPtr proc) =>
        IntPtr.Size == 8 ? SetWindowLongPtrW(hwnd, GWLP_WNDPROC, proc) : SetWindowLongW(hwnd, GWLP_WNDPROC, proc);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLongW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}

/// <summary>
/// One browser environment for the app, with its profile under our own data
/// directory. The default would be next to the executable — inside the
/// install folder, outside anything uninstall cleans up.
/// </summary>
internal static class CanvasEnvironment
{
    private static Task<CoreWebView2Environment>? _environment;

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT", "canvas");

    /// <summary>
    /// Starts the browser ahead of the first page, from the pointer reaching
    /// 「在画布打开」: the few hundred milliseconds between hover and click are
    /// most of what the browser process takes to come up. Not at startup — a
    /// user who never opens a page should not carry a browser process.
    /// </summary>
    public static void Prewarm() => _ = GetAsync();

    public static Task<CoreWebView2Environment> GetAsync()
    {
        // A failed start (runtime missing) is retried on the next request, so
        // installing WebView2 does not need an app restart.
        if (_environment is null || _environment.IsFaulted || _environment.IsCanceled)
            _environment = CreateAsync();
        return _environment;
    }

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        // WebRTC can reveal the local address to any page regardless of CSP.
        var options = new CoreWebView2EnvironmentOptions("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        return CoreWebView2Environment.CreateAsync(null, Path.Combine(Root, "webview2"), options);
    }
}
