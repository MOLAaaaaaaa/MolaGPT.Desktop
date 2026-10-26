using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// the MolaGPT account's shared credits.
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

}
