using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;

namespace MolaGPT.ViewModels;

/// <summary>How close the context is to the point where history starts being summarized.</summary>
public enum ContextPressure
{
    /// <summary>Plenty of room. The gauge should not be drawing attention.</summary>
    Normal,

    /// <summary>Worth knowing about — a good moment to compact deliberately, while the
    /// summary is still made from a complete view rather than an already-crowded one.</summary>
    Warning,

    /// <summary>Compaction is imminent.</summary>
    Critical
}

/// <summary>
/// The composer's context gauge: how much of the model's window this conversation
/// currently occupies, and whether the agent has summarized itself.
///
/// Conversation-scoped and updated once per turn, because that is when the number
/// exists — it comes from the tokens the provider actually counted, so there is
/// nothing to recompute while the user types.
///
/// <para>The rule this type exists to enforce is that <em>unknown is not zero</em>.
/// Between a compaction and the next reply the newest measurement describes the
/// context that was just discarded, and an empty ring in that moment is the single
/// most misleading thing this widget could show: it says "plenty of room" at the
/// exact point the user might want to know what was dropped.</para>
/// </summary>
public sealed partial class ContextGaugeViewModel : ObservableObject
{
    /// <summary>Matches Pi's own reserve: it compacts once the context passes
    /// window − 16,384, so "critical" has to mean "about to happen", not a round
    /// number picked for looking tidy.</summary>
    private const int CompactionReserveTokens = 16_384;

    private const double WarningPercent = 70d;

    [ObservableProperty] private int? _tokens;
    [ObservableProperty] private int _contextWindow;
    [ObservableProperty] private bool _isCompacting;

    /// <summary>Context size at the most recent compaction, 0 if none happened in
    /// this conversation. Kept so the transcript can say what was summarized away
    /// instead of the history silently shrinking.</summary>
    [ObservableProperty] private int _lastCompactionTokensBefore;

    /// <summary>The agent's estimate of what the history weighs after that cut, 0
    /// when it reported none. An estimate from counting characters, never a reading
    /// from the model — see <see cref="CompactionSizes"/>.</summary>
    [ObservableProperty] private int _lastCompactionTokensAfter;

    /// <summary>
    /// True once there is a real reading to show. The gauge stays out of the
    /// composer entirely until then rather than sitting at 0% — an empty ring reads
    /// as a measurement, and before the first reply there has not been one.
    /// </summary>
    public bool IsKnown => Tokens is > 0 && ContextWindow > 0;

    /// <summary>
    /// Whether the gauge belongs in the composer at all. A conversation that has
    /// compacted still warrants the ring even while the reading is unknown — that is
    /// precisely when the user has a reason to look at it.
    /// </summary>
    public bool IsVisible => IsKnown || HasCompacted || IsCompacting;

    /// <summary>0–100 for the ring geometry. 0 when unknown, which only ever reaches
    /// the view while <see cref="IsKnown"/> is false and the ring is hidden.</summary>
    public double Percent => IsKnown
        ? Math.Min(100d, Tokens!.Value * 100d / ContextWindow)
        : 0d;

    public ContextPressure Pressure
    {
        get
        {
            if (!IsKnown) return ContextPressure.Normal;
            if (Tokens!.Value > ContextWindow - CompactionReserveTokens) return ContextPressure.Critical;
            return Percent >= WarningPercent ? ContextPressure.Warning : ContextPressure.Normal;
        }
    }

    // Style-selector hooks, so the ring's colour lives in the theme rather than
    // being mixed by hand in code-behind.
    public bool IsWarning => Pressure == ContextPressure.Warning;
    public bool IsCritical => Pressure == ContextPressure.Critical;

    public string PercentText => IsKnown ? $"{Percent:0}%" : "—";

    public bool HasCompacted => LastCompactionTokensBefore > 0;

    public string CompactedText
    {
        get
        {
            if (!HasCompacted) return string.Empty;
            if (LastCompactionTokensAfter is <= 0
                || LastCompactionTokensAfter >= LastCompactionTokensBefore)
            {
                return $"已压缩 · 压缩前 {Format(LastCompactionTokensBefore)}";
            }

            var saved = LastCompactionTokensBefore - LastCompactionTokensAfter;
            return $"已压缩 · {Format(LastCompactionTokensBefore)} → 约 {Format(LastCompactionTokensAfter)}"
                   + $"，省下 {Format(saved)}";
        }
    }

