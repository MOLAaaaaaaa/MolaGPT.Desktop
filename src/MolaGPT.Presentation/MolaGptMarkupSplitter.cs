using System.Net;
using System.Text.RegularExpressions;

// Pure string processing with no UI dependency. The renderer can run it on the
// background thread that builds a RenderDocument.
namespace MolaGPT.Presentation;

/// <summary>
/// Splits a MolaGPT-style assistant message into a sequence of
/// <see cref="MarkupUnit"/>s before any Markdig parsing.
///
/// MolaGPT can emit pre-rendered tool markers directly inside
/// <c>delta.content</c>. This splitter extracts those markers before markdown
/// parsing so the desktop renderer can display them as native blocks.
///
/// Recognized markers:
///
///   1. <c>&lt;blockquote class="tool-status {variant}"&gt;&lt;p&gt;{label}&lt;/p&gt;&lt;/blockquote&gt;</c>
///      where variant ∈ {analyzing, completed, error, info, success}.
///      Also matches the search-specific layout
///      <c>tool-status analyzing tool-search-blockquote</c>.
///
///   2. <c>&lt;DSanalysis data-tool-type="..."&gt;...&lt;/DSanalysis&gt;</c>
///      a collapsible details container whose body is itself markdown
///      (typically with a <c>**代码:**</c> / <c>**输入:**</c> / <c>**输出:**</c>
///      sub-structure). data-tool-type ∈ {python, mcp, tool-call,
///      image-analyze, image-gen, ...}.
///
///   3. <c>&lt;steel-step&gt;...&lt;/steel-step&gt;</c>
///      a Steel Browser timeline step emitted by steel_browser_shared.php.
///
/// Stream-friendly behavior:
///   - An open <c>&lt;DSanalysis&gt;</c> without its closing tag (the model
///     is mid-tool-call) is still emitted as a unit, with whatever inner
///     content has streamed so far. The presenter renders this as an
///     "analyzing" expandable panel; subsequent ticks will re-render the
///     same unit as content grows.
///   - Anything before / between / after markers is plain markdown and is
///     packaged into <see cref="MarkupUnitKind.Markdown"/> units.
/// </summary>
public static partial class MolaGptMarkupSplitter
{
    private const string PyOutputBegin = "<!--PY_OUTPUT_BEGIN-->";
    private const string PyOutputEnd = "<!--PY_OUTPUT_END-->";
    private const string McpOutputBegin = "<!--MCP_OUTPUT_BEGIN-->";
    private const string McpOutputEnd = "<!--MCP_OUTPUT_END-->";

    public enum Variant
    {
        None,
        Analyzing,
        Completed,
        Error,
        Info,
        Success,
        Cancelled
    }

    public sealed record MarkupUnit(
        MarkupUnitKind Kind,
        string Source,
        // For ToolStatus: the variant. For DSAnalysis: data-tool-type.
        string? Tag = null,
        // For ToolStatus: the inner label (already plain text, p contents).
        // For DSAnalysis: the inner markdown body.
        string? Inner = null,
        Variant ToolVariant = Variant.None,
        bool IsClosed = true,
        string? RawInner = null,
        IReadOnlyList<ToolSearchChip>? SearchChips = null,
        string? AnalysisPhase = null,
        string? SteelIconClass = null,
        IReadOnlyList<SteelMetaItem>? SteelMetaItems = null,
        // For DSAnalysis: the label of the tool-status blockquote that preceded
        // it, absorbed by MergeToolStatusIntoAnalysis so the pair renders as one
        // card rather than a card followed by a separate fold.
        string? StatusText = null,
        string? ToolCallId = null,
        string? ToolName = null,
        string? ToolIcon = null,
        string? RunningText = null,
        string? Metadata = null,
        string? ErrorText = null,
        string? RetryTicket = null,
        string? FileTicket = null,
        string? Filename = null,
        bool PresentationPending = false,
        int SourceOffset = -1);

    public sealed record ToolSearchChip(string Text, IReadOnlyList<string> Badges);
    public sealed record SteelMetaItem(string Text, string? IconClass);

    public static string ToolName(MarkupUnit unit) => unit.ToolName ?? (unit.Kind switch
    {
        MarkupUnitKind.ToolStatus when unit.Tag?.Contains("tool-search-blockquote", StringComparison.OrdinalIgnoreCase) == true => "search_web",
        MarkupUnitKind.ToolStatus when unit.Tag?.Contains("tool-image-blockquote", StringComparison.OrdinalIgnoreCase) == true => "generate_image",
        MarkupUnitKind.DsAnalysis => unit.Tag switch
        {
            "python" => "execute_python_code",
            "web_search" or "search_web" => "search_web",
            "tool-call" => "tool",
            _ => unit.Tag ?? "tool"
        },
        MarkupUnitKind.SteelStep => unit.Tag is "scrape" or "screenshot_analyze" ? "web_fetch" : "steel_browser",
        MarkupUnitKind.ImagePendingSkeleton or MarkupUnitKind.ImageErrorCard => "generate_image",
        _ => "tool"
    });

