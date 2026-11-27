using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat;

public static class ConversationTitleText
{
    private static readonly Regex ClosedThinkTag = new(
        @"<think(?:ing)?>.*?</think(?:ing)?>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex OpenThinkTag = new(
        @"<think(?:ing)?>.*",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex EdgeMarkers = new(
        "^[\"'“”‘’「」『』《》*#\\s]+|[\"'“”‘’「」『』《》*\\s]+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TitlePrefix = new(
        @"^(标题|title)\s*[:：]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HttpError = new(
        @"HTTP\s*[45]\d\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string GenerateTitle(string? content)
    {
        content = (content ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        content = ChatMessageText.StripSystemHints(content);

        var title = content.Length <= 25 ? content : content[..25].Trim() + "...";
        return string.IsNullOrWhiteSpace(title) ? "无标题对话" : title;
    }

    public static string? CleanGeneratedTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var withoutThinking = OpenThinkTag.Replace(ClosedThinkTag.Replace(raw, " "), " ");
        var title = withoutThinking
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (string.IsNullOrWhiteSpace(title) || LooksLikeErrorPayload(title)) return null;

        title = EdgeMarkers.Replace(title, string.Empty);
        title = TitlePrefix.Replace(title, string.Empty).Trim();
        if (LooksLikeErrorPayload(title)) return null;
        title = title.Trim('。', '，', ',', '.', ':', '：', ';', '；', '!', '！', '?', '？');
        if (title.Length > 30) title = title[..30].Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static bool LooksLikeErrorPayload(string text) =>
        text.StartsWith('{')
        || text.StartsWith('[')
        || text.StartsWith('<')
        || HttpError.IsMatch(text);
}
