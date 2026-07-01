using System.Windows;
using System.Windows.Input;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Privacy disclosure shown when the user turns on the cloud relay bridge.
/// <see cref="Window.ShowDialog"/> returns true only when the user explicitly
/// consents ("我已知晓，启用").
/// </summary>
public partial class BridgePrivacyDialog : Window
{
    public BridgePrivacyDialog() => InitializeComponent();

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
