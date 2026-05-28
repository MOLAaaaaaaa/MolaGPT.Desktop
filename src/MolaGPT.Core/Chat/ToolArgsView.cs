using System.Text.Json;

namespace MolaGPT.Core.Chat;

/// <summary>
/// View-projection of a tool call's arguments, derived from
/// <see cref="ToolCallDelta.ArgumentsJson"/>. The UI binds to the populated
/// list/property and ignores the others — exactly one shape is non-empty for
/// a given args payload.
///
/// Shapes, in priority order:
///   1. SearchQueries  — search_web only; chips with optional topic badge.
///   2. PrimaryArg     — single highlight chip when args has a url/path/text key.
///   3. KeyValueArgs   — fallback. Up to 3 top-level scalar key-value pairs;
///                       any additional keys are counted in KeyValueOverflow.
///   4. IsEmpty        — args is empty / unparseable / no scalar fields.
/// </summary>
public sealed record ToolArgsView(
    IReadOnlyList<ToolSearchQueryView>? SearchQueries,
    ToolPrimaryArgView? PrimaryArg,
    IReadOnlyList<ToolKeyValueView>? KeyValueArgs,
    int KeyValueOverflow)
{
    public bool HasSearchQueries => SearchQueries is { Count: > 0 };
    public bool HasPrimaryArg    => PrimaryArg is not null;
    public bool HasKeyValueArgs  => KeyValueArgs is { Count: > 0 };
    public bool HasOverflow      => KeyValueOverflow > 0;
    public bool IsEmpty => !HasSearchQueries && !HasPrimaryArg && !HasKeyValueArgs;

    public bool IsUrlPrimary  => PrimaryArg?.Kind == ToolPrimaryArgKind.Url;
    public bool IsPathPrimary => PrimaryArg?.Kind == ToolPrimaryArgKind.Path;
    public bool IsTextPrimary => PrimaryArg?.Kind == ToolPrimaryArgKind.Text;

    public static ToolArgsView Empty { get; } = new(null, null, null, 0);
}

public sealed record ToolSearchQueryView(string Text, string? Topic)
{
    public bool HasTopic => !string.IsNullOrWhiteSpace(Topic);
}

public enum ToolPrimaryArgKind
{
    Url,
    Path,
    Text
}

public sealed record ToolPrimaryArgView(ToolPrimaryArgKind Kind, string Value, string? Badge)
{
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
}

public sealed record ToolKeyValueView(string Key, string Value, bool IsMono);
