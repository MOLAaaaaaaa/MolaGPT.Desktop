using Avalonia;
using Avalonia.Controls;

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

}
