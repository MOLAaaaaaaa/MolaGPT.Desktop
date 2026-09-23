using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.App.Views;

/// <summary>
/// The MolaGPT 账号 page: who is signed in, what the account has left, and the
/// account-only switches (对话云同步, Tracks). Usage used to live in a separate
/// window off the title bar, so the same account was managed in two places.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Usage is read from the server; re-opening the page within this
    /// window shows what was fetched rather than asking again.</summary>
    private static readonly TimeSpan UsageFreshFor = TimeSpan.FromSeconds(30);

    private readonly ObservableCollection<AccountModelRow> _accountModels = [];
    private MolaGptProxyProvider? _proxy;
    private DateTime? _usageFetchedAt;
    private int _usageRequest;

    private void InitializeAccountPage(MolaGptProxyProvider? proxy)
    {
        _proxy = proxy;
        PART_UsageModels.ItemsSource = _accountModels;
        PART_AccountAction.Click += (_, _) => AccountRequested?.Invoke(this, EventArgs.Empty);
        PART_Logout.Click += OnLogoutClick;
        PART_RefreshUsage.Click += (_, _) => _ = RefreshUsageAsync(force: true);
    }

    internal void OpenAccountPage()
    {
        PART_Nav.SelectedItem = PART_AccountNav;
        ShowSelectedPage();
    }

    internal void RefreshAccountUi()
    {
        var loggedIn = _auth is null ? _settings.IsLoggedIn : !string.IsNullOrEmpty(_auth.CurrentJwt);
        var username = _auth?.CurrentUsername ?? _settings.MolaGptUsername;

        _settings.IsLoggedIn = loggedIn;
        _settings.MolaGptUsername = loggedIn ? username : null;

        var name = string.IsNullOrWhiteSpace(username) ? "MolaGPT 用户" : username;
        PART_AccountNavName.Text = loggedIn ? name : "登录 MolaGPT";
        PART_AccountStatus.Text = loggedIn ? name : "MolaGPT 账号";
        PART_AccountAction.IsVisible = !loggedIn;
        PART_AccountBadgeDot.IsVisible = loggedIn;
        PART_UsageSection.IsVisible = loggedIn && _proxy is not null;
        PART_LogoutSection.IsVisible = loggedIn && _auth is not null;

        if (loggedIn)
        {
            // The badge is filled in by the usage fetch; until then it only says
            // the session is live.
            if (_usageFetchedAt is null) PART_AccountDetail.Text = "已登录";
            if (PART_AccountNav.IsSelected) _ = RefreshUsageAsync(force: false);
            return;
        }

        PART_AccountDetail.Text = "登录后可在桌面端使用 MolaGPT 的模型、对话同步与个性化记忆。";
        PART_CloudSyncStatus.Text = string.Empty;
        _usageFetchedAt = null;
        _usageRequest++;
        _accountModels.Clear();
    }

    private async Task RefreshUsageAsync(bool force)
    {
        if (_proxy is null || !_settings.IsLoggedIn) return;
        if (!force && _usageFetchedAt is { } at && DateTime.UtcNow - at < UsageFreshFor) return;

        var request = ++_usageRequest;
        _usageFetchedAt = DateTime.UtcNow;
        PART_RefreshUsage.IsEnabled = false;
        if (_accountModels.Count == 0) ShowUsageStatus("正在加载用量…");

        MolaGptStatus? status;
        try
        {
            status = await _proxy.FetchStatusAsync();
        }
        catch (MolaGptAuthExpiredException)
        {
            if (request != _usageRequest) return;
            ShowUsageEmpty("登录已过期，请重新登录。");
            return;
        }
        catch (Exception ex)
        {
            if (request != _usageRequest) return;
            // Let the next visit try again instead of holding a failure for 30s.
            _usageFetchedAt = null;
            ShowUsageEmpty($"无法获取用量：{ex.Message}");
            return;
        }
        finally
        {
            if (request == _usageRequest) PART_RefreshUsage.IsEnabled = true;
        }

        if (request != _usageRequest) return;
        if (status is null)
        {
            ShowUsageEmpty("尚未登录。");
            return;
        }

        if (!string.IsNullOrEmpty(status.Username))
        {
            PART_AccountStatus.Text = status.Username;
            PART_AccountNavName.Text = status.Username;
        }

        PART_AccountDetail.Text = status.Unlimited ? "无限制账户" : status.IsDonor ? "捐赠用户" : "已注册用户";
        ShowUsageStatus(null);

        if (status.Credits is { } credits && !status.Unlimited)
            BuildCreditRows(status, credits);
        else
            BuildLegacyRows(status);
    }

    private void BuildCreditRows(MolaGptStatus status, MolaGptCredits credits)
    {
        _accountModels.Clear();
        PART_UsageEmpty.IsVisible = false;
        PART_UsageTitle.Text = "额度用量";
        PART_UsageModelsExpander.Header = "各模型可用次数";
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
            _accountModels.Add(new AccountModelRow
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

        if (_accountModels.Count == 0) ShowModelsEmpty("当前账户没有可用模型");
    }

    private void BuildLegacyRows(MolaGptStatus status)
    {
        _accountModels.Clear();
        PART_UsageEmpty.IsVisible = false;
        PART_CreditsPanel.IsVisible = false;
        PART_EstimateHint.IsVisible = false;
        PART_UsageTitle.Text = "今日使用情况";
        PART_UsageModelsExpander.Header = "各模型用量";
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

            _accountModels.Add(new AccountModelRow
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
        if (_accountModels.Count == 0) ShowModelsEmpty("当前账户没有可用模型配额");
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

    private void ShowUsageStatus(string? message)
    {
        PART_UsageStatus.Text = message ?? string.Empty;
        PART_UsageStatus.IsVisible = message is not null;
    }

    /// <summary>The whole fetch failed: nothing on the card is current.</summary>
    private void ShowUsageEmpty(string message)
    {
        _accountModels.Clear();
        PART_TotalRequests.Text = "—";
        PART_TotalTokens.Text = "—";
        PART_CreditsPanel.IsVisible = false;
        PART_EstimateHint.IsVisible = false;
        PART_UsageEmpty.IsVisible = false;
        ShowUsageStatus(message);
    }

    /// <summary>The fetch worked but lists no models.</summary>
    private void ShowModelsEmpty(string message)
    {
        _accountModels.Clear();
        PART_EstimateHint.IsVisible = false;
        PART_UsageEmpty.Text = message;
        PART_UsageEmpty.IsVisible = true;
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (_auth is null) return;
        var confirmed = await Confirm.AskAsync(
            this,
            "退出登录",
            "退出后将无法继续使用 MolaGPT 模型，未下载到本地的云端对话占位会被清除，已下载的本地对话会保留。",
            "退出登录");
        if (!confirmed) return;

        // LoggedOut reaches MainWindow, which refreshes the title bar and calls
        // back into RefreshAccountUi; calling it here too keeps this page right
        // even when nothing is listening.
        _auth.Logout();
        RefreshAccountUi();
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
