using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// MolaGPT logged-in account panel. Pulls usage and quota data from
/// <c>api/auth/status.php</c> via
/// <see cref="MolaGptProxyProvider.FetchStatusAsync"/>, renders one row per
/// model with a request progress bar and a tokens progress bar, and offers
/// a logout action that wipes the JWT + unregisters the proxy provider so
/// the next chat falls back to MockEcho or a BYOK provider.
/// </summary>
public partial class AccountDialog : Window
{
    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;
    private readonly ProviderRegistry _registry;

    public AccountDialog(MolaGptAuthService auth, MolaGptProxyProvider proxy, ProviderRegistry registry)
    {
        InitializeComponent();
        _auth = auth;
        _proxy = proxy;
        _registry = registry;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };

        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        UsernameText.Text = _auth.CurrentUsername ?? "用户";
        StatusText.Text = "加载用量中…";

        MolaGptStatus? status = null;
        try
        {
            status = await _proxy.FetchStatusAsync();
        }
        catch (MolaGptAuthExpiredException)
        {
            StatusText.Text = "登录已过期，请重新登录";
            // Auth has already been cleared inside FetchStatusAsync on 401.
            ShowEmpty("尚未登录或登录已过期");
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"无法连接服务器：{ex.Message}";
            ShowEmpty("用量信息暂不可用，请稍后重试");
            return;
        }

        if (status is null)
        {
            ShowEmpty("尚未登录");
            return;
        }

        UsernameText.Text = string.IsNullOrEmpty(status.Username) ? "用户" : status.Username;
        UserBadgeText.Text = status.Unlimited ? "无限制账户" : (status.IsDonor ? "捐赠用户" : "已注册用户");
        StatusText.Text = "";

