using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MolaGPT.Presentation;

/// <summary>Source spans for Markdown literals and image destinations, as recognized by the existing parser.</summary>
public static class MarkdownSource
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation().Build();

    public static IReadOnlyList<SourceSpan> LiteralSpans(string source)
    {
        if (source.IndexOfAny(['`', '~', '>']) < 0) return [];
        return Markdown.Parse(source, Pipeline).Descendants()
            .Where(node => node is Markdig.Syntax.CodeBlock or CodeInline or Markdig.Syntax.QuoteBlock
                || node is HtmlBlock or HtmlInline && node.Span.Start >= 0 && node.Span.Start < source.Length
                && source.AsSpan(node.Span.Start).StartsWith("<!--", StringComparison.Ordinal)
                && !source.AsSpan(node.Span.Start).StartsWith("<!--PY_OUTPUT_", StringComparison.Ordinal)
                && !source.AsSpan(node.Span.Start).StartsWith("<!--MCP_OUTPUT_", StringComparison.Ordinal))
            // Markdig 0.22 reports only the opening line for an unclosed fence.
            .Select(node => node is FencedCodeBlock { IsOpen: true }
                ? new SourceSpan(node.Span.Start, source.Length - 1) : node.Span)
            .OrderBy(span => span.Start).ToArray();
    }

    public static string RewriteImages(string source, Func<string, string?> resolve, IList<int>? positions = null)
    {
        if (!source.Contains("![", StringComparison.Ordinal)) return source;
        var changes = new Dictionary<int, (SourceSpan Span, string Destination)>();
        foreach (var image in Markdown.Parse(source, Pipeline).Descendants<LinkInline>().Where(link => link.IsImage))
        {
            if (string.IsNullOrWhiteSpace(image.Url) || resolve(image.Url) is not { } destination) continue;
            if ((image.Reference?.UrlSpan ?? image.UrlSpan) is not { } span
                || span.Start < 0 || span.Length <= 0 || span.End >= source.Length) continue;
            changes.TryAdd(span.Start, (span, destination));
        }
        if (changes.Count == 0) return source;

        var result = new StringBuilder(source);
        foreach (var (span, destination) in changes.Values.OrderByDescending(change => change.Span.Start))
        {
            result.Remove(span.Start, span.Length).Insert(span.Start, destination);
            if (positions is null) continue;
            for (var i = 0; i < positions.Count; i++)
                positions[i] = positions[i] > span.End ? positions[i] + destination.Length - span.Length
                    : positions[i] >= span.Start ? span.Start : positions[i];
        }
        return result.ToString();
    }
}
