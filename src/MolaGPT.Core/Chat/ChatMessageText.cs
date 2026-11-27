using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat;

public static partial class ChatMessageText
{
    [GeneratedRegex("✝[^✝]*✝")]
    private static partial Regex DaggerWrappedTokenRegex();

    public static string RemoveSystemHints(string? content) =>
        string.IsNullOrEmpty(content) ? string.Empty : DaggerWrappedTokenRegex().Replace(content, string.Empty);

    public static string StripSystemHints(string? content, bool preserveLeadingWhitespace = false)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var stripped = RemoveSystemHints(content);
        return preserveLeadingWhitespace ? stripped.TrimEnd() : stripped.Trim();
    }

    public static InlineThinkingParts SplitInlineThinking(string? content)
    {
        if (string.IsNullOrEmpty(content)) return new InlineThinkingParts(string.Empty, null);
        if (!content.Contains("<think", StringComparison.OrdinalIgnoreCase))
            return new InlineThinkingParts(content, null);
        var splitter = new InlineThinkSplitter();
        var result = splitter.Feed(content);
        var tail = splitter.Flush();
        var thinking = (result.Thinking + tail.Thinking).Trim();
        return new InlineThinkingParts(result.Visible + tail.Visible, thinking.Length == 0 ? null : thinking);
    }
}

public sealed record InlineThinkingParts(string Visible, string? Thinking);
