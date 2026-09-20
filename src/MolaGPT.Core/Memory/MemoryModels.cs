using MemorySection = MolaGPT.Core.Personalization.MemorySection;
using MemorySections = MolaGPT.Core.Personalization.MemorySections;

namespace MolaGPT.Core.Memory;

/// <summary>
/// Where an entry came from, which decides what may rewrite it.
/// <see cref="Manual"/> is the user's own hand — including a bare line he typed
/// into MEMORY.md with no metadata at all — and automatic learning is never
/// allowed to touch it. <see cref="Confirmed"/> is model-extracted and
/// user-approved: as trustworthy as manual, but it must not be labelled
/// 「手动添加」, which would be telling the user he wrote something he did not.
/// </summary>
public enum MemoryOrigin
{
    Manual,
    Confirmed,
    Tool,
    Auto
}

/// <summary>
/// Section rules for the local memory file. The five sections themselves are
/// <see cref="MemorySection"/> — the same enum and the same Chinese headings the
/// server's Tracks page uses — so one entry reads the same way whichever end is
/// showing it. Only the parts that are specific to a file on this machine live
/// here.
/// </summary>
public static class MemorySectionRules
{
    public static IReadOnlyList<MemorySection> All => MemorySections.Ordered;

    public static string Heading(MemorySection section) => MemorySections.Wire(section);

    /// <summary>
    /// Strict, unlike <c>MemorySections.FromWire</c>, which folds anything it
    /// does not know into 近期上下文. A heading we do not recognise in MEMORY.md is
    /// the user's own prose, and its bullets must be left alone rather than
    /// quietly adopted as memories.
    /// </summary>
    public static bool TryParse(string? heading, out MemorySection section)
    {
        section = MemorySection.Identity;
        if (string.IsNullOrWhiteSpace(heading)) return false;
        var text = heading.Trim();
        foreach (var candidate in MemorySections.Ordered)
        {
            if (!string.Equals(MemorySections.Wire(candidate), text, StringComparison.Ordinal)) continue;
            section = candidate;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Half-life in days for confidence decay, or null for sections that do not
    /// decay. Identity facts and explicit prohibitions hold until the user says
    /// otherwise; 近期上下文 is the one section that is supposed to fade, which is
    /// what stops it from silently becoming a second 身份与背景.
    /// </summary>
    public static double? HalfLifeDays(MemorySection section) => section switch
    {
        MemorySection.Identity => null,
        MemorySection.Prohibition => null,
        MemorySection.Preference => 365,
        MemorySection.Project => 180,
        MemorySection.Context => 45,
        _ => 365
    };
}

/// <summary>
/// One line of MEMORY.md. <see cref="LineIndex"/> is the anchor every write goes
/// through: edits are surgical line replacements against a file the user may
/// have edited by hand a second ago, not a parse-mutate-render round trip that
/// would reformat everything around them.
/// </summary>
public sealed record MemoryEntry
{
    public required string Id { get; init; }
    public required MemorySection Section { get; init; }
    public required string Text { get; init; }
    public double Confidence { get; init; } = 1;
    public MemoryOrigin Origin { get; init; } = MemoryOrigin.Manual;
    public DateOnly? LastReinforced { get; init; }
    public string? ProfileKey { get; init; }
    public string? TopicId { get; init; }
    public int LineIndex { get; init; }

    /// <summary>Hand-written entries never decay and are never rewritten by the
    /// learning path.</summary>
    public bool IsPermanent => Origin is MemoryOrigin.Manual or MemoryOrigin.Confirmed
        || MemorySectionRules.HalfLifeDays(Section) is null;

    public double EffectiveConfidence(DateOnly today)
    {
        if (IsPermanent) return Math.Clamp(Confidence, 0, 1);
        if (MemorySectionRules.HalfLifeDays(Section) is not { } days || days <= 0)
            return Math.Clamp(Confidence, 0, 1);
        var age = LastReinforced is { } last ? today.DayNumber - last.DayNumber : 0;
        if (age <= 0) return Math.Clamp(Confidence, 0, 1);
        return Math.Clamp(Confidence * Math.Pow(0.5, age / days), 0, 1);
    }
}

/// <summary>A parsed MEMORY.md plus the raw text it came from and the write
/// stamp it was read at. Both are needed to write back safely.</summary>
public sealed record MemoryDocument(
    string RawText,
    IReadOnlyList<MemoryEntry> Entries,
    DateTime LastWriteUtc)
{
    public static MemoryDocument Empty { get; } =
        new(string.Empty, Array.Empty<MemoryEntry>(), DateTime.MinValue);
}

/// <summary>
/// profile.md. Deliberately a handful of named fields rather than free text: the
/// model may only tag an entry with one of these keys (enum-constrained), and
/// the block is assembled locally, so nothing it writes can restructure the
/// user's own profile.
/// </summary>
public sealed record MemoryProfile(IReadOnlyDictionary<string, string> Fields)
{
    public const string PreferredName = "preferred_name";
    public const string Occupation = "occupation";
    public const string Location = "location";
    public const string Language = "language";

    public static readonly string[] Keys = [PreferredName, Occupation, Location, Language];

    public static MemoryProfile Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public string? Get(string key) => Fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value.Trim()
        : null;

    public static bool IsKnownKey(string? key) =>
        key is not null && Keys.Contains(key, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What one projection actually sent, so the memory page can show the
/// same numbers instead of a second estimate that disagrees with it.</summary>
public sealed record MemoryProjection(string Text, int Injected, int Skipped)
{
    public static MemoryProjection Empty { get; } = new(string.Empty, 0, 0);
    public bool IsEmpty => string.IsNullOrEmpty(Text);
}
