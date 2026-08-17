using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.Desktop.Views;

/// <summary>
/// MolaGPT logged-in account panel. Pulls usage and quota data from
/// <c>api/auth/status.php</c> via
/// <see cref="MolaGptProxyProvider.FetchStatusAsync"/>, renders one row per
/// model with a request progress bar and a tokens progress bar, and offers
/// a logout action that clears the account token. Desktop-wide account state
/// cleanup is coordinated from
/// <see cref="MolaGPT.Desktop.Services.MolaGptLogoutCoordinator"/>.
/// </summary>
public partial class AccountDialog : Window
{
    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;

    public AccountDialog(
        MolaGptAuthService auth,
        MolaGptProxyProvider proxy)
    {
        InitializeComponent();
        _auth = auth;
        _proxy = proxy;

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

        if (status.Credits is { } credits && !status.Unlimited)
            BuildCreditRows(status, credits);
        else
            BuildModelRows(status);
    }

    /// <summary>
    /// Credit-pool rendering. There is one shared balance, so the per-model
    /// progress bars of the legacy panel would be meaningless here — each row
    /// answers "how many more turns does my remaining balance buy on this
    /// model" instead, and the single real budget sits in the header.
    /// </summary>
    private void BuildCreditRows(MolaGptStatus status, MolaGptCredits credits)
    {
        ModelList.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        SectionTitleText.Text = "额度用量";

        // Percentage only — the raw point balance is deliberately not surfaced
        // anywhere in the client, matching the web panel.
        TotalRequestsText.Text = $"{credits.RemainingPercent}%";
        TotalRequestsLabel.Text = "额度剩余";

        // Window scope, not today's — the estimates below are drawn against the
        // same window and the two figures have to agree.
        TotalTokensText.Text = FormatTokens(credits.TotalTokens(status.TokensUsage));
        TotalTokensLabel.Text = credits.IsRolling ? $"近 {credits.WindowDays} 天 Tokens" : "今日 Tokens 用量";

        CreditsBarHost.Content = BuildProgressRow(
            label: credits.TierLabel,
            valueText: credits.WindowLabel,
            ratio: credits.UsedFraction,
            barBrushKey: "Brush.Primary",
            showBar: credits.Allowance > 0);
        CreditsBarHost.Visibility = Visibility.Visible;

        // Only worth a line when it changes what the user can do next.
        CreditsHintText.Text = $"额度已耗尽，{credits.RecoveryLabel}。";
        CreditsHintText.Visibility = credits.Exhausted ? Visibility.Visible : Visibility.Collapsed;

        // Cheapest first: the models still worth switching to when the balance
        // is running low should be the ones at the top.
        var rows = status.Limits
            .Where(kv => kv.Value.Enabled)
            .Select(kv => (Id: kv.Key, Limit: kv.Value,
                           Status: status.ModelStatus.GetValueOrDefault(kv.Key)))
            .Where(r => status.IsDonor || r.Status?.Reason != "donor_only")
            // Unpriced models sort last — they are dead weight, not choices.
            .OrderBy(r => r.Status?.CreditMultiplier ?? double.MaxValue)
            .ThenBy(r => r.Limit.DisplayName, StringComparer.CurrentCulture);

        foreach (var r in rows)
        {
            ModelList.Children.Add(BuildCreditModelCard(
                r.Limit,
                credits.TokensFor(r.Id, status.TokensUsage),
                r.Status,
                credits));
        }

        if (ModelList.Children.Count == 0)
        {
            ShowEmpty("当前账户没有可用模型");
            return;
        }

        ModelList.Children.Add(new TextBlock
        {
            Text = "次数按平均对话长度估算，实际随对话长短浮动。",
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 12)
        });
    }

    private void BuildModelRows(MolaGptStatus status)
    {
        ModelList.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        CreditsBarHost.Visibility = Visibility.Collapsed;
        CreditsHintText.Visibility = Visibility.Collapsed;
        SectionTitleText.Text = "今日使用情况";
        TotalRequestsLabel.Text = "总请求次数";
        TotalTokensLabel.Text = "总 Tokens 用量";

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

    private FrameworkElement BuildCreditModelCard(
        MolaGptModelLimit limit,
        int usedTokens,
        MolaGptModelStatus? ms,
        MolaGptCredits credits)
    {
        var multiplier = ms?.CreditMultiplier;
        var uses = credits.EstimatedUses(multiplier);

        string rightText;
        string rightBrushKey;
        bool dim = false;
        if (multiplier is null)
        {
            // No price on file. The server refuses these outright rather than
            // letting them through as an unmetered channel.
            rightText = "暂不可用";
            rightBrushKey = "Brush.Error";
            dim = true;
        }
        else if (uses == int.MaxValue)
        {
            // Not "不限次数": a zero rate can come from a free-quota pool that
            // reprices itself once the pool drains, and the per-request size cap
            // still applies either way.
            rightText = "不消耗额度";
            rightBrushKey = "Brush.Success";
        }
        else if (uses <= 0)
        {
            rightText = "额度不足";
            rightBrushKey = "Brush.Error";
            dim = true;
        }
        else
        {
            rightText = $"约 {uses} 次";
            rightBrushKey = "Brush.Text.Primary";
        }

        var border = new Border
        {
            Background = (Brush)FindResource("Brush.Bg.Secondary"),
            CornerRadius = (CornerRadius)FindResource("Radius.Md"),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = dim ? 0.55 : 1.0
        };

        var stack = new StackPanel();
        border.Child = stack;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        head.Children.Add(new TextBlock
        {
            Text = limit.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var symbol = BuildPriceSymbol(ms?.CreditSymbol);
        if (symbol is not null)
        {
            Grid.SetColumn(symbol, 1);
            head.Children.Add(symbol);
        }

        var right = new TextBlock
        {
            Text = rightText,
            FontSize = 13,
            Foreground = (Brush)FindResource(rightBrushKey),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(right, 2);
        head.Children.Add(right);

        stack.Children.Add(head);

        if (usedTokens > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{credits.SpentLabel} {FormatTokens(usedTokens)}",
                FontSize = 11,
                Foreground = (Brush)FindResource("Brush.Text.Muted"),
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        return border;
    }

    /// <summary>Price tier chip. The server owns the thresholds and hands us a
    /// run of <c>$</c>; empty string means free, null means unpriced.</summary>
    private FrameworkElement? BuildPriceSymbol(string? symbol)
    {
        if (symbol is null) return null;

        if (symbol.Length == 0)
        {
            return new TextBlock
            {
                // "限免", not "免费" — free-quota pools zero the rate only while
                // the pool lasts, then hand back the real price.
                Text = "限免",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Brush.Success"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
        }

        var brushKey = symbol.Length switch
        {
            1 => "Brush.Success",
            2 => "Brush.Text.Secondary",
            3 => "Brush.Warning",
            _ => "Brush.Error"
        };

        return new TextBlock
        {
            Text = symbol,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            Foreground = (Brush)FindResource(brushKey),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "消耗档位，$ 越多越贵"
        };
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
            valueText: $"{used.ToString(CultureInfo.InvariantCulture)}/" +
                       (reqUnlimited ? "无限制" : effectiveReq.ToString(CultureInfo.InvariantCulture)),
            ratio: reqUnlimited || effectiveReq <= 0 ? 0 : Math.Min(used / (double)effectiveReq, 1.0),
            barBrushKey: "Brush.Primary",
            showBar: !reqUnlimited && effectiveReq > 0));

        stack.Children.Add(new Separator { Opacity = 0, Margin = new Thickness(0, 6, 0, 0) });

        stack.Children.Add(BuildProgressRow(
            label: "Tokens 用量",
            valueText: $"{FormatTokens(usedTokens)}/" + (tokUnlimited ? "无限制" : FormatTokens(effectiveTok)),
            ratio: tokUnlimited || effectiveTok <= 0 ? 0 : Math.Min(usedTokens / (double)effectiveTok, 1.0),
            barBrushKey: "Brush.Success",
            showBar: !tokUnlimited && effectiveTok > 0));

        return border;
    }

    /// <param name="valueText">Right-hand side of the header, already composed.
    /// The legacy rows pass "used/limit"; the credit bar passes the reset hint,
    /// because the pool is shown as a percentage and never as raw points.</param>
    private FrameworkElement BuildProgressRow(
        string label,
        string valueText,
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
            Text = valueText,
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
        CreditsBarHost.Visibility = Visibility.Collapsed;
        CreditsHintText.Visibility = Visibility.Collapsed;
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
            "退出后将无法继续使用 MolaGPT 模型，未下载到本地的云端对话占位会被清除（已下载的本地对话会保留），确认退出登录？",
            "退出登录",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _auth.Logout();

        DialogResult = true;
        Close();
    }
}
