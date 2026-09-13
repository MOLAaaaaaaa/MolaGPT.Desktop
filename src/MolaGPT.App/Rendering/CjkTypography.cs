namespace MolaGPT.App.Rendering;

/// <summary>
/// Splits mixed-script prose into runs for Latin and CJK font fallback.
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

}
