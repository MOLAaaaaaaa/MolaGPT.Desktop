using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MolaGPT.Presentation;

namespace MolaGPT.App.Rendering;

internal static partial class InlineMath
{
    public const string PlaceholderPrefix = "\uE000MolaMath";
    public const string PlaceholderSuffix = "\uE001";

    [GeneratedRegex(@"(?<![\\$])\$(?![$\s])(?<dollar>[^$\r\n]*?[^$\r\n\s])(?<![\\$])\$(?![$\d])|(?<!\\)\\\((?<paren>[\s\S]+?)(?<!\\)\\\)")]
    private static partial Regex Pattern();
    private static readonly MarkdownPipeline SourcePipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation().DisableHtml().Build();

    public static bool MayContain(string source) =>
        source.Contains('$') || source.Contains(@"\(", StringComparison.Ordinal)
        || source.Contains(@"\[", StringComparison.Ordinal) || source.Contains(@"\begin{", StringComparison.Ordinal);

    private readonly record struct FormulaSpan(int Start, int Length, string Formula);

    private static IReadOnlyList<FormulaSpan> Find(string source)
    {
        var spans = LatexDisplayParser.Find(source)
            .Select(span => new FormulaSpan(span.Start, span.Length, source.Substring(span.FormulaStart, span.FormulaLength).Trim()))
            .Concat(Pattern().Matches(source).Select(match => new FormulaSpan(match.Index, match.Length,
                (match.Groups["dollar"].Success ? match.Groups["dollar"].Value : match.Groups["paren"].Value).Trim())))
            .OrderBy(span => span.Start).ThenByDescending(span => span.Length);
        var protectedSpans = new List<SourceSpan>();
        foreach (var node in Markdown.Parse(source, SourcePipeline).Descendants())
        {
            if (node is Markdig.Syntax.CodeBlock or CodeInline) protectedSpans.Add(node.Span);
            if (node is LinkInline link && (link.Reference?.UrlSpan ?? link.UrlSpan) is { } target)
                protectedSpans.Add(target);
        }
        var result = new List<FormulaSpan>();
        var end = 0;
        foreach (var span in spans)
        {
            if (span.Start < end || protectedSpans.Any(literal => literal.Start <= span.Start && span.Start <= literal.End))
                continue;
            result.Add(span);
            end = span.Start + span.Length;
        }
        return result;
    }

    public static string Protect(string source, out IReadOnlyDictionary<string, string>? formulas)
    {
        formulas = null;
        if (!MayContain(source)) return source;
        var matches = Find(source);
        if (matches.Count == 0) return source;

        var protectedFormulas = new Dictionary<string, string>();
        var builder = new StringBuilder(source.Length);
        var last = 0;
        foreach (var match in matches)
        {
            if (match.Formula.Length == 0) continue;
            var placeholder = PlaceholderPrefix + protectedFormulas.Count + PlaceholderSuffix;
            protectedFormulas.Add(placeholder, match.Formula);
            builder.Append(source, last, match.Start - last);
            builder.Append(placeholder);
            last = match.Start + match.Length;
        }
        if (protectedFormulas.Count == 0) return source;
        builder.Append(source, last, source.Length - last);
        formulas = protectedFormulas;
        return builder.ToString();
    }
}
