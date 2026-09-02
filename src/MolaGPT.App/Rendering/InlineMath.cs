using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Finds LaTeX inside a run of prose. Top-level display math is normally split
/// into a MathBlock earlier; the display forms remain here for quotes, table
/// cells and other nested Markdown content.
///
/// The pattern is carried over verbatim from MarkdownPresenter, guards and all,
/// because getting it wrong is worse than not having it. The two that matter:
///
///   <c>(?!\s)</c> after the opening <c>$</c> and <c>(?!\d)</c> after the
///   closing one are what stop a pair of prices from being read as a formula.
///   In "costs $12 and $238", the first <c>$</c> would otherwise pair with the
///   one before 238 and swallow everything between them — including, in a table,
///   the <c>|</c> cell separators.
///
///   <c>(?&lt;!\\)</c> on both delimiters lets an author write a literal
///   <c>\$</c>.
/// </summary>
internal static partial class InlineMath
{
    public const string PlaceholderPrefix = "\uE000MolaMath";
    public const string PlaceholderSuffix = "\uE001";

    [GeneratedRegex(@"(?<!\\)\$\$(?<displayDollar>[\s\S]+?)(?<!\\)\$\$|(?<![\\$])\$(?![$\s])(?<dollar>[^$\r\n]*?[^$\r\n\s])(?<![\\$])\$(?![$\d])|\\\((?<paren>[\s\S]+?)\\\)|\\\[(?<bracket>[\s\S]+?)\\\]|(?<environment>\\begin\{(?<env>equation\*?|align\*?|alignat\*?|aligned|alignedat|gather\*?|multline\*?|split|matrix|smallmatrix|pmatrix|bmatrix|Bmatrix|vmatrix|Vmatrix|cases|array)\}[\s\S]*?\\end\{\k<env>\})")]
    private static partial Regex Pattern();

    /// <summary>Cheap pre-filter: the regex is only worth running on text that
    /// contains a delimiter at all, and most prose does not.</summary>
    public static bool MayContain(string source) =>
        source.IndexOf('$') >= 0
        || source.Contains(@"\(", StringComparison.Ordinal)
        || source.Contains(@"\[", StringComparison.Ordinal)
        || source.Contains(@"\begin{", StringComparison.Ordinal);

    public static IReadOnlyList<Match>? Find(string source)
    {
        if (!MayContain(source)) return null;

        var matches = Pattern().Matches(source);
        if (matches.Count == 0) return null;

        var codeSpans = FindCodeSpans(source);
        return matches
            .Cast<Match>()
            .Where(match => !codeSpans.Any(span => match.Index >= span.Start && match.Index < span.End))
            .ToArray();
    }

    /// <summary>
    /// Markdig treats the backslashes in <c>\(...\)</c> and <c>\[...\]</c> as
    /// Markdown escapes. Preserve every formula as plain private-use text until
    /// the inline tree has been built, then let MarkdownTextBlock replace the
    /// token with a MathView.
    /// </summary>
    public static string Protect(string source, out IReadOnlyDictionary<string, string>? formulas)
    {
        formulas = null;
        if (!MayContain(source)) return source;

        var matches = Find(source);
        if (matches is null || matches.Count == 0) return source;

        var protectedFormulas = new Dictionary<string, string>();
        var builder = new StringBuilder(source.Length);
        var last = 0;
        var index = 0;

        foreach (Match match in matches)
        {
            var formula = Formula(match);
            if (formula.Length == 0) continue;

            var placeholder = PlaceholderPrefix + index++ + PlaceholderSuffix;
            protectedFormulas[placeholder] = formula;
            builder.Append(source, last, match.Index - last);
            builder.Append(placeholder);
            last = match.Index + match.Length;
        }

        if (protectedFormulas.Count == 0) return source;

        builder.Append(source, last, source.Length - last);
        formulas = protectedFormulas;
        return builder.ToString();
    }

    /// <summary>The formula body for a match, or empty when the match carried
    /// no usable group — in which case the caller emits the source text.</summary>
    public static string Formula(Match match)
    {
        if (match.Groups["displayDollar"].Success) return match.Groups["displayDollar"].Value.Trim();
        if (match.Groups["dollar"].Success) return match.Groups["dollar"].Value.Trim();
        if (match.Groups["paren"].Success) return match.Groups["paren"].Value.Trim();
        if (match.Groups["bracket"].Success) return match.Groups["bracket"].Value.Trim();
        if (match.Groups["environment"].Success) return match.Groups["environment"].Value.Trim();
        return string.Empty;
    }

    private static IReadOnlyList<(int Start, int End)> FindCodeSpans(string source)
    {
        var spans = new List<(int Start, int End)>();
        for (var start = source.IndexOf('`'); start >= 0; start = source.IndexOf('`', start + 1))
        {
            var openingLength = 1;
            while (start + openingLength < source.Length && source[start + openingLength] == '`')
                openingLength++;

            for (var cursor = start + openingLength; cursor < source.Length;)
            {
                var close = source.IndexOf('`', cursor);
                if (close < 0) return spans;

                var closingLength = 1;
                while (close + closingLength < source.Length && source[close + closingLength] == '`')
                    closingLength++;

                if (closingLength == openingLength)
                {
                    spans.Add((start, close + closingLength));
                    start = close + closingLength - 1;
                    break;
                }

                cursor = close + closingLength;
            }
        }

        return spans;
    }
}
