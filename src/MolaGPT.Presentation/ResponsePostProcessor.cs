using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MolaGPT.Presentation;

public sealed record ResponseRegexRule(string Name, string Pattern, string Replacement, bool Enabled = true);

public static class ResponsePostProcessor
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const string Cjk = @"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\u3040-\u30FF\u31F0-\u31FF\u1100-\u11FF\u3130-\u318F\uAC00-\uD7AF]";

    public static IReadOnlyList<ResponseRegexRule> DefaultRules { get; } = Array.AsReadOnly<ResponseRegexRule>(
    [
        new("中文逗号", $"(?<={Cjk}),(?={Cjk})", "，"),
        new("中文引号", $"\"(?=[^\"\\r\\n]*{Cjk})(?<text>[^\"\\r\\n]*)\"", "“${text}”")
    ]);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UsePipeTables()
        .UseGridTables()
        .UseMathematics()
        .UsePreciseSourceLocation()
        .Build();

    // These delimiters are also recognized by the renderer, but not by Markdig's math extension.
    private static readonly Regex Latex = new(
        @"\\\([\s\S]+?\\\)|\\\[[\s\S]+?\\\]|\\begin\{(?<env>equation\*?|align\*?|alignat\*?|aligned|alignedat|gather\*?|multline\*?|split|matrix|smallmatrix|pmatrix|bmatrix|Bmatrix|vmatrix|Vmatrix|cases|array)\}[\s\S]*?\\end\{\k<env>\}",
        RegexOptions.CultureInvariant, MatchTimeout);

    // The rendering splitter normalizes and merges its input; match these source ranges without changing it.
    private static readonly Regex ToolMarkup = new(
        """<(?<tag>DSanalysis|steel-step|blockquote(?=[^>]*\bclass\s*=\s*(?:"[^"]*\btool-status\b[^"]*"|'[^']*\btool-status\b[^']*')))\b[^>]*>[\s\S]*?(?:</\k<tag>\s*>|\z)|<!--(?<output>PY|MCP)_OUTPUT_BEGIN-->[\s\S]*?(?:<!--\k<output>_OUTPUT_END-->|\z)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    public static void Validate(IReadOnlyList<ResponseRegexRule> rules)
    {
        foreach (var rule in rules)
            if (rule.Enabled)
                CreateRegex(rule);
    }

    public static string Apply(
        string text,
        IReadOnlyList<ResponseRegexRule> rules,
        int startIndex = 0,
        IList<int>? offsets = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, text.Length);

        var mappedOffsets = offsets?.ToArray();
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var regex = CreateRegex(rule);
            try
            {
                var spans = ProtectedSpans(text);
                var spanIndex = 0;
                var previousOffsets = mappedOffsets?.ToArray();
                var shift = 0;
                text = regex.Replace(text, match =>
                {
                    if (match.Index < startIndex) return match.Value;
                    while (spanIndex < spans.Count && spans[spanIndex].End < match.Index)
                        spanIndex++;
                    if (spanIndex < spans.Count
                        && spans[spanIndex].Start < match.Index + Math.Max(1, match.Length))
                        return match.Value;

                    var replacement = match.Result(rule.Replacement);
                    var difference = replacement.Length - match.Length;
                    if (mappedOffsets is not null && previousOffsets is not null)
                    {
                        for (var i = 0; i < mappedOffsets.Length; i++)
                        {
                            // Keep an anchor at the opening boundary before the replacement.
                            if (previousOffsets[i] <= match.Index) continue;
                            mappedOffsets[i] = previousOffsets[i] < match.Index + match.Length
                                ? match.Index + shift + replacement.Length
                                : mappedOffsets[i] + difference;
                        }
                    }
                    shift += difference;
                    return replacement;
                });
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new RegexMatchTimeoutException($"规则 {rule.Name} 执行超时（{MatchTimeout.TotalMilliseconds:0} 毫秒）。", ex);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"规则 {rule.Name} 无法替换：{ex.Message}", ex);
            }
        }

        if (offsets is not null && mappedOffsets is not null)
            for (var i = 0; i < mappedOffsets.Length; i++)
                offsets[i] = mappedOffsets[i];
        return text;
    }

    private static Regex CreateRegex(ResponseRegexRule rule)
    {
        try
        {
            return new Regex(rule.Pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"规则 {rule.Name} 的正则表达式无效：{ex.Message}", ex);
        }
    }

    private static List<SourceSpan> ProtectedSpans(string text)
    {
        var spans = new List<SourceSpan>();
        var literalSpans = new List<SourceSpan>();
        foreach (var node in Markdown.Parse(text, Pipeline).Descendants())
        {
            switch (node)
            {
                case Markdig.Syntax.CodeBlock:
                case CodeInline:
                case MathInline:
                    spans.Add(node.Span);
                    literalSpans.Add(node.Span);
                    break;
                case HtmlBlock:
                case HtmlInline:
                case AutolinkInline:
                case LinkReferenceDefinition:
                    spans.Add(node.Span);
                    break;
                case LinkInline link:
                    if (link.IsAutoLink || link.IsShortcut)
                    {
                        spans.Add(link.Span);
                        break;
                    }
                    if (link.UrlSpan is { } urlSpan)
                        spans.Add(urlSpan);
                    if (link.Title is not null && link.TitleSpan is { Length: > 0 } titleSpan)
                        spans.Add(titleSpan);
                    if (link.Reference is not null && link.LabelSpan is { } labelSpan)
                        spans.Add(labelSpan);
                    break;
            }
        }

        foreach (Match match in Latex.Matches(text))
            if (!spans.Any(span => span.Start <= match.Index && match.Index <= span.End))
                spans.Add(new SourceSpan(match.Index, match.Index + match.Length - 1));

        foreach (Match match in ToolMarkup.Matches(text))
            if (!literalSpans.Any(span => span.Start <= match.Index && match.Index <= span.End))
                spans.Add(new SourceSpan(match.Index, match.Index + match.Length - 1));

        spans.Sort((left, right) => left.Start.CompareTo(right.Start));
        return spans;
    }
}
