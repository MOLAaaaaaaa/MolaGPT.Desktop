using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.App.Views;

public partial class AccountWindow : MolaContentWindow
{
    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;
    private readonly ObservableCollection<AccountModelRow> _models = [];

    public AccountWindow(MolaGptAuthService auth, MolaGptProxyProvider proxy)
    {
        _auth = auth;
        _proxy = proxy;

        InitializeComponent();
        PART_ModelList.ItemsSource = _models;
        PART_Close.Click += (_, _) => Close(false);
        PART_Logout.Click += OnLogoutClick;
        Opened += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        PART_Username.Text = _auth.CurrentUsername ?? "用户";
        PART_Status.Text = "加载用量中...";

        MolaGptStatus? status;
        try
        {
            status = await _proxy.FetchStatusAsync();
        }
        catch (MolaGptAuthExpiredException)
        {
            PART_Status.Text = "登录已过期，请重新登录";
            ShowEmpty("尚未登录或登录已过期");
            return;
        }
        catch (Exception ex)
        {
            PART_Status.Text = $"无法连接服务器：{ex.Message}";
            ShowEmpty("用量信息暂不可用，请稍后重试");
            return;
        }

        if (status is null)
        {
            ShowEmpty("尚未登录");
            return;
        }

        PART_Username.Text = string.IsNullOrEmpty(status.Username) ? "用户" : status.Username;
        PART_UserBadge.Text = status.Unlimited ? "无限制账户" : status.IsDonor ? "捐赠用户" : "已注册用户";
        PART_Status.Text = string.Empty;

        if (status.Credits is { } credits && !status.Unlimited)
            BuildCreditRows(status, credits);
        else
            BuildLegacyRows(status);
    }

    private void BuildCreditRows(MolaGptStatus status, MolaGptCredits credits)
    {
        _models.Clear();
        PART_Empty.IsVisible = false;
        PART_SectionTitle.Text = "额度用量";
        PART_TotalRequests.Text = $"{credits.RemainingPercent}%";
        PART_TotalRequestsLabel.Text = "额度剩余";
        PART_TotalTokens.Text = FormatTokens(credits.TotalTokens(status.TokensUsage));
        PART_TotalTokensLabel.Text = credits.IsRolling ? $"近 {credits.WindowDays} 天 Tokens" : "今日 Tokens 用量";

        PART_CreditsTier.Text = credits.TierLabel;
        PART_CreditsWindow.Text = credits.WindowLabel;
        PART_CreditsProgress.Value = credits.UsedFraction;
        PART_CreditsPanel.IsVisible = credits.Allowance > 0;
        PART_CreditsHint.Text = $"额度已耗尽，{credits.RecoveryLabel}。";
        PART_CreditsHint.IsVisible = credits.Exhausted;
        PART_EstimateHint.IsVisible = true;

        var rows = status.Limits
            .Where(pair => pair.Value.Enabled)
            .Select(pair => (Id: pair.Key, Limit: pair.Value, Status: status.ModelStatus.GetValueOrDefault(pair.Key)))
            .Where(row => status.IsDonor || row.Status?.Reason != "donor_only")
            .OrderBy(row => row.Status?.CreditMultiplier ?? double.MaxValue)
            .ThenBy(row => row.Limit.DisplayName, StringComparer.CurrentCulture);

        foreach (var row in rows)
        {
            var multiplier = row.Status?.CreditMultiplier;
            var uses = credits.EstimatedUses(multiplier);
            var rightText = multiplier is null
                ? "暂不可用"
                : uses == int.MaxValue
                    ? "不消耗额度"
                    : uses <= 0 ? "额度不足" : $"约 {uses} 次";

            var symbol = row.Status?.CreditSymbol;
            _models.Add(new AccountModelRow
            {
                Name = row.Limit.DisplayName,
                RightText = rightText,
                HasRightText = true,
                RightIsSuccess = uses == int.MaxValue,
                RightIsError = multiplier is null || uses <= 0,
                Opacity = multiplier is null || uses <= 0 ? 0.55 : 1,
                PriceSymbol = symbol?.Length == 0 ? "限免" : symbol ?? string.Empty,
                HasPriceSymbol = symbol is not null,
                PriceIsSuccess = symbol?.Length is 0 or 1,
                PriceIsWarning = symbol?.Length == 3,
                PriceIsError = symbol?.Length >= 4,
                Detail = credits.TokensFor(row.Id, status.TokensUsage) is var usedTokens && usedTokens > 0
                    ? $"{credits.SpentLabel} {FormatTokens(usedTokens)}"
                    : string.Empty
            });
        }

        if (_models.Count == 0) ShowEmpty("当前账户没有可用模型");
    }

