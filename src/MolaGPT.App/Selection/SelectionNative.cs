using System.Runtime.InteropServices;

namespace MolaGPT.App.Selection;

internal static class SelectionNative
{
    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    internal delegate void WinEventProc(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public Point Pt; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Pt; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    // INPUT is 40 bytes on the supported win-x64 target; the union begins at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 40)] internal struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message msg);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point pt);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetCapture();
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] internal static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32.dll")] internal static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint mem);
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();

    internal static bool IsOwn(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    internal static bool IsFullscreen(nint hwnd)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetWindowRect(hwnd, out var r)
            && GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)
            && r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top
            && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }
}
