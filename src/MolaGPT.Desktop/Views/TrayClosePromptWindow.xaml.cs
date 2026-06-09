using System.Windows;
using System.Windows.Input;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class TrayClosePromptWindow : Window
{
    private TrayCloseBehavior? _choice;

    public TrayClosePromptWindow()
    {
        InitializeComponent();
    }

    public static TrayCloseBehavior? ShowFor(Window? owner)
    {
        var dialog = new TrayClosePromptWindow();
        if (owner is not null)
            dialog.Owner = owner;

        return dialog.ShowDialog() == true ? dialog._choice : null;
    }

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e)
    {
        _choice = TrayCloseBehavior.MinimizeToTray;
        DialogResult = true;
    }

    private void ExitClick(object sender, RoutedEventArgs e)
    {
        _choice = TrayCloseBehavior.Exit;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