    private void BuildLegacyRows(MolaGptStatus status)
    {
        _models.Clear();
        PART_Empty.IsVisible = false;
        PART_CreditsPanel.IsVisible = false;
        PART_EstimateHint.IsVisible = false;
        PART_SectionTitle.Text = "今日使用情况";
        PART_TotalRequestsLabel.Text = "总请求次数";
        PART_TotalTokensLabel.Text = "总 Tokens 用量";

        var totalRequests = 0;
        long totalTokens = 0;
        foreach (var (modelId, limit) in status.Limits.OrderBy(pair => pair.Value.DisplayName, StringComparer.CurrentCulture))
        {
            if (!limit.Enabled) continue;

            var used = status.Usage.GetValueOrDefault(modelId, 0);
            var usedTokens = status.TokensUsage.GetValueOrDefault(modelId, 0);
            totalRequests += used;
            totalTokens += usedTokens;
            status.ModelStatus.TryGetValue(modelId, out var modelStatus);

            var requestLimit = EffectiveLimit(limit.DailyRequests, modelStatus?.Remaining, used);
            var tokenLimit = EffectiveLimit(limit.DailyTokens, modelStatus?.RemainingTokens, usedTokens);
            var requestsUnlimited = status.Unlimited || limit.DailyRequests == -1 || modelStatus?.Remaining == -1;
            var tokensUnlimited = status.Unlimited || limit.DailyTokens is -1 or null || modelStatus?.RemainingTokens == -1;

            _models.Add(new AccountModelRow
            {
                Name = limit.DisplayName,
                HasLegacyUsage = true,
                RequestsText = $"{used.ToString(CultureInfo.InvariantCulture)}/{(requestsUnlimited ? "无限制" : requestLimit.ToString(CultureInfo.InvariantCulture))}",
                RequestsRatio = requestsUnlimited || requestLimit <= 0 ? 0 : Math.Min(used / (double)requestLimit, 1),
                ShowRequestsBar = !requestsUnlimited && requestLimit > 0,
                TokensText = $"{FormatTokens(usedTokens)}/{(tokensUnlimited ? "无限制" : FormatTokens(tokenLimit))}",
                TokensRatio = tokensUnlimited || tokenLimit <= 0 ? 0 : Math.Min(usedTokens / (double)tokenLimit, 1),
                ShowTokensBar = !tokensUnlimited && tokenLimit > 0
            });
        }

        PART_TotalRequests.Text = totalRequests.ToString(CultureInfo.InvariantCulture);
        PART_TotalTokens.Text = FormatTokens(totalTokens);
        if (_models.Count == 0) ShowEmpty("当前账户没有可用模型配额");
    }

    private static int EffectiveLimit(int? declaredLimit, int? remaining, int used)
    {
        if (declaredLimit is -1 or null) return remaining is null ? 0 : Math.Max(0, remaining.Value + used);
        if (remaining is null) return declaredLimit.Value;
        return Math.Min(declaredLimit.Value, remaining.Value + used);
    }

    private static string FormatTokens(long value)
    {
        if (value < 1_000) return value.ToString(CultureInfo.InvariantCulture);
        if (value < 1_000_000) return (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    private void ShowEmpty(string message)
    {
        _models.Clear();
        PART_CreditsPanel.IsVisible = false;
        PART_EstimateHint.IsVisible = false;
        PART_Empty.Text = message;
        PART_Empty.IsVisible = true;
    }

    private async void OnLogoutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var confirmed = await Confirm.AskAsync(
            this,
            "退出登录",
            "退出后将无法继续使用 MolaGPT 模型，未下载到本地的云端对话占位会被清除，已下载的本地对话会保留。",
            "退出登录");
        if (!confirmed) return;

        _auth.Logout();
        Close(true);
    }
}

public sealed class AccountModelRow
{
    public string Name { get; init; } = string.Empty;
    public string PriceSymbol { get; init; } = string.Empty;
    public bool HasPriceSymbol { get; init; }
    public bool PriceIsSuccess { get; init; }
    public bool PriceIsWarning { get; init; }
    public bool PriceIsError { get; init; }
    public string RightText { get; init; } = string.Empty;
    public bool HasRightText { get; init; }
    public bool RightIsSuccess { get; init; }
    public bool RightIsWarning { get; init; }
    public bool RightIsError { get; init; }
    public string Detail { get; init; } = string.Empty;
    public bool HasDetail => Detail.Length > 0;
    public double Opacity { get; init; } = 1;
    public bool HasLegacyUsage { get; init; }
    public string RequestsText { get; init; } = string.Empty;
    public double RequestsRatio { get; init; }
    public bool ShowRequestsBar { get; init; }
    public string TokensText { get; init; } = string.Empty;
    public double TokensRatio { get; init; }
    public bool ShowTokensBar { get; init; }
}
