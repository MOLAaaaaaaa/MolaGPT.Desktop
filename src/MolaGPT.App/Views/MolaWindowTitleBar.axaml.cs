using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MolaGPT.App.Views;

public partial class MolaWindowTitleBar : UserControl
{
    public static readonly StyledProperty<string> TitleTextProperty =
        AvaloniaProperty.Register<MolaWindowTitleBar, string>(nameof(TitleText), string.Empty);

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<MolaWindowTitleBar, bool>(nameof(ShowCloseButton), true);

    public MolaWindowTitleBar()
    {
        InitializeComponent();
        PART_DragArea.PointerPressed += OnPointerPressed;
        PART_Close.Click += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.Close();
    }

    public string TitleText
    {
        get => GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public bool ShowCloseButton
    {
        get => GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2 && window.CanResize)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        window.BeginMoveDrag(e);
    }
}
