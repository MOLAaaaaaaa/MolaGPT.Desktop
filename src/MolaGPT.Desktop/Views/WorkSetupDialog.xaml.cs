using System.Windows;
using System.Windows.Input;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Views;

public partial class WorkSetupDialog : Window
{
    private readonly PythonRuntimeManager _pythonRuntime;
    private readonly SettingsViewModel _settings;

    public WorkSetupDialog(PythonRuntimeManager pythonRuntime, SettingsViewModel settings)
    {
        InitializeComponent();
        _pythonRuntime = pythonRuntime;
        _settings = settings;
        MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
    }

    private async void ConfigureClick(object sender, RoutedEventArgs e)
    {
        ConfigureButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        ConfigureButton.Content = "配置中…";
        try
        {
            var progress = new Progress<PythonRuntimeProgress>(p =>
                StatusText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"正在配置 {p.Progress:P0}" : p.Message);
            var runtime = await _pythonRuntime.DownloadAndInstallAsync(progress, CancellationToken.None);
            _settings.PythonToolEnabled = true;
            _settings.PythonToolExecutablePath = runtime.PythonExecutablePath;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "配置失败：" + ex.Message;
            ConfigureButton.Content = "重试";
            ConfigureButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
        }
    }

    private void SkipClick(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void CloseClick(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
