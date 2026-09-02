using System.Diagnostics;
using Avalonia.Controls;
using MolaGPT.App.Infrastructure;

namespace MolaGPT.App.Views;

public partial class UpdateWindow : MolaContentWindow
{
    private readonly string? _downloadUrl;
    private readonly AppAutoUpdateService.UpdatePackage? _package;
    private readonly Func<AppAutoUpdateService.UpdatePackage, Task>? _backgroundDownload;

    internal UpdateWindow(
        string version,
        string? notes,
        string? downloadUrl,
        string actionText,
        string? installerSha256,
        Func<AppAutoUpdateService.UpdatePackage, Task>? backgroundDownload)
    {
        InitializeComponent();
        _downloadUrl = downloadUrl;
        _backgroundDownload = backgroundDownload;
        if (CanAutoInstall(version, downloadUrl, installerSha256))
        {
            _package = new AppAutoUpdateService.UpdatePackage(
                version, downloadUrl!, installerSha256!, Path.GetFileName(new Uri(downloadUrl!).LocalPath));
        }

        PART_Version.Text = $"新版本 v{version} 已发布";
        PART_Notes.Markdown = string.IsNullOrWhiteSpace(notes) ? "本次发布暂无更新说明。" : notes;
        PART_Action.Content = _package is not null
            ? "下载并安装"
            : string.IsNullOrWhiteSpace(actionText) ? "立即下载" : actionText;
        PART_Action.IsEnabled = !string.IsNullOrWhiteSpace(downloadUrl);

        PART_Close.Click += (_, _) => Close(false);
        PART_Later.Click += (_, _) => Close(false);
        PART_Action.Click += OnAction;
    }

    private async void OnAction(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_package is not null && _backgroundDownload is not null)
        {
            var package = _package;
            Close(true);
            await _backgroundDownload(package);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_downloadUrl))
            Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true });
        Close(true);
    }

    private static bool CanAutoInstall(string version, string? url, string? sha256) =>
        !string.IsNullOrWhiteSpace(version)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.LocalPath.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(sha256);
}