    /// <summary>
    /// Why the gauge is showing nothing yet. Two different silences — "no reply
    /// counted yet" and "just compacted, waiting to be re-measured" — and telling
    /// them apart is the whole reason this line exists.
    /// </summary>
    public string UnknownReason => HasCompacted ? "压缩后待重新统计" : "回复后统计";

    public string UsageText => IsKnown
        ? $"{Format(Tokens!.Value)} / {Format(ContextWindow)}"
        : "—";

    /// <summary>The popup's headline: usage and share on one line.</summary>
    public string SummaryText => IsKnown ? $"{UsageText} · {PercentText}" : UnknownReason;

    /// <summary>
    /// How much room is left before the agent compacts on its own. Its own property
    /// rather than part of <see cref="Tooltip"/> so the popup can show it without
    /// also repeating the usage line printed directly above it.
    /// </summary>
    public string HeadroomText
    {
        get
        {
            if (!IsKnown) return string.Empty;
            if (Pressure == ContextPressure.Critical) return "即将自动压缩";

            var headroom = Math.Max(0, ContextWindow - CompactionReserveTokens - Tokens!.Value);
            return $"距自动压缩 {Format(headroom)}";
        }
    }

    /// <summary>The whole story in one hover, for people who never open the popup.</summary>
    public string Tooltip
    {
        get
        {
            if (IsCompacting) return "正在压缩";
            if (!IsKnown) return "上下文 · " + UnknownReason;

            var third = HasCompacted ? $"\n{CompactedText}" : string.Empty;
            return $"上下文 {SummaryText}\n{HeadroomText}{third}";
        }
    }

    /// <summary>Fold in what a finished turn reported. Null tokens mean the turn had
    /// nothing countable — a failure, or the gap right after a compaction — and the
    /// previous reading is deliberately left alone rather than being reset, so the
    /// gauge holds its last true value instead of blanking on every hiccup.</summary>
    public void Apply(ContextUsageDelta delta)
    {
        if (delta.ContextWindow > 0) ContextWindow = delta.ContextWindow;
        if (delta.Tokens is > 0) Tokens = delta.Tokens;
        Refresh();
    }

    public void ApplyCompaction(CompactionDelta delta)
    {
        if (delta is { Completed: true, Aborted: false, TokensBefore: > 0 })
        {
            LastCompactionTokensBefore = delta.TokensBefore;
            LastCompactionTokensAfter = delta.TokensAfter;

            // The history really did shrink, and the size of what is left is not
            // known until the model answers again. Holding the pre-compaction number
            // would leave the ring pinned near full and make compaction look broken.
            // The estimate above is a different thing and is labelled as one.
            Tokens = null;
        }

        // Set last, so anything watching this flag for the end of a run reads the
        // outcome rather than the state the run started in.
        IsCompacting = !delta.Completed;
        Refresh();
    }

    /// <summary>Switching conversations switches the measurement with it.</summary>
    public void Reset()
    {
        Tokens = null;
        ContextWindow = 0;
        IsCompacting = false;
        LastCompactionTokensBefore = 0;
        LastCompactionTokensAfter = 0;
        CompactionError = null;
        Refresh();
    }

    // ---- acting on it ---------------------------------------------------

    /// <summary>
    /// What a compaction did to the context.
    ///
    /// <paramref name="TokensBefore"/> is the size at the cut, 0 when the agent
    /// declined because there was nothing worth summarizing.
    /// <paramref name="TokensAfter"/> is the agent's <em>estimate</em> of what is
    /// left — counted from characters, not reported by the model, and 0 on runtimes
    /// that do not report it at all. The two are kept apart on purpose: one is a
    /// measurement and the other is arithmetic on a heuristic.
    /// </summary>
    public readonly record struct CompactionSizes(int TokensBefore, int TokensAfter = 0);

