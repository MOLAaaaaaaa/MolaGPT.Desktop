namespace MolaGPT.Core.Personalization;

/// <summary>
/// One long-term memory entry. Projection of the server-side
/// <c>memory_entries</c> table (<c>user_data_manager.php get_memory_entries</c>).
/// Maintained nightly by the dream pipeline; clients only display and adjudicate.
/// </summary>
public sealed record MemoryEntry(
    string Id,
    string Text,
    string? Section = null,
    string? Category = null,
    double Confidence = 0,
    bool Permanent = false,
    double? HalfLifeDays = null,
    long? Ttl = null,
    MemoryRating? UserRating = null,
    long FirstTs = 0,
    long LastTs = 0,
    int Recurrence = 1,
    long CreatedTs = 0,
    bool UserSet = false,
    IReadOnlyList<MemorySource>? Sources = null)
{
    public ConfidenceTier ConfidenceTier => ConfidenceTiers.Of(Confidence);

    public int DaysSinceReinforced(long nowSeconds)
    {
        var reference = LastTs > 0 ? LastTs : CreatedTs;
        if (reference <= 0) return 0;
        return (int)Math.Max(0, (nowSeconds - reference) / 86_400L);
    }

    public MemoryStatus Status(long nowSeconds) =>
        MemoryStatuses.Of(Confidence, DaysSinceReinforced(nowSeconds));

    /// <summary>Time-boxed entries stop being injected after expiry.</summary>
    public bool IsExpired(long nowSeconds) => Ttl.HasValue && Ttl.Value < nowSeconds;
}

public sealed record MemorySource(long Ts, string ChatId);

/// <summary>
/// MEMORY.md projection stats. The server trims by token budget;
/// over-budget entries are not injected into the system prompt.
/// </summary>
public sealed record MemoryProjection(
    int Entries = 0,
    int Skipped = 0,
    int Tokens = 0,
    int Budget = 0)
{
    public float Usage => Budget > 0 ? Math.Clamp((float)Tokens / Budget, 0, 1) : 0;
}

/// <summary>
/// Server fixed 5 sections in fixed order. Wire values are the Chinese names;
/// <c>add_memory_entry</c> validates against them verbatim.
/// </summary>
public enum MemorySection
{
    Identity,
    Preference,
    Project,
    Context,
    Prohibition
}

public static class MemorySections
{
    public const string IdentityWire = "身份与背景";
    public const string PreferenceWire = "长期偏好与表达风格";
    public const string ProjectWire = "进行中的项目";
    public const string ContextWire = "近期上下文";
    public const string ProhibitionWire = "明确的禁止项";

    public static string Wire(MemorySection section) => section switch
    {
        MemorySection.Identity => IdentityWire,
        MemorySection.Preference => PreferenceWire,
        MemorySection.Project => ProjectWire,
        MemorySection.Context => ContextWire,
        MemorySection.Prohibition => ProhibitionWire,
        _ => ContextWire
    };

    public static string Label(MemorySection section) => Wire(section);

    /// <summary>Unknown sections fall back to 近期上下文, same as the server.</summary>
    public static MemorySection FromWire(string? value) => value switch
    {
        IdentityWire => MemorySection.Identity,
        PreferenceWire => MemorySection.Preference,
        ProjectWire => MemorySection.Project,
        ProhibitionWire => MemorySection.Prohibition,
        _ => MemorySection.Context
    };

    public static IReadOnlyList<MemorySection> Ordered { get; } =
    [
        MemorySection.Identity,
        MemorySection.Preference,
        MemorySection.Project,
        MemorySection.Context,
        MemorySection.Prohibition
    ];
}

public enum ConfidenceTier
{
    Core,
    Known,
    Vague
}

public static class ConfidenceTiers
{
    public static ConfidenceTier Of(double confidence) => confidence switch
    {
        >= 0.8 => ConfidenceTier.Core,
        >= 0.4 => ConfidenceTier.Known,
        _ => ConfidenceTier.Vague
    };

    public static string Label(ConfidenceTier tier) => tier switch
    {
        ConfidenceTier.Core => "核心印象",
        ConfidenceTier.Known => "初步了解",
        _ => "模糊猜测"
    };
}

public enum MemoryStatus
{
    Active,
    Stable,
    Growing,
    Fading,
    Weak,
    Questioned
}

public static class MemoryStatuses
{
    public static MemoryStatus Of(double confidence, int daysSinceReinforced) => (confidence, daysSinceReinforced) switch
    {
        (>= 0.8, <= 3) => MemoryStatus.Active,
        (>= 0.8, _) => MemoryStatus.Stable,
        (>= 0.4, <= 7) => MemoryStatus.Growing,
        (>= 0.4, _) => MemoryStatus.Fading,
        (>= 0.1, _) => MemoryStatus.Weak,
        _ => MemoryStatus.Questioned
    };

    public static string Label(MemoryStatus status) => status switch
    {
        MemoryStatus.Active => "活跃",
        MemoryStatus.Stable => "稳定",
        MemoryStatus.Growing => "成长",
        MemoryStatus.Fading => "衰减",
        MemoryStatus.Weak => "微弱",
        _ => "存疑"
    };

    public static bool NearExpiry(MemoryStatus status) => status == MemoryStatus.Fading;
}

/// <summary>
/// Explicit user rating on one entry (3-step incremental semantics).
/// The server applies <see cref="Delta"/> onto the stored confidence and
/// clips to 0.05..0.98; re-tapping the selected rating sends
/// <see cref="ClearWire"/> to revoke.
/// </summary>
public enum MemoryRating
{
    Agree,
    Doubt,
    Reject
}

