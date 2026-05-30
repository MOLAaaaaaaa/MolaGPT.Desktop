using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MolaGPT.Desktop.Services;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// Shows release notes for an available update. GitHub-backed updates can
/// download and verify the installer, then run it after the app exits.
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly string? _downloadUrl;
    private readonly AutoUpdateService? _autoUpdate;
    private readonly AutoUpdateService.UpdatePackage? _package;
    private readonly Func<AutoUpdateService.UpdatePackage, Task>? _backgroundDownloadRequested;
    private string? _downloadedInstallerPath;

    public UpdateDialog(
        string version,
        string? notes,
        string? downloadUrl,
        string? actionText = null,
        string title = "发现新版本",
        string? versionText = null,
        string? installerSha256 = null,
        AutoUpdateService? autoUpdate = null,
        Func<AutoUpdateService.UpdatePackage, Task>? backgroundDownloadRequested = null)
    {
        InitializeComponent();
        _downloadUrl = downloadUrl;
        _autoUpdate = autoUpdate;
        _backgroundDownloadRequested = backgroundDownloadRequested;
        if (CanAutoInstall(version, downloadUrl, installerSha256, autoUpdate))
        {
            _package = new AutoUpdateService.UpdatePackage(
                version,
                downloadUrl!,
                installerSha256!,
                Path.GetFileName(new Uri(downloadUrl!).LocalPath));
        }

        TitleText.Text = title;
        VersionText.Text = versionText ?? $"新版本 v{version} 已发布";
        NotesMarkdown.Markdown = string.IsNullOrWhiteSpace(notes)
            ? "本次发布暂无更新说明。"
            : notes;
        DownloadButton.Content = _package is not null
            ? "下载并安装"
            : string.IsNullOrWhiteSpace(actionText) ? "立即下载" : actionText;

        // No URL to open. Keep the dialog useful, but avoid a dead button.
        if (string.IsNullOrWhiteSpace(_downloadUrl))
        {
            DownloadButton.IsEnabled = false;
            DownloadButton.Content = "暂无下载";
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };
    }

    private async void DownloadClick(object sender, RoutedEventArgs e)
    {
        if (_package is not null && _autoUpdate is not null)
        {
            if (_backgroundDownloadRequested is not null)
            {
                var package = _package;
                DialogResult = true;
                Close();
                await _backgroundDownloadRequested(package);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_downloadedInstallerPath))
            {
                _autoUpdate.InstallAfterExitAndRestart(_downloadedInstallerPath);
                return;
            }

            DownloadButton.IsEnabled = false;
            try
            {
                VersionText.Text = $"正在下载 MolaGPT Desktop v{_package.Version}";
                var progress = new Progress<double>(p =>
                {
                    DownloadButton.Content = $"下载中 {Math.Clamp((int)(p * 100), 0, 100)}%";
                });
                _downloadedInstallerPath = await _autoUpdate.DownloadAndVerifyAsync(_package, progress);
                VersionText.Text = "安装包已下载并通过校验，重启 MolaGPT 后将自动更新";
                NotesMarkdown.Markdown = "准备就绪。点击 **重启并更新** 后，MolaGPT 会退出并静默安装新版本，安装完成后自动重新打开。";
                DownloadButton.Content = "重启并更新";
            }
            catch (Exception ex)
            {
                VersionText.Text = "自动更新未完成";
                NotesMarkdown.Markdown = $"下载或校验安装包失败：{ex.Message}";
                DownloadButton.Content = "重试";
            }
            finally
            {
                DownloadButton.IsEnabled = true;
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(_downloadUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _downloadUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Browser unavailable / blocked — nothing actionable here.
            }
        }
        DialogResult = true;
        Close();
    }

    private static bool CanAutoInstall(
        string version,
        string? downloadUrl,
        string? installerSha256,
        AutoUpdateService? autoUpdate)
    {
        return autoUpdate is not null
               && !string.IsNullOrWhiteSpace(version)
               && !string.IsNullOrWhiteSpace(downloadUrl)
               && Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
               && uri.Scheme is "https"
               && uri.LocalPath.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(installerSha256);
    }

    private void LaterClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
