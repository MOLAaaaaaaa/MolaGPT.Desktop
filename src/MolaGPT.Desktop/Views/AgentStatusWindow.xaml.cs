using System.Windows;
using System.Windows.Input;
using MolaGPT.ViewModels.Agents;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Minimal Agent status surface: a session list with phase / attention badges
/// and a one-line send + interrupt to drive a live turn for local verification.
/// No transcript — the transcript is rendered on the phone (Phase 3). The window
/// is a thin renderer over <see cref="AgentBridgeStatusViewModel"/>, which in
/// turn reads the headless <c>AgentBridgeService</c>.
/// </summary>
public partial class AgentStatusWindow : Window
{
    private readonly AgentBridgeStatusViewModel _vm;

    public AgentStatusWindow(AgentBridgeStatusViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PickFolderAsync = () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 Agent 工作目录",
                Multiselect = false
            };
            var ok = Owner is { } owner ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            return Task.FromResult(ok == true ? dlg.FolderName : null);
        };
    }

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void RefreshClick(object sender, RoutedEventArgs e) => _ = _vm.LoadAsync();

    private void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.Handled)
        {
            e.Handled = true;
            if (_vm.SendCommand.CanExecute(null))
                _vm.SendCommand.Execute(null);
        }
    }
}