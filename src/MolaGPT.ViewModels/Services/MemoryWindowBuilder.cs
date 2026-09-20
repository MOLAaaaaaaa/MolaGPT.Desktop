using System.Text.RegularExpressions;
using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels.Services;

/// <summary>One window of a conversation handed to the consolidation model, plus
/// how far through the conversation it looked.</summary>
public sealed record MemoryWindow(
    IReadOnlyList<MessageRow> Messages,
    long SeenThrough)
{
    public bool HasContent => Messages.Count > 0;
    public IEnumerable<MessageRow> UserMessages =>
        Messages.Where(row => row.Role == ChatMessage.RoleUser);
}

/// <summary>
/// Builds the window locally, before anything is sent anywhere — the free half
/// of consolidation. Most windows die here (nothing new, or nothing but code),
/// and every one that does costs no tokens at all.
/// </summary>
public static partial class MemoryWindowBuilder
{
    /// <summary>Messages per window. One request sees one window.</summary>
    public const int WindowSize = 40;

    /// <summary>Messages the first window of an old conversation looks back
    /// over under 近期内容. Everything earlier is written off deliberately: the
    /// alternative is a first run that bills the user for a year of history.</summary>
    public const int RecentOnlyTail = 20;

    /// <summary>
    /// Four characters, not the eight an English-language project would pick.
    /// 「我住在上海」 is five characters and is exactly the kind of fact worth
    /// keeping; an eight-character floor would drop it.
    /// </summary>
    private const int MinimumUsefulLength = 4;

    [GeneratedRegex(@"```[\s\S]*?```|`[^`\n]*`", RegexOptions.None)]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.None)]
    private static partial Regex UrlRegex();

    /// <summary>
    /// The next window for one conversation.
    ///
    /// Two rules earn their keep here. The window stops before a turn that is
    /// still being written, so a reply that lands a second later does not end up
    /// below the watermark and never get read. And the watermark advances over
    /// everything *looked at*, not everything *kept*: otherwise a run of
    /// code-only messages stays in the queue forever, taking a slot in every
    /// scan from a conversation that has something to say.
    /// </summary>
    public static MemoryWindow Build(
        IReadOnlyList<MessageRow> history,
        long watermark,
        long resetAt,
        bool recentOnly)
    {
        var pending = history
            .Where(row => row.CreatedAt > watermark && row.CreatedAt >= resetAt)
            .Where(row => row.Role is ChatMessage.RoleUser
                or ChatMessage.RoleAssistant)
            .OrderBy(row => row.CreatedAt)
            .ToList();

        if (pending.Count == 0) return new MemoryWindow(Array.Empty<MessageRow>(), watermark);

        // 近期内容 on a conversation nobody has consolidated before: read the tail
        // and write off the rest in one move, rather than walking years of
        // history a window at a time.
        if (recentOnly && watermark == 0 && pending.Count > RecentOnlyTail)
            pending = pending.Skip(pending.Count - RecentOnlyTail).ToList();

        var slice = pending.Take(WindowSize).ToList();

        // Cut back to the last assistant message: a trailing user message is a
        // question whose answer has not been written yet, and splitting the two
        // across windows loses the pair.
        var lastAssistant = slice.FindLastIndex(row => row.Role == ChatMessage.RoleAssistant);
        if (lastAssistant < 0) return new MemoryWindow(Array.Empty<MessageRow>(), watermark);
        slice = slice.Take(lastAssistant + 1).ToList();

        var seenThrough = slice[^1].CreatedAt;
        var kept = slice
            .Select(row => row with { Content = Strip(row.Content) })
            .Where(row => row.Content.Length >= MinimumUsefulLength)
            .ToList();

        return new MemoryWindow(kept, seenThrough);
    }

    /// <summary>
    /// Code fences, inline code and links out. They are the bulk of a technical
    /// conversation and none of them is ever a fact about the user.
    /// </summary>
    public static string Strip(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var text = CodeRegex().Replace(content, " ");
        text = UrlRegex().Replace(text, " ");
        return text.Trim();
    }
}