public static class MemoryRatings
{
    public const string AgreeWire = "agree";
    public const string DoubtWire = "doubt";
    public const string RejectWire = "reject";
    public const string ClearWire = "clear";

    public const double ConfidenceMin = 0.05;
    public const double ConfidenceMax = 0.98;

    public static string Wire(MemoryRating rating) => rating switch
    {
        MemoryRating.Agree => AgreeWire,
        MemoryRating.Doubt => DoubtWire,
        _ => RejectWire
    };

    public static string Label(MemoryRating rating) => rating switch
    {
        MemoryRating.Agree => "认可",
        MemoryRating.Doubt => "存疑",
        _ => "否认"
    };

    public static double Delta(MemoryRating rating) => rating switch
    {
        MemoryRating.Agree => 0.10,
        MemoryRating.Doubt => -0.15,
        _ => -0.35
    };

    public static MemoryRating? FromWire(string? value) => value?.ToLowerInvariant() switch
    {
        AgreeWire => MemoryRating.Agree,
        DoubtWire => MemoryRating.Doubt,
        RejectWire => MemoryRating.Reject,
        _ => null
    };
}

/// <summary>Memory category (9 kinds). Colors are mapped in the UI layer.</summary>
public enum InsightCategory
{
    BiographicalIdentity,
    CorePersonalValue,
    LongTermInterest,
    HabitPattern,
    WorkStyle,
    ProjectFocus,
    SituationalContext,
    Ephemeral,
    ExplicitInstruction
}

public static class InsightCategories
{
    public static InsightCategory? FromWire(string? value) => value?.ToLowerInvariant() switch
    {
        "biographical_identity" => InsightCategory.BiographicalIdentity,
        "core_personal_value" => InsightCategory.CorePersonalValue,
        "long_term_interest" => InsightCategory.LongTermInterest,
        "habit_pattern" => InsightCategory.HabitPattern,
        "work_style" => InsightCategory.WorkStyle,
        "project_focus" => InsightCategory.ProjectFocus,
        "situational_context" => InsightCategory.SituationalContext,
        "ephemeral" => InsightCategory.Ephemeral,
        "explicit_instruction" => InsightCategory.ExplicitInstruction,
        _ => null
    };

    public static string Label(InsightCategory category) => category switch
    {
        InsightCategory.BiographicalIdentity => "身份认知",
        InsightCategory.CorePersonalValue => "核心价值",
        InsightCategory.LongTermInterest => "长期热忱",
        InsightCategory.HabitPattern => "习惯模式",
        InsightCategory.WorkStyle => "工作风格",
        InsightCategory.ProjectFocus => "当前焦点",
        InsightCategory.SituationalContext => "即时情境",
        InsightCategory.Ephemeral => "瞬时兴趣",
        _ => "明确要求"
    };
}

/// <summary>
/// One pending candidate: extracted by the nightly shallow-sleep intake but
/// too weakly evidenced to promote. The user adjudicates.
/// </summary>
public sealed record MemoryCandidate(
    string Id,
    string Text,
    string? Quote = null,
    string? SourceChatId = null,
    long ObservedTs = 0,
    MemorySection Section = MemorySection.Context);

/// <summary>Style preferences + custom instruction, injected into system prompt.</summary>
public sealed record StylePreferences(
    IReadOnlyList<string>? Styles = null,
    string CustomInstruction = "")
{
    public IReadOnlyList<string> Styles { get; init; } = Styles ?? [];

    public bool HasStyle(ConversationStyle style) => Styles.Contains(style.Wire());

    public StylePreferences Toggled(ConversationStyle style)
    {
        var wire = style.Wire();
        return this with { Styles = Styles.Contains(wire) ? Styles.Where(s => s != wire).ToList() : Styles.Append(wire).ToList() };
    }

    public const int CustomInstructionMax = 500;
}

public enum ConversationStyle
{
    MoreDirect,
    Polite,
    Concise,
    Detailed,
    Formal,
    Casual
}

public static class ConversationStyles
{
    public static string Wire(this ConversationStyle style) => style switch
    {
        ConversationStyle.MoreDirect => "more_direct",
        ConversationStyle.Polite => "polite",
        ConversationStyle.Concise => "concise",
        ConversationStyle.Detailed => "detailed",
        ConversationStyle.Formal => "formal",
        _ => "casual"
    };

    public static string Label(this ConversationStyle style) => style switch
    {
        ConversationStyle.MoreDirect => "更直接",
        ConversationStyle.Polite => "更克制",
        ConversationStyle.Concise => "更精炼",
        ConversationStyle.Detailed => "更详细",
        ConversationStyle.Formal => "更专业",
        _ => "更轻松"
    };

    public static ConversationStyle? FromWire(string? value) => value switch
    {
        "more_direct" => ConversationStyle.MoreDirect,
        "polite" => ConversationStyle.Polite,
        "concise" => ConversationStyle.Concise,
        "detailed" => ConversationStyle.Detailed,
        "formal" => ConversationStyle.Formal,
        "casual" => ConversationStyle.Casual,
        _ => null
    };

    public static IReadOnlyList<ConversationStyle> All { get; } =
    [
        ConversationStyle.MoreDirect,
        ConversationStyle.Polite,
        ConversationStyle.Concise,
        ConversationStyle.Detailed,
        ConversationStyle.Formal,
        ConversationStyle.Casual
    ];
}

/// <summary>Result of <c>get_memory_entries</c>: entries + projection + server toggle.</summary>
public sealed record MemoryEntriesResult(
    IReadOnlyList<MemoryEntry> Entries,
    MemoryProjection Projection,
    bool MemoryEnabled);
