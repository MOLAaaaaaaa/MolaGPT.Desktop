using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat.Providers;

namespace MolaGPT.ViewModels;

/// <summary>One limit of the plan: how much of it is left and when that changes.</summary>
/// <param name="RemainingPercent">Rounded up, so a sliver left never reads as 0%.</param>
/// <param name="UsedFraction">0–1, for the bar.</param>
public sealed record PlanUsageRow(string Label, int RemainingPercent, double UsedFraction, string? Detail)
{
    public string ValueText => $"剩余 {RemainingPercent}%";
    public bool IsLow => RemainingPercent is > 0 and <= 10;
    public bool IsEmpty => RemainingPercent <= 0;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>
/// What the plan behind the active model has left, shown with the context gauge:
/// a ChatGPT plan's rolling limits, or the MolaGPT account's shared credits.
///
/// Follows the provider, not the conversation, so <see cref="ContextGaugeViewModel.Reset"/>
/// leaves it alone. The MolaGPT credit balance itself is never shown, only the
/// share left and the per-model estimate, as on every other surface.
/// </summary>
public sealed partial class PlanUsageViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Tooltip))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private IReadOnlyList<PlanUsageRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(Tooltip))]
    private string? _note;

    public bool IsVisible => Title.Length > 0;
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>One line for the gauge's tooltip, plus the note when there is one.</summary>
    public string Tooltip
    {
        get
        {
            if (!IsVisible) return string.Empty;
            var line = Rows.Count == 0
                ? Title
                : Title + "：" + string.Join(" · ", Rows.Select(row => $"{row.Label}剩余 {row.RemainingPercent}%"));
            return HasNote ? line + "\n" + Note : line;
        }
    }

    /// <summary>Re-reads the figures when the popup opens. Set by whoever feeds this.</summary>
    public Func<Task>? RefreshRequested { get; set; }

    // Concurrent so the command never reports itself unavailable: it is bound to
    // the gauge button, which would grey out while a read is in flight.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (RefreshRequested is { } refresh) await refresh();
    }

    public void Clear()
    {
        Rows = [];
        Note = null;
        Title = string.Empty;
    }

    /// <summary>A ChatGPT plan's windows, shortest first. Nothing until read.</summary>
    public void ShowChatGpt(ChatGptUsage? usage, string? plan, DateTimeOffset now)
    {
        if (usage is null)
        {
            Clear();
            return;
        }

        Rows = usage.Windows.Select(window =>
        {
            var used = Math.Clamp(window.UsedPercent, 0, 100);
            return new PlanUsageRow(
                WindowLabel(window.WindowMinutes),
                Remaining(used),
                used / 100,
                window.ResetsAt is { } at ? ResetText(at, now) : null);
        }).ToList();
        Note = usage.LimitReached ? "已达用量上限" : null;
        Title = "ChatGPT " + PlanLabel(plan);
    }

    /// <summary>The MolaGPT account's shared credits, with how far they go on
    /// <paramref name="model"/>. Nothing on a server still on per-model quotas.</summary>
    public void ShowCredits(MolaGptStatus status, string modelId)
    {
        if (status.Unlimited)
        {
            Rows = [];
            Note = "不限额度";
            Title = "MolaGPT 额度";
            return;
        }
        if (status.Credits is not { } credits)
        {
            Clear();
            return;
        }

        string? detail = credits.Exhausted ? credits.RecoveryLabel
            : credits.IsRolling ? null
            : "明日 0 点重置";
        Rows = [new PlanUsageRow(
            credits.IsRolling ? $"近 {credits.WindowDays} 天" : "今日",
            credits.RemainingPercent,
            credits.UsedFraction,
            detail)];
        status.ModelStatus.TryGetValue(modelId, out var model);
        Note = ModelNote(credits, model);
        Title = "MolaGPT 额度 · " + credits.TierLabel;
    }

    private static string? ModelNote(MolaGptCredits credits, MolaGptModelStatus? model)
    {
        if (model is null || credits.Exhausted) return null;
        if (model.CreditMultiplier is null) return "当前模型暂不可用";
        var uses = credits.EstimatedUses(model.CreditMultiplier) ?? 0;
        if (uses == int.MaxValue) return "当前模型不消耗额度";
        if (uses <= 0) return "剩余额度不足以再发一次";
        // Peak and off-peak prices change the count; say which one it is.
        var period = model.PricingPeriodLabel is { } label ? $"（{label}）" : string.Empty;
        return $"当前模型约 {uses} 次{period}";
    }

    private static int Remaining(double usedPercent) =>
        usedPercent >= 100 ? 0 : Math.Clamp((int)Math.Ceiling(100 - usedPercent), 1, 100);

    private static string WindowLabel(int minutes)
    {
        // Matched loosely: OpenAI reports the length, and a window a few minutes
        // off its nominal size is still that window.
        static bool Near(int value, int target) => Math.Abs(value - target) <= target / 20;
        if (minutes <= 0) return "用量";
        if (Near(minutes, 300)) return "5 小时";
        if (Near(minutes, 1440)) return "每日";
        if (Near(minutes, 10080)) return "每周";
        if (Near(minutes, 43200)) return "每月";
        if (minutes >= 1440) return $"{Math.Round(minutes / 1440d)} 天";
        return minutes >= 60 ? $"{Math.Round(minutes / 60d)} 小时" : $"{minutes} 分钟";
    }

    /// <summary>An absolute time, so the line is still right when read later.</summary>
    private static string ResetText(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var day = local.Date == today ? "今天"
            : local.Date == today.AddDays(1) ? "明天"
            : $"{local.Month} 月 {local.Day} 日";
        return $"{day} {local:HH:mm} 重置";
    }

    private static string PlanLabel(string? plan) => string.IsNullOrWhiteSpace(plan)
        ? "套餐"
        : char.ToUpperInvariant(plan[0]) + plan[1..].ToLowerInvariant();
}
