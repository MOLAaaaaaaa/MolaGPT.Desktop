using Avalonia.Controls;
using Avalonia.Input;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class TrayClosePromptWindow : MolaWindow
{
    public TrayClosePromptWindow()
    {
        InitializeComponent();
        PART_Header.PointerPressed += OnHeaderPointerPressed;
        PART_Cancel.Click += (_, _) => Close(null);
        PART_Minimize.Click += (_, _) => Close(TrayCloseBehavior.MinimizeToTray);
        PART_Exit.Click += (_, _) => Close(TrayCloseBehavior.Exit);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