    /// <summary>
    /// Runs a compaction. Null while unset — the gauge is then read-only, which is
    /// how it behaves for providers that have no agent loop to compact.
    /// </summary>
    public Func<CancellationToken, Task<CompactionSizes>>? CompactRequested { get; set; }

    /// <summary>Pushes the auto-compaction preference down to the agent.</summary>
    public Func<bool, CancellationToken, Task>? AutoCompactionChangeRequested { get; set; }

    /// <summary>
    /// Called once a manual compaction actually cut something, with the size at the
    /// cut, so the transcript can mark <em>where</em> and the conversation can keep
    /// the fact past this window.
    ///
    /// Automatic compactions need no equivalent: they arrive as chunks on a live
    /// turn and are recorded there. This one has no turn — which is exactly how it
    /// came to be the only kind that vanished on reload.
    /// </summary>
    public Action<CompactionSizes>? CompactionRecorded { get; set; }

    public bool CanCompact => CompactRequested is not null && !IsCompacting;

    /// <summary>Whether the controls belong in the popup at all.</summary>
    public bool IsControllable => CompactRequested is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompactionError))]
    private string? _compactionError;

    public bool HasCompactionError => !string.IsNullOrWhiteSpace(CompactionError);

    /// <summary>
    /// Mirrors Pi's default rather than assuming the user has an opinion. Turning it
    /// off is a real trade — no summarizing behind your back, but a long enough
    /// conversation eventually gets refused upstream — so it is stated in the popup
    /// rather than buried.
    ///
    /// The one thing on this gauge that is <em>not</em> conversation-scoped, which is
    /// why <see cref="Reset"/> leaves it alone: it is an application-wide preference,
    /// stored in settings and restored into here by whoever wires
    /// <see cref="AutoCompactionChangeRequested"/>.
    /// </summary>
    [ObservableProperty] private bool _isAutoCompactionEnabled = true;

    partial void OnIsAutoCompactionEnabledChanged(bool value)
    {
        if (AutoCompactionChangeRequested is null) return;
        _ = ApplyAutoCompactionAsync(value);
    }

    private async Task ApplyAutoCompactionAsync(bool enabled)
    {
        try
        {
            await AutoCompactionChangeRequested!(enabled, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Say so rather than leaving the switch showing a setting that never
            // reached the agent.
            CompactionError = "设置未生效：" + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCompact))]
    private async Task CompactAsync()
    {
        if (CompactRequested is null) return;

        CompactionError = null;
        IsCompacting = true;
        CompactCommand.NotifyCanExecuteChanged();
        try
        {
            var sizes = await CompactRequested(CancellationToken.None);

            // Same handling as an automatic compaction: the history really did
            // shrink and its new size is unknown until the model replies again.
            if (sizes.TokensBefore > 0)
            {
                LastCompactionTokensBefore = sizes.TokensBefore;
                LastCompactionTokensAfter = sizes.TokensAfter;
                Tokens = null;
                CompactionRecorded?.Invoke(sizes);
            }
            else
            {
                CompactionError = "无可压缩内容";
            }
        }
        catch (Exception ex)
        {
            CompactionError = ex.Message;
        }
        finally
        {
            IsCompacting = false;
            CompactCommand.NotifyCanExecuteChanged();
            Refresh();
        }
    }

    partial void OnLastCompactionTokensAfterChanged(int value) => Refresh();
    partial void OnTokensChanged(int? value) => Refresh();
    partial void OnContextWindowChanged(int value) => Refresh();
    partial void OnIsCompactingChanged(bool value) => Refresh();
    partial void OnLastCompactionTokensBeforeChanged(int value) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsKnown));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(CanCompact));
        OnPropertyChanged(nameof(IsControllable));
        OnPropertyChanged(nameof(HasCompactionError));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(Pressure));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsCritical));
        OnPropertyChanged(nameof(HasCompacted));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HeadroomText));
        OnPropertyChanged(nameof(CompactedText));
        OnPropertyChanged(nameof(UnknownReason));
        OnPropertyChanged(nameof(Tooltip));
    }

    private static string Format(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
        >= 1_000 => $"{tokens / 1_000d:0}K",
        _ => tokens.ToString()
    };
}
