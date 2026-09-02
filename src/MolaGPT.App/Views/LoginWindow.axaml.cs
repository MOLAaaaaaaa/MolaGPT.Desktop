using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Desktop.Services;

namespace MolaGPT.App.Views;

public partial class LoginWindow : MolaContentWindow
{
    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;
    private readonly ProviderRegistry _registry;
    private readonly MolaGptLocalToolsRegistrar _localToolsRegistrar;
    private bool _waitingForExternal;

    public static event Action<ExternalLoginCompletion>? ExternalLoginCompleted;

    public static bool NotifyExternalLoginCompleted(bool success, string? message = null)
    {
        var handler = ExternalLoginCompleted;
        if (handler is null) return false;
        handler(new ExternalLoginCompletion(success, message));
        return true;
    }

    public LoginWindow(
        MolaGptAuthService auth,
        MolaGptProxyProvider proxy,
        ProviderRegistry registry,
        MolaGptLocalToolsRegistrar localToolsRegistrar)
    {
        _auth = auth;
        _proxy = proxy;
        _registry = registry;
        _localToolsRegistrar = localToolsRegistrar;

        InitializeComponent();

        PART_Username.Text = _auth.CurrentUsername ?? string.Empty;
        PART_Close.Click += (_, _) => Close(false);
        PART_Cancel.Click += (_, _) => Close(false);

        ExternalLoginCompleted += OnExternalLoginCompleted;
        Closed += (_, _) => ExternalLoginCompleted -= OnExternalLoginCompleted;
        Opened += (_, _) => PART_Username.Focus();
    }

    private async void OnLoginClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var username = PART_Username.Text?.Trim();
        var password = PART_Password.Text ?? string.Empty;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            ShowStatus("请输入用户名和密码。");
            return;
        }

        ShowStatus("登录中...");
        SetBusy(true);
        try
        {
            var result = await _auth.LoginAsync(username, password);
            if (!result.Success)
            {
                ShowStatus(result.ErrorMessage ?? "登录失败。");
                return;
            }

            try
            {
                await _proxy.RefreshModelsAsync();
            }
            catch (Exception ex)
            {
                ShowStatus($"已登录，但拉取模型失败：{ex.Message}");
                return;
            }

            _registry.Register(_proxy);
            try { await _localToolsRegistrar.RefreshAsync(); }
            catch { }

            Close(true);
        }
        catch (Exception ex)
        {
            ShowStatus($"网络错误：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnGoogleLoginClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        StartOAuthFlow("https://chatgpt.wljay.cn/v2/api/auth/google_init.php?desktop=1");

    private void OnMicrosoftLoginClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        StartOAuthFlow("https://chatgpt.wljay.cn/v2/api/auth/ms_init.php?desktop=1");

    private void OnLinuxDoLoginClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        StartOAuthFlow("https://chatgpt.wljay.cn/v2/api/auth/oauth_init.php?desktop=1");

    private void StartOAuthFlow(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"无法打开浏览器：{ex.Message}");
            return;
        }

        _waitingForExternal = true;
        ShowStatus("已在系统浏览器中打开授权页，完成后会自动返回；若未自动返回，可点取消重试。");
        SetBusy(true);
    }

    private void OnExternalLoginCompleted(ExternalLoginCompletion completion)
    {
        if (!_waitingForExternal) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (completion.Success)
            {
                Close(true);
                return;
            }

            _waitingForExternal = false;
            SetBusy(false);
            ShowStatus(completion.Message ?? "第三方登录失败，请重试。");
        });
    }

    private void ShowStatus(string message)
    {
        PART_StatusText.Text = message;
        PART_StatusPanel.IsVisible = true;
    }

    private void SetBusy(bool busy)
    {
        PART_Username.IsEnabled = !busy;
        PART_Password.IsEnabled = !busy;
        PART_Login.IsEnabled = !busy;
        PART_Google.IsEnabled = !busy;
        PART_Microsoft.IsEnabled = !busy;
        PART_LinuxDo.IsEnabled = !busy;
    }
}

public sealed record ExternalLoginCompletion(bool Success, string? Message);