    public static string ToolPhase(MarkupUnit unit, bool isFinal) =>
        unit.Kind == MarkupUnitKind.DsAnalysis && unit.ToolCallId is null
            ? unit.AnalysisPhase switch
            {
                "error" => "error", "cancelled" => "cancelled",
                _ => unit.IsClosed ? "completed" : isFinal ? "cancelled" : "running"
            }
            : unit.ToolVariant switch
            {
                Variant.Completed or Variant.Success => "completed",
                Variant.Error => "error", Variant.Cancelled => "cancelled", Variant.Info => "info",
                Variant.None => isFinal ? "completed" : "running",
                _ => isFinal ? "cancelled" : "running"
            };

    [GeneratedRegex("""<blockquote\b(?<attrs>[^>]*\bclass\s*=\s*(?:"(?<class>[^"]*\btool-status\b[^"]*)"|'(?<class>[^']*\btool-status\b[^']*)')[^>]*)>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ToolStatusOpenRegex();

    [GeneratedRegex("""<DSanalysis\b(?<attrs>[^>]*)>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DsOpenRegex();

    [GeneratedRegex("""<steel-step\b(?<attrs>[^>]*)>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteelOpenRegex();

    [GeneratedRegex("""<div\b(?<attrs>[^>]*\bclass\s*=\s*(?:"(?<class>[^"]*\bai-image-pending-skeleton\b[^"]*)"|'(?<class>[^']*\bai-image-pending-skeleton\b[^']*)')[^>]*)>\s*</div>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ImagePendingSkeletonRegex();

    [GeneratedRegex("""<div\b[^>]*\bclass\s*=\s*(?:"[^"]*\bai-image-error-card\b[^"]*"|'[^']*\bai-image-error-card\b[^']*')[^>]*>\s*<div\b[^>]*\bclass\s*=\s*(?:"[^"]*\bai-image-error-title\b[^"]*"|'[^']*\bai-image-error-title\b[^']*')[^>]*>(?<title>.*?)</div>\s*<div\b[^>]*\bclass\s*=\s*(?:"[^"]*\bai-image-error-message\b[^"]*"|'[^']*\bai-image-error-message\b[^']*')[^>]*>(?<message>.*?)</div>\s*</div>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ImageErrorCardRegex();

    [GeneratedRegex("""<div\b(?<attrs>[^>]*\bclass\s*=\s*(?:"(?<class>[^"]*\btool-steel-step\b[^"]*)"|'(?<class>[^']*\btool-steel-step\b[^']*)')[^>]*)>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteelRootOpenRegex();

    [GeneratedRegex("""<span\b[^>]*\bclass\s*=\s*(?:"[^"]*\btool-steel-step-dot\b[^"]*"|'[^']*\btool-steel-step-dot\b[^']*')[^>]*>[\s\S]*?<i\b[^>]*\bclass\s*=\s*(?:"(?<class>[^"]*)"|'(?<class>[^']*)')""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteelDotIconRegex();

    [GeneratedRegex("""<p\b[^>]*\bclass\s*=\s*(?:"[^"]*\btool-steel-step-title\b[^"]*"|'[^']*\btool-steel-step-title\b[^']*')[^>]*>(?<inner>.*?)</p>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteelTitleRegex();

    [GeneratedRegex("""<span\b[^>]*\bclass\s*=\s*(?:"[^"]*\btool-steel-meta-item\b[^"]*"|'[^']*\btool-steel-meta-item\b[^']*')[^>]*>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteelMetaOpenRegex();

    [GeneratedRegex("""<?span\b(?=[^>]*\bclass\s*=\s*(?:"[^"]*\btool-steel-meta-item\b[^"]*"|'[^']*\btool-steel-meta-item\b[^']*'))[^>]*>[\s\S]*?</span>\s*</span>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OrphanSteelMetaFragmentRegex();

    [GeneratedRegex("""<i\b[^>]*\bclass\s*=\s*(?:"(?<class>[^"]*)"|'(?<class>[^']*)')""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InnerIconClassRegex();

    [GeneratedRegex("""(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""", RegexOptions.Singleline)]
    private static partial Regex AttrRegex();

    [GeneratedRegex("""<p\b[^>]*\bclass\s*=\s*(?:"[^"]*\btool-search-title\b[^"]*"|'[^']*\btool-search-title\b[^']*')[^>]*>(?<inner>.*?)</p>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SearchTitleRegex();

    [GeneratedRegex("""<span\b[^>]*\bclass\s*=\s*(?:"[^"]*(?<![\w-])tool-search-chip-text(?![\w-])[^"]*"|'[^']*(?<![\w-])tool-search-chip-text(?![\w-])[^']*')[^>]*>(?<inner>.*?)</span>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SearchChipTextRegex();

    [GeneratedRegex("""<span\b[^>]*\bclass\s*=\s*(?:"[^"]*(?<![\w-])tool-search-chip-badge(?![\w-])[^"]*"|'[^']*(?<![\w-])tool-search-chip-badge(?![\w-])[^']*')[^>]*>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SearchBadgeOpenRegex();

    [GeneratedRegex("""<span\b[^>]*\bclass\s*=\s*(?:"[^"]*(?<![\w-])tool-search-chip(?![\w-])[^"]*"|'[^']*(?<![\w-])tool-search-chip(?![\w-])[^']*')[^>]*>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SearchChipOpenRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private const string DsCloseTag = "</DSanalysis>";
    private const string BlockquoteCloseTag = "</blockquote>";
    private const string SteelCloseTag = "</steel-step>";

    public static List<MarkupUnit> Split(string source) =>
        string.IsNullOrEmpty(source) ? [] : SplitNormalized(NormalizeOutputSegmentMarkers(source));

    internal static List<MarkupUnit> SplitNormalized(string source)
    {
        var result = new List<MarkupUnit>();
        if (string.IsNullOrEmpty(source)) return result;
        if (!source.Contains("tool-status", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("<DSanalysis", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("<steel-step", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("ai-image-", StringComparison.OrdinalIgnoreCase))
        {
            EmitMarkdownIfAny(result, source, 0, source.Length);
            return result;
        }

        var literals = MarkdownSource.LiteralSpans(source);

        int pos = 0, search = 0;
        int len = source.Length;

        while (pos < len)
        {
            // Find next marker — earliest of tool-status, DSanalysis, or steel step.
            var dsMatch = DsOpenRegex().Match(source, search);
            var bqMatch = ToolStatusOpenRegex().Match(source, search);
            var steelMatch = SteelOpenRegex().Match(source, search);
            var imageSkeletonMatch = ImagePendingSkeletonRegex().Match(source, search);
            var imageErrorMatch = ImageErrorCardRegex().Match(source, search);

            int dsStart = dsMatch.Success ? dsMatch.Index : -1;
            int bqStart = bqMatch.Success ? bqMatch.Index : -1;
            int steelStart = steelMatch.Success ? steelMatch.Index : -1;
            int imageSkeletonStart = imageSkeletonMatch.Success ? imageSkeletonMatch.Index : -1;
            int imageErrorStart = imageErrorMatch.Success ? imageErrorMatch.Index : -1;

            if (dsStart < 0 && bqStart < 0 && steelStart < 0 && imageSkeletonStart < 0 && imageErrorStart < 0)
            {
                // No more markers — flush remainder as markdown.
                EmitMarkdownIfAny(result, source, pos, len);
                break;
            }

            int nextStart = MinPositive(dsStart, bqStart, steelStart, imageSkeletonStart, imageErrorStart);

            var literalEnd = literals.Where(span => span.Start <= nextStart && nextStart <= span.End)
                .Select(span => span.End).DefaultIfEmpty(-1).First();
            if (literalEnd >= 0)
            {
                search = literalEnd + 1;
                if (search >= len)
                {
                    EmitMarkdownIfAny(result, source, pos, len);
                    break;
                }
                continue;
            }

            // Flush plain markdown before the marker.
            EmitMarkdownIfAny(result, source, pos, nextStart);

            if (nextStart == steelStart && steelMatch.Success)
            {
                var bodyStart = steelMatch.Index + steelMatch.Length;
                var closeIdx = source.IndexOf(SteelCloseTag, bodyStart, StringComparison.OrdinalIgnoreCase);

                int bodyEnd;
                int unitEnd;
                bool isClosed;
                if (closeIdx < 0)
                {
                    bodyEnd = len;
                    unitEnd = len;
                    isClosed = false;
                }
                else
                {
                    bodyEnd = closeIdx;
                    unitEnd = closeIdx + SteelCloseTag.Length;
                    isClosed = true;
                }

                var inner = bodyEnd > bodyStart ? source.Substring(bodyStart, bodyEnd - bodyStart) : string.Empty;
                var raw = source.Substring(steelMatch.Index, unitEnd - steelMatch.Index);
                var parsed = ParseSteelStep(inner);
                result.Add(new MarkupUnit(
                    Kind: MarkupUnitKind.SteelStep,
                    Source: raw,
                    Tag: parsed.Action,
                    Inner: parsed.Title,
                    ToolVariant: parsed.Phase,
                    IsClosed: isClosed,
                    RawInner: inner,
                    SteelIconClass: parsed.IconClass,
                    SteelMetaItems: parsed.MetaItems,
                    SourceOffset: steelMatch.Index));
                pos = unitEnd;
            }
            else if (nextStart == imageSkeletonStart && imageSkeletonMatch.Success)
            {
                var raw = imageSkeletonMatch.Value;
                result.Add(new MarkupUnit(
                    Kind: MarkupUnitKind.ImagePendingSkeleton,
                    Source: raw,
                    Tag: "image-gen",
                    ToolVariant: Variant.Analyzing,
                    IsClosed: false,
                    AnalysisPhase: "analyzing",
                    SourceOffset: imageSkeletonMatch.Index));
                pos = imageSkeletonMatch.Index + imageSkeletonMatch.Length;
            }
            else if (nextStart == imageErrorStart && imageErrorMatch.Success)
            {
                var title = ExtractInnerText(imageErrorMatch.Groups["title"].Value);
                var message = ExtractInnerText(imageErrorMatch.Groups["message"].Value);
                result.Add(new MarkupUnit(
                    Kind: MarkupUnitKind.ImageErrorCard,
                    Source: imageErrorMatch.Value,
                    Tag: string.IsNullOrWhiteSpace(title) ? "图片绘制失败" : title,
                    Inner: message,
                    ToolVariant: Variant.Error,
                    IsClosed: true,
                    AnalysisPhase: "error",
                    SourceOffset: imageErrorMatch.Index));
                pos = imageErrorMatch.Index + imageErrorMatch.Length;
            }
            else if (nextStart == bqStart && bqMatch.Success)
            {
                // Tool status block. It may still be streaming, especially
                // the search layout where the server sends the opening tag,
                // then each chip, then the closing blockquote.
                var classes = bqMatch.Groups["class"].Value.Trim();
                var bodyStart = bqMatch.Index + bqMatch.Length;
                var closeIdx = source.IndexOf(BlockquoteCloseTag, bodyStart, StringComparison.OrdinalIgnoreCase);
                int bodyEnd;
                int unitEnd;
                bool isClosed;
                if (closeIdx < 0)
                {
                    bodyEnd = len;
                    unitEnd = len;
                    isClosed = false;
                }
                else
                {
                    bodyEnd = closeIdx;
                    unitEnd = closeIdx + BlockquoteCloseTag.Length;
                    isClosed = true;
                }

                var inner = bodyEnd > bodyStart ? source.Substring(bodyStart, bodyEnd - bodyStart) : string.Empty;
                var label = ExtractToolStatusLabel(inner, classes);
                var variant = ParseVariant(classes);
                var raw = source.Substring(bqMatch.Index, unitEnd - bqMatch.Index);
                result.Add(new MarkupUnit(
                    Kind: MarkupUnitKind.ToolStatus,
                    Source: raw,
                    Tag: classes,
                    Inner: label,
                    ToolVariant: variant,
                    IsClosed: isClosed,
                    RawInner: inner,
                    SearchChips: ExtractSearchChips(inner),
                    SourceOffset: bqMatch.Index));
                pos = unitEnd;
            }
            else
            {
                // DSanalysis. Find the matching close tag — if missing, the
                // tool is mid-call, body is everything after the open.
                var openLen = dsMatch.Length;
                var attrs = ParseAttributes(dsMatch.Groups["attrs"].Value);
                attrs.TryGetValue("data-tool-type", out var dataType);
                attrs.TryGetValue("data-analysis-phase", out var phase);
                var bodyStart = dsMatch.Index + openLen;
                var closeIdx = source.IndexOf(DsCloseTag, bodyStart, StringComparison.OrdinalIgnoreCase);

                int bodyEnd;
                int unitEnd;
                bool isClosed;
                if (closeIdx < 0)
                {
                    bodyEnd = len;
                    unitEnd = len;
                    isClosed = false;
                }
                else
                {
                    bodyEnd = closeIdx;
                    unitEnd = closeIdx + DsCloseTag.Length;
                    isClosed = true;
                }

                var body = bodyEnd > bodyStart ? source.Substring(bodyStart, bodyEnd - bodyStart) : string.Empty;
                var raw = source.Substring(dsMatch.Index, unitEnd - dsMatch.Index);
                result.Add(new MarkupUnit(
                    Kind: MarkupUnitKind.DsAnalysis,
                    Source: raw,
                    Tag: dataType,
                    Inner: body,
                    IsClosed: isClosed,
                    RawInner: body,
                    AnalysisPhase: phase,
                    SourceOffset: dsMatch.Index));
                pos = unitEnd;
            }
            search = pos;
        }

        CoalesceToolCalls(result);
        MergeToolStatusIntoAnalysis(result);
        ApplyImplicitToolStatusPhases(result);
        return result;
    }

    private static void CoalesceToolCalls(List<MarkupUnit> units)
    {
        var merged = new List<MarkupUnit>(units.Count);
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var original in units)
        {
            var attrs = CallAttributes(original);
            if (attrs?.GetValueOrDefault("data-tool-call") is not { Length: > 0 } id)
            {
                if (original.Kind != MarkupUnitKind.Markdown || HasVisibleActivity(original.Source))
                    ReleasePresentation(merged);
                merged.Add(original);
                continue;
            }

            var isAnalysis = original.Kind == MarkupUnitKind.DsAnalysis;
            var label = isAnalysis ? null : original.Inner;
            var unit = original with
            {
                ToolCallId = id,
                ToolName = attrs.GetValueOrDefault("data-tool-name"),
                ToolIcon = attrs.GetValueOrDefault("data-tool-icon"),
                StatusText = label,
                RunningText = original.ToolVariant == Variant.Analyzing ? label : null,
                Metadata = ElementText(original.Source, "tool-status-meta"),
                ErrorText = ElementText(original.Source, "tool-status-error"),
                RetryTicket = ActionTicket(original.Source, "tool-download-retry"),
                FileTicket = ActionTicket(original.Source, "tool-retry-file"),
                Filename = ActionTicket(original.Source, "tool-retry-file", "data-filename"),
                PresentationPending = original.ToolVariant is Variant.Completed or Variant.Success
            };
            if (!calls.TryGetValue(id, out var index))
            {
                ReleasePresentation(merged);
                calls.Add(id, merged.Count);
                merged.Add(unit);
                continue;
            }

            var previous = merged[index];
            var body = isAnalysis || previous.Kind != MarkupUnitKind.DsAnalysis ? unit : previous;
            merged[index] = body with
            {
                StatusText = label ?? previous.StatusText,
                RunningText = unit.RunningText ?? previous.RunningText,
                ToolName = unit.ToolName ?? previous.ToolName,
                ToolIcon = unit.ToolIcon ?? previous.ToolIcon,
                Source = previous.Source,
                SourceOffset = previous.SourceOffset,
                ToolVariant = unit.ToolVariant != Variant.None ? unit.ToolVariant : previous.ToolVariant,
                PresentationPending = unit.ToolVariant != Variant.None ? unit.PresentationPending : previous.PresentationPending,
                SearchChips = unit.SearchChips ?? previous.SearchChips,
                SteelMetaItems = unit.SteelMetaItems ?? previous.SteelMetaItems,
                Metadata = unit.Metadata ?? previous.Metadata,
                ErrorText = unit.ErrorText ?? previous.ErrorText,
                RetryTicket = unit.RetryTicket ?? previous.RetryTicket,
                FileTicket = unit.FileTicket ?? previous.FileTicket,
                Filename = unit.Filename ?? previous.Filename
            };
        }
        units.Clear();
        units.AddRange(merged);
    }

    private static Dictionary<string, string>? CallAttributes(MarkupUnit original)
    {
        var tag = original.Kind switch
        {
            MarkupUnitKind.SteelStep => SteelRootOpenRegex().Match(original.Source).Value,
            MarkupUnitKind.DsAnalysis or MarkupUnitKind.ToolStatus
                => original.Source[..(original.Source.IndexOf('>') + 1)],
            _ => string.Empty
        };
        return tag.Contains("data-tool-call", StringComparison.OrdinalIgnoreCase) ? ParseAttributes(tag) : null;
    }

    private static void ReleasePresentation(List<MarkupUnit> units)
    {
        for (var i = 0; i < units.Count; i++)
            if (units[i].PresentationPending) units[i] = units[i] with { PresentationPending = false };
    }

    private static bool HasVisibleActivity(string source) =>
        ExtractInnerText(source).Any(c => !char.IsWhiteSpace(c) && c is not '\u200B' and not '\uFEFF');

    private static string? ElementText(string source, string className)
    {
        var match = Regex.Match(source, """<(?:div|p|span)\b[^>]*class=["'][^"']*\b""" + className + """\b[^"']*["'][^>]*>(.*?)</(?:div|p|span)>""", RegexOptions.Singleline);
        return match.Success ? ExtractInnerText(match.Groups[1].Value) : null;
    }

    private static string? ActionTicket(string source, string className, string attribute = "data-tool-ticket")
    {
        var match = Regex.Match(source, """<button\b[^>]*class=["'][^"']*\b""" + className + """\b[^"']*["'][^>]*>""");
        return match.Success ? ParseAttributes(match.Value).GetValueOrDefault(attribute) : null;
    }

    private static int MinPositive(params int[] values)
    {
        var min = -1;
        foreach (var value in values)
        {
            if (value < 0) continue;
            if (min < 0 || value < min) min = value;
        }
        return min;
    }

    /// <summary>
    /// Marker comments delimit an instant output patch, not visible markdown.
    /// Complete marker pairs are stripped while
    /// preserving the payload. A dangling begin marker means the tool output
    /// payload is still streaming, so hide the partial tail until the end
    /// marker arrives.
    ///
    /// Exposed so <c>MarkdownPresenter</c> can strip these markers up front for
    /// EVERY render path (not just the splitter path): the output frame of a
    /// Python/MCP call arrives after the <c>&lt;DSanalysis&gt;</c> open tag was
    /// already consumed in an earlier frame, so an incremental frame may carry
    /// <c>...END--&gt;</c> with no custom-markup opener — without an up-front
    /// strip it would leak to the markdown renderer as literal text.
    /// </summary>
    public static string NormalizeOutputSegmentMarkers(string source)
    {
        if (!source.Contains("<!--", StringComparison.Ordinal)) return source;
        // Later paragraphs cannot close a code span around an earlier marker.
        // Keep the full-source incremental path from rebuilding their AST.
        var lastComment = source.LastIndexOf("<!--", StringComparison.Ordinal);
        var paragraphEnd = source.IndexOf("\n\n", lastComment, StringComparison.Ordinal);
        var windowsParagraphEnd = source.IndexOf("\r\n\r\n", lastComment, StringComparison.Ordinal);
        var boundary = MinPositive(paragraphEnd, windowsParagraphEnd);
        var literals = MarkdownSource.LiteralSpans(boundary < 0 ? source : source[..(boundary + 1)]);
        bool Literal(int index) => literals.Any(span => span.Start <= index && index <= span.End);
        var result = new System.Text.StringBuilder(source.Length);
        var pos = 0;
        while (pos < source.Length)
        {
            var next = source.IndexOf("<!--", pos, StringComparison.Ordinal);
            if (next < 0) { result.Append(source, pos, source.Length - pos); break; }
            result.Append(source, pos, next - pos);
            var marker = new[] { PyOutputBegin, PyOutputEnd, McpOutputBegin, McpOutputEnd }
                .FirstOrDefault(value => source.AsSpan(next).StartsWith(value, StringComparison.Ordinal));
            if (marker is null || Literal(next))
            {
                result.Append("<!--");
                pos = next + 4;
                continue;
            }
            if (marker == PyOutputBegin || marker == McpOutputBegin)
            {
                var closing = marker == PyOutputBegin ? PyOutputEnd : McpOutputEnd;
                var end = source.IndexOf(closing, next + marker.Length, StringComparison.Ordinal);
                while (end >= 0 && Literal(end))
                    end = source.IndexOf(closing, end + closing.Length, StringComparison.Ordinal);
                if (end < 0) break;
            }
            pos = next + marker.Length;
        }
        return HideDanglingMarkerPrefix(result.ToString());
    }

    private static string StripOrphanSteelMetaFragments(string source)
    {
        return source.Contains("tool-steel-meta-item", StringComparison.OrdinalIgnoreCase)
            ? OrphanSteelMetaFragmentRegex().Replace(source, string.Empty)
            : source;
    }

    private static string HideDanglingMarkerPrefix(string source)
    {
        foreach (var marker in new[] { PyOutputBegin, PyOutputEnd, McpOutputBegin, McpOutputEnd })
        {
            var max = Math.Min(marker.Length - 1, source.Length);
            for (int n = max; n >= 4; n--)
            {
                if (source.EndsWith(marker[..n], StringComparison.Ordinal))
                    return MarkdownSource.LiteralSpans(source).Any(span => span.Start <= source.Length - n && source.Length - n <= span.End)
                        ? source : source[..^n];
            }
        }

        return source;
    }

    private static void EmitMarkdownIfAny(List<MarkupUnit> result, string source, int start, int end)
    {
        if (end <= start) return;
        var slice = source.Substring(start, end - start);
        slice = StripOrphanSteelMetaFragments(slice);
        // Trim purely whitespace markdown segments to keep the unit list small,
        // but preserve them when they hold visible text or markdown structure.
        if (string.IsNullOrWhiteSpace(slice)) return;
        result.Add(new MarkupUnit(MarkupUnitKind.Markdown, slice, SourceOffset: start));
    }

    private static void ApplyImplicitToolStatusPhases(List<MarkupUnit> units)
    {
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit.Kind != MarkupUnitKind.ImagePendingSkeleton) continue;

            var phase = FindFollowingDsPhase(units, i + 1);
            if (phase is "completed" or "success" or "error")
            {
                var nextVariant = phase == "error" ? Variant.Error : Variant.Completed;
                units[i] = unit with
                {
                    ToolVariant = nextVariant,
                    IsClosed = true,
                    AnalysisPhase = phase
                };
            }
        }
    }

    /// <summary>
    /// Folds a tool-status blockquote into the &lt;DSanalysis&gt; that follows it.
    /// The status line ("正在通过连接器 GitHub 来处理用户请求") and the analysis body
    /// are two halves of one call, and the presenters draw them as a single card:
    /// the status becomes the card's subtitle, the body its fold.
    ///
    /// Adjacency is the only link the stream gives us — there is no shared id —
    /// so this uses the same rule <see cref="FindFollowingDsPhase"/> already
    /// relies on: real content between the two (prose, another status, another
    /// analysis, a browser step) breaks the pair. Search blockquotes keep their
    /// own card; nothing follows them to merge into.
    /// </summary>
    private static void MergeToolStatusIntoAnalysis(List<MarkupUnit> units)
    {
        for (int i = units.Count - 1; i >= 0; i--)
        {
            if (units[i].Kind != MarkupUnitKind.DsAnalysis) continue;
            var analysis = units[i];
            if (analysis.ToolCallId is not null || analysis.Tag is not ("mcp" or "python" or "image-action" or "code-search"))
                continue;

            var statusIndex = FindPrecedingToolStatus(units, i - 1);
            if (statusIndex < 0) continue;

            var status = units[statusIndex];
            if (status.ToolCallId is not null || status.ToolVariant is not (Variant.None or Variant.Analyzing)
                || string.IsNullOrWhiteSpace(status.Inner)
                || status.Tag?.Contains("tool-subagent-blockquote", StringComparison.OrdinalIgnoreCase) == true) continue;

            units[i] = units[i] with { StatusText = status.Inner };
            units.RemoveAt(statusIndex);
        }
    }

    private static int FindPrecedingToolStatus(List<MarkupUnit> units, int start)
    {
        for (int i = start; i >= 0 && i > start - 5; i--)
        {
            var unit = units[i];
            if (unit.Kind == MarkupUnitKind.Markdown)
            {
                if (!string.IsNullOrWhiteSpace(unit.Source)) return -1;
                continue;
            }

            return unit.Kind == MarkupUnitKind.ToolStatus && !IsSearchStatus(unit) ? i : -1;
        }

        return -1;
    }

    private static bool IsSearchStatus(MarkupUnit unit) =>
        unit.Tag?.Contains("tool-search-blockquote", StringComparison.OrdinalIgnoreCase) == true;

    private static string? FindFollowingDsPhase(List<MarkupUnit> units, int start)
    {
        for (int i = start; i < units.Count && i < start + 5; i++)
        {
            var unit = units[i];
            if (unit.Kind == MarkupUnitKind.ToolStatus) return null;
            if (unit.Kind == MarkupUnitKind.Markdown && !string.IsNullOrWhiteSpace(unit.Source)) return null;
            if (unit.Kind != MarkupUnitKind.DsAnalysis) continue;

            if (!string.IsNullOrWhiteSpace(unit.AnalysisPhase))
                return unit.AnalysisPhase!.Trim().ToLowerInvariant();

            return unit.IsClosed ? "completed" : "analyzing";
        }

        return null;
    }

    private static string ExtractToolStatusLabel(string innerHtml, string classes)
    {
        if (classes.Contains("tool-search-blockquote", StringComparison.OrdinalIgnoreCase))
        {
            var title = SearchTitleRegex().Match(innerHtml);
            if (title.Success) return ExtractInnerText(title.Groups["inner"].Value);
        }

        var paragraph = Regex.Match(innerHtml, "<p\\b[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return ExtractInnerText(paragraph.Success ? paragraph.Groups[1].Value : innerHtml);
    }

    private static IReadOnlyList<ToolSearchChip> ExtractSearchChips(string innerHtml)
    {
        var chips = new List<ToolSearchChip>();
        if (string.IsNullOrWhiteSpace(innerHtml)) return chips;

        var starts = SearchChipOpenRegex().Matches(innerHtml);

        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index;
            int end = (i + 1 < starts.Count) ? starts[i + 1].Index : innerHtml.Length;
            var chipHtml = innerHtml.Substring(start, Math.Max(0, end - start));
            var textMatch = SearchChipTextRegex().Match(chipHtml);
            if (!textMatch.Success) continue;

            var text = ExtractInnerText(textMatch.Groups["inner"].Value);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var badges = ExtractSearchBadges(chipHtml);

            chips.Add(new ToolSearchChip(text, badges));
        }

        return chips;
    }

    private static IReadOnlyList<string> ExtractSearchBadges(string chipHtml)
    {
        var badges = new List<string>();
        var starts = SearchBadgeOpenRegex().Matches(chipHtml);
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index + starts[i].Length;
            int end = (i + 1 < starts.Count) ? starts[i + 1].Index : chipHtml.Length;
            var text = ExtractInnerText(chipHtml.Substring(start, Math.Max(0, end - start)));
            if (!string.IsNullOrWhiteSpace(text)) badges.Add(text);
        }

        return badges;
    }

    private sealed record SteelStepParts(
        string? Action,
        string Title,
        Variant Phase,
        string? IconClass,
        IReadOnlyList<SteelMetaItem> MetaItems);

    private static SteelStepParts ParseSteelStep(string innerHtml)
    {
        var root = SteelRootOpenRegex().Match(innerHtml);
        var classes = root.Success ? root.Groups["class"].Value.Trim() : string.Empty;
        var attrs = root.Success ? ParseAttributes(root.Groups["attrs"].Value) : new Dictionary<string, string>();
        attrs.TryGetValue("data-steel-action", out var action);
        var phase = ParseVariant(classes);
        if (phase == Variant.None) phase = Variant.Analyzing;

        var titleMatch = SteelTitleRegex().Match(innerHtml);
        var title = titleMatch.Success ? ExtractInnerText(titleMatch.Groups["inner"].Value) : ExtractInnerText(innerHtml);
        if (string.IsNullOrWhiteSpace(title)) title = "正在查看网页";

        var iconMatch = SteelDotIconRegex().Match(innerHtml);
        var iconClass = iconMatch.Success ? iconMatch.Groups["class"].Value : null;
        return new SteelStepParts(action, title, phase, iconClass, ExtractSteelMetaItems(innerHtml));
    }

    private static IReadOnlyList<SteelMetaItem> ExtractSteelMetaItems(string innerHtml)
    {
        var items = new List<SteelMetaItem>();
        var starts = SteelMetaOpenRegex().Matches(innerHtml);
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index + starts[i].Length;
            int end = (i + 1 < starts.Count) ? starts[i + 1].Index : innerHtml.Length;
            var itemHtml = innerHtml.Substring(start, Math.Max(0, end - start));
            var iconMatch = InnerIconClassRegex().Match(itemHtml);
            var iconClass = iconMatch.Success ? iconMatch.Groups["class"].Value : null;
            var text = ExtractInnerText(itemHtml);
            if (!string.IsNullOrWhiteSpace(text))
                items.Add(new SteelMetaItem(text, iconClass));
        }

        return items;
    }

    private static string ExtractInnerText(string innerHtml)
    {
        if (string.IsNullOrWhiteSpace(innerHtml)) return string.Empty;
        // Strip HTML tags (rough — sufficient for tool-status which is just
        // `<p>label</p>` plus an optional decoration).
        var noTags = HtmlTagRegex().Replace(innerHtml, " ");
        return WebUtility.HtmlDecode(WhitespaceRegex().Replace(noTags, " ").Trim());
    }

    private static Dictionary<string, string> ParseAttributes(string attrs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttrRegex().Matches(attrs))
        {
            result[match.Groups["name"].Value] = WebUtility.HtmlDecode(match.Groups["value"].Value);
        }

        return result;
    }

    private static Variant ParseVariant(string classExtra)
    {
        var lower = classExtra.ToLowerInvariant();
        if (lower.Contains("cancelled")) return Variant.Cancelled;
        if (lower.Contains("analyzing")) return Variant.Analyzing;
        if (lower.Contains("completed")) return Variant.Completed;
        if (lower.Contains("error")) return Variant.Error;
        if (lower.Contains("info")) return Variant.Info;
        if (lower.Contains("success")) return Variant.Success;
        return Variant.None;
    }
}

public enum MarkupUnitKind
{
    Markdown,
    ToolStatus,
    DsAnalysis,
    SteelStep,
    ImagePendingSkeleton,
    ImageErrorCard
}
