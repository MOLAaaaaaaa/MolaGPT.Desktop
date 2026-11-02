using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using static MolaGPT.App.Selection.SelectionNative;

namespace MolaGPT.App.Selection;

public partial class SelectionToolbarWindow : Window
{
    private Button? _pressedButton;
    internal event Action<string>? ActionRequested;
    public SelectionToolbarWindow()
    {
        InitializeComponent();
        Win32Properties.AddWindowStylesCallback(this, (style, ex) => (style, ex | 0x08000000 | 0x80));
        Win32Properties.AddWndProcHookCallback(this, OnNativeMessage);
        Win32Properties.SetWindowCornerPreference(this, Win32Properties.WindowCornerPreference.Round);
    }

    private nint OnNativeMessage(nint hwnd, uint msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x21)
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        // Avalonia's Win32 mouse-down handler calls SetFocus even after MA_NOACTIVATE.
        // Handle this small toolbar's button gesture here so the source keeps its focus.
        if (msg is 0x0201 or 0x0203)
        {
            ClearPressed();
            _pressedButton = HitButton(lp);
            if (_pressedButton is not null)
            {
                _pressedButton.Classes.Add("pressed");
                SetCapture(hwnd);
            }
            handled = true;
        }
        else if (msg == 0x0202)
        {
            var button = _pressedButton;
            ClearPressed();
            if (GetCapture() == hwnd) ReleaseCapture();
            handled = true;
            if (button is not null && button == HitButton(lp))
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else if (msg is 0x001F or 0x0215) ClearPressed(); // Cancel mode / capture lost.
        return 0;
    }

    private Button? HitButton(nint coordinates)
    {
        var point = new Avalonia.Point((short)(long)coordinates / RenderScaling,
            (short)((long)coordinates >> 16) / RenderScaling);
        var button = this.InputHitTest(point) is Visual hit ? hit as Button ?? hit.FindAncestorOfType<Button>() : null;
        return button is { IsEffectivelyEnabled: true } ? button : null;
    }

    private void ClearPressed()
    {
        _pressedButton?.Classes.Remove("pressed");
        _pressedButton = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || IsVisible) return;
        ClearPressed();
        if (TryGetPlatformHandle() is { } handle && GetCapture() == handle.Handle) ReleaseCapture();
    }
    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action }) ActionRequested?.Invoke(action);
    }
}
