using System.Net;
using System.Text.RegularExpressions;

namespace MolaGPT.Desktop.Controls;

/// <summary>
/// Splits a MolaGPT-style assistant message into a sequence of
/// <see cref="MarkupUnit"/>s before any Markdig parsing.
///
/// MolaGPT can emit pre-rendered tool markers directly inside
/// <c>delta.content</c>. This splitter extracts those markers before markdown
/// parsing so the desktop renderer can display them as native WPF blocks.
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
public static class MolaGptMarkupSplitter
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
        Success
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
        IReadOnlyList<SteelMetaItem>? SteelMetaItems = null);

    public sealed record ToolSearchChip(string Text, IReadOnlyList<string> Badges);
    public sealed record SteelMetaItem(string Text, string? IconClass);

    private static readonly Regex s_toolStatusOpenRegex = new(
        @"<blockquote\b(?<attrs>[^>]*\bclass\s*=\s*(?:""(?<class>[^""]*\btool-status\b[^""]*)""|'(?<class>[^']*\btool-status\b[^']*)')[^>]*)>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_dsOpenRegex = new(
        @"<DSanalysis\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_steelOpenRegex = new(
        @"<steel-step\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_imagePendingSkeletonRegex = new(
        @"<div\b(?<attrs>[^>]*\bclass\s*=\s*(?:""(?<class>[^""]*\bai-image-pending-skeleton\b[^""]*)""|'(?<class>[^']*\bai-image-pending-skeleton\b[^']*)')[^>]*)>\s*</div>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_imageErrorCardRegex = new(
        @"<div\b[^>]*\bclass\s*=\s*(?:""[^""]*\bai-image-error-card\b[^""]*""|'[^']*\bai-image-error-card\b[^']*')[^>]*>\s*<div\b[^>]*\bclass\s*=\s*(?:""[^""]*\bai-image-error-title\b[^""]*""|'[^']*\bai-image-error-title\b[^']*')[^>]*>(?<title>.*?)</div>\s*<div\b[^>]*\bclass\s*=\s*(?:""[^""]*\bai-image-error-message\b[^""]*""|'[^']*\bai-image-error-message\b[^']*')[^>]*>(?<message>.*?)</div>\s*</div>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_steelRootOpenRegex = new(
        @"<div\b(?<attrs>[^>]*\bclass\s*=\s*(?:""(?<class>[^""]*\btool-steel-step\b[^""]*)""|'(?<class>[^']*\btool-steel-step\b[^']*)')[^>]*)>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_steelDotIconRegex = new(
        @"<span\b[^>]*\bclass\s*=\s*(?:""[^""]*\btool-steel-step-dot\b[^""]*""|'[^']*\btool-steel-step-dot\b[^']*')[^>]*>[\s\S]*?<i\b[^>]*\bclass\s*=\s*(?:""(?<class>[^""]*)""|'(?<class>[^']*)')",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_steelTitleRegex = new(
        @"<p\b[^>]*\bclass\s*=\s*(?:""[^""]*\btool-steel-step-title\b[^""]*""|'[^']*\btool-steel-step-title\b[^']*')[^>]*>(?<inner>.*?)</p>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_steelMetaOpenRegex = new(
        @"<span\b[^>]*\bclass\s*=\s*(?:""[^""]*\btool-steel-meta-item\b[^""]*""|'[^']*\btool-steel-meta-item\b[^']*')[^>]*>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_orphanSteelMetaFragmentRegex = new(
        @"<?span\b(?=[^>]*\bclass\s*=\s*(?:""[^""]*\btool-steel-meta-item\b[^""]*""|'[^']*\btool-steel-meta-item\b[^']*'))[^>]*>[\s\S]*?</span>\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_innerIconClassRegex = new(
        @"<i\b[^>]*\bclass\s*=\s*(?:""(?<class>[^""]*)""|'(?<class>[^']*)')",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_attrRegex = new(
        @"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex s_searchTitleRegex = new(
        @"<p\b[^>]*\bclass\s*=\s*(?:""[^""]*\btool-search-title\b[^""]*""|'[^']*\btool-search-title\b[^']*')[^>]*>(?<inner>.*?)</p>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_searchChipTextRegex = new(
        @"<span\b[^>]*\bclass\s*=\s*(?:""[^""]*(?<![\w-])tool-search-chip-text(?![\w-])[^""]*""|'[^']*(?<![\w-])tool-search-chip-text(?![\w-])[^']*')[^>]*>(?<inner>.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_searchBadgeOpenRegex = new(
        @"<span\b[^>]*\bclass\s*=\s*(?:""[^""]*(?<![\w-])tool-search-chip-badge(?![\w-])[^""]*""|'[^']*(?<![\w-])tool-search-chip-badge(?![\w-])[^']*')[^>]*>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private const string DsCloseTag = "</DSanalysis>";
    private const string BlockquoteCloseTag = "</blockquote>";
    private const string SteelCloseTag = "</steel-step>";

    public static List<MarkupUnit> Split(string source)
    {
        var result = new List<MarkupUnit>();
        if (string.IsNullOrEmpty(source)) return result;

        source = NormalizeOutputSegmentMarkers(source);

        int pos = 0;
        int len = source.Length;

        while (pos < len)
        {
            // Find next marker — earliest of tool-status, DSanalysis, or steel step.
            var dsMatch = s_dsOpenRegex.Match(source, pos);
            var bqMatch = s_toolStatusOpenRegex.Match(source, pos);
            var steelMatch = s_steelOpenRegex.Match(source, pos);
            var imageSkeletonMatch = s_imagePendingSkeletonRegex.Match(source, pos);
            var imageErrorMatch = s_imageErrorCardRegex.Match(source, pos);

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
                    SteelMetaItems: parsed.MetaItems));
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
                    AnalysisPhase: "analyzing"));
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
                    AnalysisPhase: "error"));
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
                    SearchChips: ExtractSearchChips(inner)));
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
                    AnalysisPhase: phase));
                pos = unitEnd;
            }
        }

        ApplyImplicitToolStatusPhases(result);
        return result;
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
    /// </summary>
    private static string NormalizeOutputSegmentMarkers(string source)
    {
        source = NormalizeMarkerPair(source, PyOutputBegin, PyOutputEnd);
        source = NormalizeMarkerPair(source, McpOutputBegin, McpOutputEnd);
        return HideDanglingMarkerPrefix(source);
    }

    private static string StripOrphanSteelMetaFragments(string source)
    {
        return source.Contains("tool-steel-meta-item", StringComparison.OrdinalIgnoreCase)
            ? s_orphanSteelMetaFragmentRegex.Replace(source, string.Empty)
            : source;
    }

    private static string NormalizeMarkerPair(string source, string begin, string end)
    {
        var result = new System.Text.StringBuilder(source.Length);
        var pos = 0;
        while (pos < source.Length)
        {
            var beginIdx = source.IndexOf(begin, pos, StringComparison.Ordinal);
            if (beginIdx < 0)
            {
                result.Append(source, pos, source.Length - pos);
                break;
            }

            result.Append(source, pos, beginIdx - pos);
            var payloadStart = beginIdx + begin.Length;
            var endIdx = source.IndexOf(end, payloadStart, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                // Keep everything before the marker and hide the partial
                // output tail. The next render tick will include the end
                // marker and reveal the payload atomically.
                break;
            }

            result.Append(source, payloadStart, endIdx - payloadStart);
            pos = endIdx + end.Length;
        }

        return result.ToString();
    }

    private static string HideDanglingMarkerPrefix(string source)
    {
        foreach (var marker in new[] { PyOutputBegin, PyOutputEnd, McpOutputBegin, McpOutputEnd })
        {
            var max = Math.Min(marker.Length - 1, source.Length);
            for (int n = max; n >= 4; n--)
            {
                if (source.EndsWith(marker[..n], StringComparison.Ordinal))
                    return source[..^n];
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
        result.Add(new MarkupUnit(MarkupUnitKind.Markdown, slice));
    }

    private static void ApplyImplicitToolStatusPhases(List<MarkupUnit> units)
    {
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit.Kind != MarkupUnitKind.ToolStatus) continue;
            if (unit.ToolVariant is not (Variant.Analyzing or Variant.None)) continue;

            var phase = FindFollowingDsPhase(units, i + 1);
            var nextVariant = phase switch
            {
                "completed" => Variant.Completed,
                "success" => Variant.Success,
                "error" => Variant.Error,
                "analyzing" => Variant.Analyzing,
                _ => unit.ToolVariant
            };

            if (nextVariant != unit.ToolVariant)
                units[i] = unit with { ToolVariant = nextVariant };
        }

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
            var title = s_searchTitleRegex.Match(innerHtml);
            if (title.Success) return ExtractInnerText(title.Groups["inner"].Value);
        }

        return ExtractInnerText(innerHtml);
    }

    private static IReadOnlyList<ToolSearchChip> ExtractSearchChips(string innerHtml)
    {
        var chips = new List<ToolSearchChip>();
        if (string.IsNullOrWhiteSpace(innerHtml)) return chips;

        var starts = Regex.Matches(
            innerHtml,
            @"<span\b[^>]*\bclass\s*=\s*(?:""[^""]*(?<![\w-])tool-search-chip(?![\w-])[^""]*""|'[^']*(?<![\w-])tool-search-chip(?![\w-])[^']*')[^>]*>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index;
            int end = (i + 1 < starts.Count) ? starts[i + 1].Index : innerHtml.Length;
            var chipHtml = innerHtml.Substring(start, Math.Max(0, end - start));
            var textMatch = s_searchChipTextRegex.Match(chipHtml);
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
        var starts = s_searchBadgeOpenRegex.Matches(chipHtml);
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
        var root = s_steelRootOpenRegex.Match(innerHtml);
        var classes = root.Success ? root.Groups["class"].Value.Trim() : string.Empty;
        var attrs = root.Success ? ParseAttributes(root.Groups["attrs"].Value) : new Dictionary<string, string>();
        attrs.TryGetValue("data-steel-action", out var action);
        var phase = ParseVariant(classes);
        if (phase == Variant.None) phase = Variant.Analyzing;

        var titleMatch = s_steelTitleRegex.Match(innerHtml);
        var title = titleMatch.Success ? ExtractInnerText(titleMatch.Groups["inner"].Value) : ExtractInnerText(innerHtml);
        if (string.IsNullOrWhiteSpace(title)) title = "正在查看网页";

        var iconMatch = s_steelDotIconRegex.Match(innerHtml);
        var iconClass = iconMatch.Success ? iconMatch.Groups["class"].Value : null;
        return new SteelStepParts(action, title, phase, iconClass, ExtractSteelMetaItems(innerHtml));
    }

    private static IReadOnlyList<SteelMetaItem> ExtractSteelMetaItems(string innerHtml)
    {
        var items = new List<SteelMetaItem>();
        var starts = s_steelMetaOpenRegex.Matches(innerHtml);
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index + starts[i].Length;
            int end = (i + 1 < starts.Count) ? starts[i + 1].Index : innerHtml.Length;
            var itemHtml = innerHtml.Substring(start, Math.Max(0, end - start));
            var iconMatch = s_innerIconClassRegex.Match(itemHtml);
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
        var noTags = Regex.Replace(innerHtml, "<[^>]+>", " ", RegexOptions.Singleline);
        return WebUtility.HtmlDecode(Regex.Replace(noTags, @"\s+", " ").Trim());
    }

    private static Dictionary<string, string> ParseAttributes(string attrs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in s_attrRegex.Matches(attrs))
        {
            result[match.Groups["name"].Value] = WebUtility.HtmlDecode(match.Groups["value"].Value);
        }

        return result;
    }

    private static Variant ParseVariant(string classExtra)
    {
        var lower = classExtra.ToLowerInvariant();
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
