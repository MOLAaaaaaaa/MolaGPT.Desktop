using Avalonia.Controls;
using Avalonia.Input;

namespace MolaGPT.App.Views;

public partial class BridgePrivacyWindow : MolaWindow
{
    public BridgePrivacyWindow()
    {
        InitializeComponent();
        PART_Cancel.Click += (_, _) => Close(false);
        PART_Confirm.Click += (_, _) => Close(true);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
