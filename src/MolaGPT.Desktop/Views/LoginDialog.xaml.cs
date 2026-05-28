using System.Windows;
using System.Windows.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// MolaGPT login dialog. On success it refreshes the proxy model list and
/// registers the provider so the model selector can use the account route.
/// </summary>
public partial class LoginDialog : Window
{
    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;
    private readonly ProviderRegistry _registry;

    public LoginDialog(MolaGptAuthService auth, MolaGptProxyProvider proxy, ProviderRegistry registry)
    {
        InitializeComponent();
        _auth = auth;
        _proxy = proxy;
        _registry = registry;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };

        UsernameBox.Text = _auth.CurrentUsername ?? string.Empty;
        UsernameBox.Focus();
    }

    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        var name = UsernameBox.Text?.Trim();
        var pw = PasswordBox.Password ?? string.Empty;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pw))
        {
            ShowStatus("请输入用户名和密码。");
            return;
        }

        ShowStatus("登录中...");
        SetBusy(true);
        try
        {
            var res = await _auth.LoginAsync(name, pw);
            if (!res.Success)
            {
                ShowStatus(res.ErrorMessage ?? "登录失败。");
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
            DialogResult = true;
            Close();
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

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowStatus(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        UsernameBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        LoginButton.IsEnabled = !busy;
    }
}