        BuildModelRows(status);
    }

    private void BuildModelRows(MolaGptStatus status)
    {
        ModelList.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;

        int totalReq = 0;
        long totalTokens = 0;

        // Iterate over the model set exposed by the user's quota table.
        foreach (var (modelId, limit) in status.Limits.OrderBy(kv => kv.Value.DisplayName, StringComparer.CurrentCulture))
        {
            if (!limit.Enabled) continue;

            int used = status.Usage.GetValueOrDefault(modelId, 0);
            int usedTokens = status.TokensUsage.GetValueOrDefault(modelId, 0);
            totalReq += used;
            totalTokens += usedTokens;

            status.ModelStatus.TryGetValue(modelId, out var ms);

            var card = BuildModelCard(modelId, limit, used, usedTokens, ms, status.Unlimited);
            ModelList.Children.Add(card);
        }

        if (ModelList.Children.Count == 0)
        {
            ShowEmpty("当前账户没有可用模型配额");
        }

        TotalRequestsText.Text = totalReq.ToString(CultureInfo.InvariantCulture);
        TotalTokensText.Text = FormatTokens(totalTokens);
    }

    private FrameworkElement BuildModelCard(
        string modelId,
        MolaGptModelLimit limit,
        int used,
        int usedTokens,
        MolaGptModelStatus? ms,
        bool isUnlimitedUser)
    {
        // Usage card with request and token progress bars.
        var border = new Border
        {
            Background = (Brush)FindResource("Brush.Bg.Secondary"),
            CornerRadius = (CornerRadius)FindResource("Radius.Md"),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel();
        border.Child = stack;

        // Title
        stack.Children.Add(new TextBlock
        {
            Text = limit.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        // Effective limits — the web honors the smaller of (config limit,
        // remaining + used) so anti-abuse adjustments show through.
        var effectiveReq = EffectiveLimit(limit.DailyRequests, ms?.Remaining, used);
        var effectiveTok = EffectiveLimit(limit.DailyTokens, ms?.RemainingTokens, usedTokens);
        bool reqUnlimited = isUnlimitedUser || limit.DailyRequests == -1 || ms?.Remaining == -1;
        bool tokUnlimited = isUnlimitedUser || limit.DailyTokens == -1 || limit.DailyTokens is null || ms?.RemainingTokens == -1;

        stack.Children.Add(BuildProgressRow(
            label: "请求次数",
            usedText: used.ToString(CultureInfo.InvariantCulture),
            limitText: reqUnlimited ? "无限制" : effectiveReq.ToString(CultureInfo.InvariantCulture),
            ratio: reqUnlimited || effectiveReq <= 0 ? 0 : Math.Min(used / (double)effectiveReq, 1.0),
            barBrushKey: "Brush.Primary",
            showBar: !reqUnlimited && effectiveReq > 0));

        stack.Children.Add(new Separator { Opacity = 0, Margin = new Thickness(0, 6, 0, 0) });

        stack.Children.Add(BuildProgressRow(
            label: "Tokens 用量",
            usedText: FormatTokens(usedTokens),
            limitText: tokUnlimited ? "无限制" : FormatTokens(effectiveTok),
            ratio: tokUnlimited || effectiveTok <= 0 ? 0 : Math.Min(usedTokens / (double)effectiveTok, 1.0),
            barBrushKey: "Brush.Success",
            showBar: !tokUnlimited && effectiveTok > 0));

        return border;
    }

    private FrameworkElement BuildProgressRow(
        string label,
        string usedText,
        string limitText,
        double ratio,
        string barBrushKey,
        bool showBar)
    {
        var stack = new StackPanel();

        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        headerGrid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            FontSize = 12
        });
        var counts = new TextBlock
        {
            Text = $"{usedText}/{limitText}",
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            FontSize = 12,
            FontFamily = (FontFamily)FindResource("Font.Mono")
        };
        Grid.SetColumn(counts, 1);
        headerGrid.Children.Add(counts);

        stack.Children.Add(headerGrid);

        if (showBar)
        {
            var trackBg = (Brush)FindResource("Brush.Bg.Tertiary");

            // Pick bar color by saturation: ≥80% → warning, ≥100% → error.
            Brush barBrush;
            if (ratio >= 1) barBrush = (Brush)FindResource("Brush.Error");
            else if (ratio >= 0.8) barBrush = (Brush)FindResource("Brush.Warning");
            else barBrush = (Brush)FindResource(barBrushKey);

            var track = new Border
            {
                Background = trackBg,
                CornerRadius = new CornerRadius(3),
                Height = 6
            };
            // Use a Grid with two children so the fill is positioned relative
            // to the track without computing pixel widths up-front.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, ratio), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - ratio), GridUnitType.Star) });
            var fill = new Border
            {
                Background = barBrush,
                CornerRadius = new CornerRadius(3)
            };
            Grid.SetColumn(fill, 0);
            grid.Children.Add(fill);
            track.Child = grid;
            stack.Children.Add(track);
        }

        return stack;
    }

    private static int EffectiveLimit(int? declaredLimit, int? remaining, int used)
    {
        // login.js:3811 — getEffectiveLimit prefers (remaining + used) when
        // anti-abuse trims the daily allowance below the user's static limit.
        if (declaredLimit is -1 or null) return remaining is null ? 0 : Math.Max(0, remaining.Value + used);
        if (remaining is null) return declaredLimit.Value;
        return Math.Min(declaredLimit.Value, remaining.Value + used);
    }

    private static string FormatTokens(long n)
    {
        if (n < 1_000) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 1_000_000) return (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    private void ShowEmpty(string message)
    {
        ModelList.Children.Clear();
        EmptyState.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        ModelList.Children.Add(EmptyState);
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void LogoutClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "退出后将无法继续使用 MolaGPT 模型，确认退出登录？",
            "退出登录",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _auth.Logout();
        // Drop the proxy provider so the model selector stops listing
        // MolaGPT account models. ProviderRegistry.Unregister is the public
        // counterpart to Register; if it doesn't exist we fall back to a
        // best-effort no-op (the next 401 will surface the auth-expired
        // error and clear the active model anyway).
        try { _registry.Unregister(_proxy.Id); } catch { /* tolerate */ }

        DialogResult = true;
        Close();
    }
}
