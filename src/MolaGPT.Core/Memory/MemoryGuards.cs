using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Memory;

/// <summary>
/// Content rules shared by the tool path and the automatic-learning path.
/// One set, on purpose: two copies would turn "what can actually get written"
/// into a question nobody can answer.
/// </summary>
public static partial class MemoryGuards
{
    public const int MaxEntryCharacters = 300;

    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{16,}|ghp_[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_\-]{30,}|xox[baprs]-[A-Za-z0-9\-]{10,})")]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"(密码|口令|password|passwd|api[_\s-]?key|secret[_\s-]?key|access[_\s-]?token|私钥|助记词)\s*[:：=]\s*\S+",
        RegexOptions.IgnoreCase)]
    private static partial Regex LabelledSecretRegex();

    [GeneratedRegex(@"(\b\d{17}[\dXx]\b|\b\d{15}\b|\b\d{16,19}\b)")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(ignore\s+(all\s+)?previous\s+instructions|disregard\s+the\s+above|忘记你之前的|忽略(以上|上面|之前)的?(指令|提示)|你现在是|system\s*prompt\s*[:：])",
        RegexOptions.IgnoreCase)]
    private static partial Regex InjectionRegex();

    /// <summary>
    /// Words that make a sentence a denial or a deletion request. `forget` needs
    /// one of these in the user's own words: a verbatim quote only proves the
    /// user said something, not that he asked for this memory to go, and
    /// deleting is irreversible and writes a suppression on top.
    /// </summary>
    private static readonly string[] DenialMarkers =
    [
        "不是", "不对", "不要记", "别记", "忘记", "忘掉", "删掉", "删除", "去掉", "取消",
        "改成", "换成", "已经不", "不再", "错了", "搞错", "不准确",
        "forget", "delete", "remove", "not true", "no longer", "incorrect", "wrong"
    ];

    /// <summary>
    /// Deduplication key: punctuation, whitespace and case removed. Also the
    /// key suppressions are recorded under, so "用户住在上海。" and
    /// "用户住在上海" cannot come back as two different facts.
    /// </summary>
    public static string NormalizeKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    public static bool IsSensitive(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (CredentialRegex().IsMatch(text)
            || LabelledSecretRegex().IsMatch(text)
            || IdentifierRegex().IsMatch(text));

    public static bool LooksLikeInjection(string? text) =>
        !string.IsNullOrWhiteSpace(text) && InjectionRegex().IsMatch(text);

    public static bool LooksLikeDenial(string? quote) =>
        !string.IsNullOrWhiteSpace(quote)
        && DenialMarkers.Any(marker => quote.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a quote really is the user's own words from this turn. Compared
    /// with whitespace collapsed, because a model that re-wraps a long line is
    /// still quoting; anything beyond that is not.
    /// </summary>
    public static bool QuoteBelongsTo(string? quote, string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(userMessage)) return false;
        if (userMessage.Contains(quote, StringComparison.Ordinal)) return true;
        return Collapse(userMessage).Contains(Collapse(quote), StringComparison.Ordinal);
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Why this text may not be stored, or null when it may.</summary>
    public static string? RejectionReason(string? text, bool allowSensitive)
    {
        if (string.IsNullOrWhiteSpace(text)) return "记忆内容不能为空。";
        var trimmed = text.Trim();
        if (trimmed.Length > MaxEntryCharacters) return $"单条记忆不能超过 {MaxEntryCharacters} 字。";
        if (LooksLikeInjection(trimmed)) return "检测到指令性内容，无法写入记忆。";
        if (!allowSensitive && IsSensitive(trimmed)) return "内容包含凭据或证件信息，无法写入记忆。";
        return null;
    }
}
