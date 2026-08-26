using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Finds inline LaTeX inside a run of prose — <c>$…$</c>, <c>\(…\)</c> and
/// <c>\[…\]</c>.
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

    [GeneratedRegex(@"(?<!\\)\$(?!\s)(?<dollar>[^$\r\n]*?[^$\r\n\s])(?<!\\)\$(?!\d)|\\\((?<paren>[\s\S]+?)\\\)|\\\[(?<bracket>[\s\S]+?)\\\]")]
    private static partial Regex Pattern();

    /// <summary>Cheap pre-filter: the regex is only worth running on text that
    /// contains a delimiter at all, and most prose does not.</summary>
    public static bool MayContain(string source) =>
        source.IndexOf('$') >= 0
        || source.Contains(@"\(", StringComparison.Ordinal)
        || source.Contains(@"\[", StringComparison.Ordinal);

    public static MatchCollection? Find(string source) =>
        MayContain(source) ? Pattern().Matches(source) : null;

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

        var matches = Pattern().Matches(source);
        if (matches.Count == 0) return source;

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
        if (match.Groups["dollar"].Success) return match.Groups["dollar"].Value.Trim();
        if (match.Groups["paren"].Success) return match.Groups["paren"].Value.Trim();
        if (match.Groups["bracket"].Success) return match.Groups["bracket"].Value.Trim();
        return string.Empty;
    }
}
