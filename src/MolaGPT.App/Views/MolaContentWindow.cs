using Avalonia;
using Avalonia.Controls;

namespace MolaGPT.App.Views;

public class MolaContentWindow : MolaWindow
{
    public static readonly StyledProperty<Control?> HeaderProperty =
        AvaloniaProperty.Register<MolaContentWindow, Control?>(nameof(Header));

    public static readonly StyledProperty<string?> HeaderTitleProperty =
        AvaloniaProperty.Register<MolaContentWindow, string?>(nameof(HeaderTitle));

    public MolaContentWindow()
    {
        Header = new MolaWindowTitleBar();
    }

    protected override Type StyleKeyOverride => typeof(MolaContentWindow);

    public Control? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? HeaderTitle
    {
        get => GetValue(HeaderTitleProperty);
        set => SetValue(HeaderTitleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if ((change.Property == TitleProperty
             || change.Property == HeaderProperty
             || change.Property == HeaderTitleProperty)
            && Header is MolaWindowTitleBar titleBar)
        {
            titleBar.TitleText = string.IsNullOrEmpty(HeaderTitle) ? Title ?? string.Empty : HeaderTitle;
        }
    }
}
