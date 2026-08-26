using System.Text;

namespace MolaGPT.App.Rendering;

/// <summary>
/// The mixed-script typography rules message content is rendered under, ported
/// from MarkdownPresenter's <c>ApplyWebFontFallback</c> and
/// <c>ApplyCjkPunctuation</c>.
///
/// Two things happen to every non-monospace run of prose:
///
///   1. It is cut at each Latin↔CJK boundary so each piece can be given the
///      face that was designed for it. Geist has no CJK coverage, so leaving a
///      mixed run on one family means the shaper picks the substitute, and which
///      substitute it picks is not the same choice the WPF build made. Splitting
///      explicitly is what keeps the two builds looking alike.
///
///   2. ASCII punctuation sitting next to CJK is promoted to its full-width
///      form. A comma between two Chinese characters is a 、-width glyph in
///      typeset Chinese; leaving it half-width is the single most visible tell
///      that text was laid out by a Latin-first renderer.
///
/// Both rules skip inline code, where the author's exact bytes are the point.
/// </summary>
internal static class CjkTypography
{
    /// <summary>
    /// Splits at Latin↔CJK boundaries. Returns one entry per homogeneous piece,
    /// in order; a run with no boundary yields a single entry.
    /// </summary>
    public static List<(string Text, bool IsCjk)> SplitByScript(string text)
    {
        var result = new List<(string, bool)>();
        if (text.Length == 0) return result;

        var start = 0;
        var currentCjk = ShouldUseCjkFont(text[0]);

        for (var i = 1; i < text.Length; i++)
        {
            var nextCjk = ShouldUseCjkFont(text[i]);
            if (nextCjk == currentCjk) continue;

            result.Add((text[start..i], currentCjk));
            start = i;
            currentCjk = nextCjk;
        }

        result.Add((text[start..], currentCjk));
        return result;
    }

    /// <summary>
    /// Promotes ASCII punctuation that has CJK on either side to its full-width
    /// form. Quotes alternate open/close across the string, which is why this
    /// cannot be done character-by-character from the caller.
    /// </summary>
    public static string NormalizePunctuation(string text)
    {
        if (text.Length == 0 || !MayNeedPunctuation(text)) return text;

        var builder = new StringBuilder(text.Length);
        var doubleQuoteOpen = true;
        var singleQuoteOpen = true;
        var changed = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var replacement = ch switch
            {
                ',' when HasCjkAround(text, i) => '，',
                ':' when HasCjkAround(text, i) => '：',
                ';' when HasCjkAround(text, i) => '；',
                '?' when HasCjkAround(text, i) => '？',
                '!' when HasCjkAround(text, i) => '！',
                '(' when HasCjkAround(text, i) => '（',
                ')' when HasCjkAround(text, i) => '）',
                '"' when HasCjkAround(text, i) => ConsumeQuote(ref doubleQuoteOpen, '“', '”'),
                '\'' when HasCjkAround(text, i) => ConsumeQuote(ref singleQuoteOpen, '‘', '’'),
                '.' when ShouldUseCjkPeriod(text, i) => '。',
                _ => ch
            };

            if (replacement != ch) changed = true;
            builder.Append(replacement);
        }

        return changed ? builder.ToString() : text;
    }

    private static bool MayNeedPunctuation(string text)
    {
        if (text.IndexOfAny([',', '.', ':', ';', '?', '!', '(', ')', '"', '\'']) < 0) return false;
        foreach (var ch in text)
        {
            if (IsCjkTextChar(ch)) return true;
        }
        return false;
    }

    private static char ConsumeQuote(ref bool open, char openChar, char closeChar)
    {
        var result = open ? openChar : closeChar;
        open = !open;
        return result;
    }

    /// <summary>
    /// A period only becomes 。 when neither neighbour is alphanumeric. Without
    /// that guard "3.5" and "config.json" inside a Chinese sentence get rewritten.
    /// </summary>
    private static bool ShouldUseCjkPeriod(string text, int index)
    {
        if (!HasCjkAround(text, index)) return false;

        var previous = PreviousMeaningful(text, index);
        var next = NextMeaningful(text, index);
        return !IsAsciiLetterOrDigit(previous) && !IsAsciiLetterOrDigit(next);
    }

    private static bool HasCjkAround(string text, int index) =>
        IsCjkTextChar(PreviousMeaningful(text, index))
        || IsCjkTextChar(NextMeaningful(text, index));

    private static char PreviousMeaningful(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i])) return text[i];
        }
        return '\0';
    }

    private static char NextMeaningful(string text, int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) return text[i];
        }
        return '\0';
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        ch is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>Ranges that decide which font a character is drawn with. Wider
    /// than <see cref="IsCjkTextChar"/>: smart quotes and the ellipsis are Latin
    /// codepoints that still want the CJK face when they sit in CJK copy.</summary>
    private static bool ShouldUseCjkFont(char ch) =>
        ch is >= '⺀' and <= '⻿'   // CJK radicals
        or >= '‘' and <= '‟'      // CJK-style smart quotes
        or '…'                         // Chinese ellipsis
        or >= '　' and <= '〿'      // CJK punctuation: 、。「」
        or >= '぀' and <= 'ヿ'      // kana
        or >= '㄀' and <= 'ㄯ'      // bopomofo
        or >= '㐀' and <= '䶿'      // CJK extension A
        or >= '一' and <= '鿿'      // CJK unified ideographs
        or >= '豈' and <= '﫿'      // CJK compatibility ideographs
        or >= '︐' and <= '﹏'      // vertical / compatibility forms
        or >= '＀' and <= '￯';     // full-width punctuation / forms

    /// <summary>Ranges that count as "there is CJK here" for the punctuation
    /// rule. Excludes the Latin codepoints above, which must not by themselves
    /// make a neighbouring comma full-width.</summary>
    private static bool IsCjkTextChar(char ch) =>
        ch is >= '⺀' and <= '⻿'
        or >= '　' and <= '〿'
        or >= '぀' and <= 'ヿ'
        or >= '㄀' and <= 'ㄯ'
        or >= '㐀' and <= '䶿'
        or >= '一' and <= '鿿'
        or >= '豈' and <= '﫿'
        or >= '︐' and <= '﹏'
        or >= '＀' and <= '￯';
}
