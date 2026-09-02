using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Runtime.InteropServices;

namespace MolaGPT.App.Views;

/// <summary>
/// Shared native shell for every MolaGPT window.
/// </summary>
public class MolaWindow : Window
{
    private const double HighDensityRenderScaling = 2d;
    private const uint WmNcCalcSize = 0x0083;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;

    /// <summary>
    /// Raised whenever any MolaGPT window comes to the front.
    ///
    /// Coming back to the app does not necessarily activate the main window —
    /// Windows restores whichever window was last in front, which is often
    /// Settings. Anything that needs to know "the user is back" has to watch
    /// all of them, and every window in the app derives from this type.
    /// </summary>
    public static event EventHandler? AnyWindowActivated;

    public MolaWindow()
    {
        Classes.Add("molawindow");
        Activated += (_, _) => AnyWindowActivated?.Invoke(this, EventArgs.Empty);
        ApplyTextRasterization(RenderScaling);
        ScalingChanged += HandleScalingChanged;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        TransparencyLevelHint = [WindowTransparencyLevel.None];

        Win32Properties.AddWindowStylesCallback(this, PreserveNativeTransitionStyles);
        Win32Properties.AddWndProcHookCallback(this, HandleWindowMessage);
        Win32Properties.SetWindowCornerPreference(this, Win32Properties.WindowCornerPreference.Round);
    }

    // Text rasterization adapts to pixel density, because "smooth" and "sharp"
    // want opposite things depending on how many pixels a glyph gets:
    //   * Standard density (<2x, e.g. a 1080p/1440p panel at 100%): subpixel
    //     (ClearType) + hinting. There aren't enough pixels for grayscale outlines
    //     to look crisp, so we spend the LCD's RGB stripes on horizontal resolution
    //     and grid-fit the stems. This is the "not blurry" path.
    //   * High density (>=2x, a HiDPI/Retina-class panel): grayscale + no hinting +
    //     unaligned baselines. There the pixels are plentiful, so we render the true
    //     outline for the smooth, unmechanical macOS look without any fuzz.
    internal static (TextRenderingMode Rendering, TextHintingMode Hinting, BaselinePixelAlignment Baseline)
        SelectTextRasterization(double renderScaling)
        => renderScaling >= HighDensityRenderScaling
            ? (TextRenderingMode.Antialias, TextHintingMode.None, BaselinePixelAlignment.Unaligned)
            : (TextRenderingMode.SubpixelAntialias, TextHintingMode.Light, BaselinePixelAlignment.Aligned);

    private void HandleScalingChanged(object? sender, EventArgs e)
        => ApplyTextRasterization(RenderScaling);

    private void ApplyTextRasterization(double renderScaling)
    {
        var options = SelectTextRasterization(renderScaling);
        TextOptions.SetTextRenderingMode(this, options.Rendering);
        TextOptions.SetTextHintingMode(this, options.Hinting);
        TextOptions.SetBaselinePixelAlignment(this, options.Baseline);
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
