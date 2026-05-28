using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// MolaGPT login dialog. On success it refreshes the proxy model list and
/// registers the provider so the model selector can use the account route.
/// Third-party login (Google / Microsoft / Linux Do) opens the system
/// browser at the v2 OAuth init endpoint with desktop=1; oauth_landing.html
/// then redirects to molagpt://oauth_callback?token=... which is captured
/// by the running instance via UrlSchemeRegistrar + SingleInstanceGuard.
///
/// While waiting on the browser the dialog stays open so the user has a
/// place to land when they alt-tab back, and so we have a parent window
/// to close once App.xaml.cs finishes applying the external token. The
/// completion signal comes through <see cref="ExternalLoginCompleted"/>,
/// which App.xaml.cs raises after ApplyExternalToken returns true.
/// </summary>
public partial class LoginDialog : Window
{
    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;
    private readonly ProviderRegistry _registry;
    private bool _waitingForExternal;

    /// <summary>
    /// Raised by App.xaml.cs once a molagpt://oauth_callback deep link has
    /// been processed and the JWT is persisted. The currently-open
    /// LoginDialog (if any) closes itself with DialogResult=true so its
    /// caller (typically SettingsWindow or MainViewModel) can refresh the
    /// account panel and kick a cloud sync.
    /// </summary>
    public static event Action? ExternalLoginCompleted;

    /// <summary>App.xaml.cs calls this after ApplyExternalToken succeeds.</summary>
    public static void NotifyExternalLoginCompleted() => ExternalLoginCompleted?.Invoke();

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

        ExternalLoginCompleted += OnExternalLoginCompleted;
        Closed += (_, _) => ExternalLoginCompleted -= OnExternalLoginCompleted;

        UsernameBox.Text = _auth.CurrentUsername ?? string.Empty;
        UsernameBox.Focus();
    }

    private void OnExternalLoginCompleted()
    {
        // Multiple LoginDialog instances theoretically possible; only the
        // one waiting on a browser handoff should consume this.
        if (!_waitingForExternal) return;
        Dispatcher.Invoke(() =>
        {
            DialogResult = true;
            Close();
        });
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

    private void GoogleLoginClick(object sender, RoutedEventArgs e) =>
        StartOAuthFlow("https://chatgpt.wljay.cn/v2/api/auth/google_init.php?desktop=1");

    private void MicrosoftLoginClick(object sender, RoutedEventArgs e) =>
        StartOAuthFlow("https://chatgpt.wljay.cn/v2/api/auth/ms_init.php?desktop=1");

    private void LinuxDoLoginClick(object sender, RoutedEventArgs e) =>
        StartOAuthFlow("https://chatgpt.wljay.cn/v2/api/auth/oauth_init.php?desktop=1");

    /// <summary>
    /// Opens the system browser at the OAuth init URL and parks the
    /// dialog in a "waiting" state. The dialog stays open until either
    /// the user clicks 取消 or App.xaml.cs raises
    /// <see cref="ExternalLoginCompleted"/> after the molagpt://
    /// callback is processed.
    /// </summary>
    private void StartOAuthFlow(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"无法打开浏览器：{ex.Message}");
            return;
        }

        _waitingForExternal = true;
        ShowStatus("已在系统浏览器中打开授权页，完成后会自动返回；若未自动返回，可点取消重试。");
        SetBusy(true);
        // Re-enable cancel button so the user can bail out manually if
        // the browser hand-off never fires (e.g. they closed the tab).
        LoginButton.IsEnabled = false;
        GoogleButton.IsEnabled = false;
        MicrosoftButton.IsEnabled = false;
        LinuxDoButton.IsEnabled = false;
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
