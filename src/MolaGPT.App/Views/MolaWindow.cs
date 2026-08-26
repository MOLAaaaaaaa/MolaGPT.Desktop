using Avalonia;
using Avalonia.Controls;
using System.Runtime.InteropServices;

namespace MolaGPT.App.Views;

/// <summary>
/// Shared native shell for every MolaGPT window.
/// </summary>
public class MolaWindow : Window
{
    private const uint WmNcCalcSize = 0x0083;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;

    public MolaWindow()
    {
        Classes.Add("molawindow");
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        TransparencyLevelHint = [WindowTransparencyLevel.None];

        Win32Properties.AddWindowStylesCallback(this, PreserveNativeTransitionStyles);
        Win32Properties.AddWndProcHookCallback(this, HandleWindowMessage);
        Win32Properties.SetWindowCornerPreference(this, Win32Properties.WindowCornerPreference.Round);
    }

    private static (uint style, uint exStyle) PreserveNativeTransitionStyles(uint style, uint exStyle)
        => (style | WsCaption | WsSysMenu, exStyle);

    private IntPtr HandleWindowMessage(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcCalcSize || wParam == IntPtr.Zero)
            return IntPtr.Zero;

        if (IsZoomed(hwnd))
        {
            var dpi = GetDpiForWindow(hwnd);
            var frameX = GetSystemMetricsForDpi(SmCxSizeFrame, dpi)
                         + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
            var frameY = GetSystemMetricsForDpi(SmCySizeFrame, dpi)
                         + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
            var parameters = Marshal.PtrToStructure<NcCalcSizeParams>(lParam);
            parameters.NewClientRect.Left += frameX;
            parameters.NewClientRect.Top += frameY;
            parameters.NewClientRect.Right -= frameX;
            parameters.NewClientRect.Bottom -= frameY;
            Marshal.StructureToPtr(parameters, lParam, false);
        }

        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcCalcSizeParams
    {
        public NativeRect NewClientRect;
        public NativeRect OldWindowRect;
        public NativeRect OldClientRect;
        public IntPtr WindowPosition;
    }
}
