using Markdig;
using Markdig.Syntax.Inlines;

// Several block kinds here share a name with their Markdig counterpart
// (Paragraph, Heading, Quote, List, ThematicBreak, Code). Alias the Markdig side
// so the unqualified names in this file always mean our own render model.
using MdBlock = Markdig.Syntax.Block;
using MdCode = Markdig.Syntax.CodeBlock;
using MdFencedCode = Markdig.Syntax.FencedCodeBlock;
using MdHeading = Markdig.Syntax.HeadingBlock;
using MdList = Markdig.Syntax.ListBlock;
using MdParagraph = Markdig.Syntax.ParagraphBlock;
using MdQuote = Markdig.Syntax.QuoteBlock;
using MdTable = Markdig.Extensions.Tables.Table;
using MdThematicBreak = Markdig.Syntax.ThematicBreakBlock;

namespace MolaGPT.Presentation;

/// <summary>
/// Turns a message body into a flat <see cref="RenderDocument"/>.
///
/// Everything here is pure computation over strings: no UI types, no thread
/// affinity, no ambient state. It is meant to be called from a background
/// thread, which is the whole point — the old renderer could not move Markdig
/// off the UI thread because parsing and WPF object construction were the same
/// pass.
///
/// Block classification mirrors what MarkdownPresenter's dispatch does today
/// (image-only paragraphs, math fences, code fences and quotes get their own
/// block kinds; everything else keeps its source slice for the view to render
/// inline), so the two renderers produce the same visual decomposition.
/// </summary>
public static partial class MessageDocumentParser
{
    /// <summary>Matches MarkdownPresenter's streaming pipeline.</summary>
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UsePipeTables()
        .UseGridTables()
        .DisableHtml()
        .Build();

    private static readonly string[] s_mathLanguages = ["math", "latex", "tex"];

    public static RenderDocument Parse(string? body, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(body)) return RenderDocument.Empty;

        var hash = Hash(body);
        var normalized = MolaGptMarkupSplitter.NormalizeOutputSegmentMarkers(body);

        List<RenderBlock> blocks;
        try
        {
            blocks = ParseCore(normalized, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A malformed or half-streamed body must degrade to readable text,
            // never take the caller down. The old renderer had the same rule.
            blocks =
            [
                new RawTextBlock
                {
                    Text = normalized,
                    SourceStart = 0,
                    SourceLength = normalized.Length,
                    Key = MakeKey("raw", normalized, new Dictionary<string, int>())
                }
            ];
        }

        return new RenderDocument(hash, normalized, blocks);
    }

    /// <summary>
    /// How many trailing blocks are thrown away and re-parsed on an append.
    /// One would cover a paragraph growing; three also covers the cases where
    /// new text reaches backwards — a fence closing, a setext underline turning
    /// the line above into a heading, a lazy list continuation.
    /// </summary>
    private const int IncrementalTailBlocks = 3;

    /// <summary>
    /// Re-parse for the streaming case, reusing the blocks that cannot have
    /// changed.
    ///
    /// A streamed answer only ever grows at the end, so everything before the
    /// last few blocks is already final. Reusing those blocks by reference also
    /// keeps their <see cref="RenderBlock.Key"/>s identical, which is what stops
    /// the list from re-realizing elements the user is currently looking at.
    ///
    /// Falls back to a full <see cref="Parse"/> whenever the new body is not a
    /// pure append — a retry, an edit, or a normalization pass that rewrote
    /// earlier text.
    /// </summary>
    public static RenderDocument ParseIncremental(
        RenderDocument? previous, string? body, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(body)) return RenderDocument.Empty;
        if (previous is null || previous.Blocks.Count == 0) return Parse(body, ct);

        var hash = Hash(body);
        if (hash == previous.SourceHash) return previous;

        var normalized = MolaGptMarkupSplitter.NormalizeOutputSegmentMarkers(body);
        if (!normalized.StartsWith(previous.Source, StringComparison.Ordinal))
            return Parse(body, ct);

        var cutIndex = Math.Max(0, previous.Blocks.Count - IncrementalTailBlocks);
        var cutOffset = previous.Blocks[cutIndex].SourceStart;
        if (cutOffset <= 0 || cutOffset > normalized.Length) return Parse(body, ct);

        try
        {
            var reused = previous.Blocks.Take(cutIndex).ToList();

            // Seed the duplicate counter from the reused prefix so a block in the
            // tail that repeats earlier content cannot be handed a key that is
            // already in use above it.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var block in reused)
            {
                var baseKey = block.Key.Split('#')[0];
                seen[baseKey] = seen.TryGetValue(baseKey, out var n) ? n + 1 : 1;
            }

            var tail = ParseCore(normalized[cutOffset..], ct, seen);
            foreach (var block in tail)
                reused.Add(block with { SourceStart = block.SourceStart + cutOffset });

            return new RenderDocument(hash, normalized, reused);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Parse(body, ct);
        }
    }

    private static List<RenderBlock> ParseCore(
        string body, CancellationToken ct, Dictionary<string, int>? seenSeed = null)
    {
        // Tool status / analysis / image cards are pre-rendered markup that
        // MolaGPT emits straight into delta.content. They have to come out
        // before Markdig sees the text, exactly as the FlowDocument renderer
        // does it, or the markdown parser mangles them.
        var units = MolaGptMarkupSplitter.Split(body);
        var blocks = new List<RenderBlock>();
        var seen = seenSeed ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var offset = 0;

        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            ct.ThrowIfCancellationRequested();

            // Split partitions the input, so a running sum over unit sources
            // gives each unit's offset. Guard anyway: if a future splitter change
            // breaks that, fall back to searching rather than emitting offsets
            // that index the wrong text.
            var start = body.AsSpan(offset).StartsWith(unit.Source)
                ? offset
                : body.IndexOf(unit.Source, offset, StringComparison.Ordinal);
            if (start < 0) start = offset;
            offset = start + unit.Source.Length;

            if (unit.Kind == MarkupUnitKind.Markdown)
                AppendMarkdownBlocks(unit.Source, start, blocks, seen, ct);
            else
                blocks.Add(new MarkupUnitBlock
                {
                    Unit = unit,
                    HasFollowingContent = HasFollowingContent(units, index),
                    SourceStart = start,
                    SourceLength = unit.Source.Length,
                    Key = MakeKey($"unit-{unit.Kind}", unit.Source, seen)
                });
        }

        return blocks;
    }

    /// <summary>
    /// Mirrors MarkdownPresenter.HasFollowingContent: a trailing run of status
    /// chips or blank markdown does not count as the answer having moved on.
    /// </summary>
    /// <summary>
    /// Flattens a list into items carrying their nesting depth.
    ///
    /// The view needs depth plus the item's own inline markdown; it does not
    /// need the tree. Flattening here keeps the view a straight loop and means
    /// a deeply nested list cannot recurse the UI layer.
    /// </summary>
    private static List<ListItem> FlattenList(MdList list, string segment)
    {
        var items = new List<ListItem>();
        Walk(list, 0);
        return items;

        void Walk(MdList current, int depth)
        {
            var number = current.IsOrdered && int.TryParse(current.OrderedStart, out var first) ? first : 1;

            foreach (var child in current)
            {
                if (child is not Markdig.Syntax.ListItemBlock item) continue;

                // The item's own text is its leaf blocks; a nested list under it
                // is a separate child and is recursed into rather than inlined.
                var text = new List<string>();
                var nested = new List<MdList>();

                foreach (var part in item)
                {
                    if (part is MdList inner) nested.Add(inner);
                    else if (SliceOf(part, segment) is { Length: > 0 } s) text.Add(s);
                }

                items.Add(new ListItem(
                    string.Join(" ", text).Trim(),
                    depth,
                    current.IsOrdered,
                    number++));

                foreach (var inner in nested) Walk(inner, depth + 1);
            }
        }
    }

    private static List<IReadOnlyList<string>> ReadTableRows(MdTable table, string segment)
    {
        var rows = new List<IReadOnlyList<string>>();

        foreach (var rowNode in table)
        {
            if (rowNode is not Markdig.Extensions.Tables.TableRow row) continue;

            var cells = new List<string>();
            foreach (var cellNode in row)
            {
                if (cellNode is not Markdig.Extensions.Tables.TableCell cell)
                {
                    cells.Add(string.Empty);
                    continue;
                }

                var parts = new List<string>();
                foreach (var part in cell)
                {
                    if (SliceOf(part, segment) is { Length: > 0 } s) parts.Add(s);
                }
                cells.Add(string.Join(" ", parts).Trim());
            }
            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// -1 left/unset, 0 centre, 1 right — one entry per real column.
    ///
    /// Trimmed to the widest row on purpose. Markdig reports one more column
    /// definition than the table has for the usual pipe-delimited syntax (the
    /// trailing "|" opens a column that never gets a cell), and taking that
    /// count at face value renders a phantom empty column on the right.
    /// </summary>
    private static List<int> ReadTableAlignments(
        MdTable table, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

        var alignments = new List<int>();
        foreach (var definition in table.ColumnDefinitions)
        {
            if (alignments.Count >= columns) break;
            alignments.Add(definition.Alignment switch
            {
                Markdig.Extensions.Tables.TableColumnAlign.Center => 0,
                Markdig.Extensions.Tables.TableColumnAlign.Right => 1,
                _ => -1
            });
        }

        // A row wider than the declared columns still needs an entry each.
        while (alignments.Count < columns) alignments.Add(-1);
        return alignments;
    }

    /// <summary>
    /// The source text a node covers, or null when its span is unusable.
    ///
    /// Slicing rather than reading the inline AST is deliberate: the view
    /// re-parses inline markdown itself, so it needs the markers intact.
    ///
    /// The extent comes from the inline tree rather than the block's own Span.
    /// Inside a table cell Markdig reports a paragraph span that stops at the
    /// first emphasis delimiter, so "**x**" sliced by the block span yields
    /// "**". The inlines carry the true bounds.
    /// </summary>
    private static string? SliceOf(MdBlock node, string segment)
    {
        var start = node.Span.Start;
        var end = node.Span.End + 1;

        if (node is Markdig.Syntax.LeafBlock { Inline: { } container }
            && InlineExtent(container) is var (inlineStart, inlineEnd)
            && inlineEnd > inlineStart)
        {
            start = Math.Min(start < 0 ? inlineStart : start, inlineStart);
            end = Math.Max(end, inlineEnd);
        }

        if (start < 0 || end > segment.Length || end <= start) return null;
        return segment[start..end];
    }

    /// <summary>Min start and max end over an inline subtree, in source offsets.</summary>
    private static (int Start, int End) InlineExtent(ContainerInline container)
    {
        var start = int.MaxValue;
        var end = int.MinValue;

        Walk(container);
        return start == int.MaxValue ? (0, 0) : (start, end);

        void Walk(ContainerInline current)
        {
            foreach (var inline in current)
            {
                if (inline.Span.Start >= 0)
                {
                    if (inline.Span.Start < start) start = inline.Span.Start;
                    if (inline.Span.End + 1 > end) end = inline.Span.End + 1;
                }
                if (inline is ContainerInline nested) Walk(nested);
            }
        }
    }

    private static bool HasFollowingContent(
        IReadOnlyList<MolaGptMarkupSplitter.MarkupUnit> units, int index)
    {
        for (var i = index + 1; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit.Kind == MarkupUnitKind.ToolStatus) continue;
            if (unit.Kind == MarkupUnitKind.Markdown)
            {
                if (!string.IsNullOrWhiteSpace(unit.Source)) return true;
                continue;
            }
            return true;
        }
        return false;
    }

    private static void AppendMarkdownBlocks(
        string segment,
        int segmentStart,
        List<RenderBlock> blocks,
        Dictionary<string, int> seen,
        CancellationToken ct)
    {
        if (segment.Length == 0) return;

        var ast = Markdig.Markdown.Parse(segment, s_pipeline);
        foreach (var node in ast)
        {
            ct.ThrowIfCancellationRequested();

            var (start, length) = SliceSpan(node, segment);
            if (length <= 0) continue;
            var slice = segment.Substring(start, length);

            blocks.Add(Classify(node, segment, slice, segmentStart + start, length, seen));
        }
    }

    /// <param name="segment">The text the node's spans index into. Needed because
    /// list items and table cells are reported as inline markdown, which can only
    /// be recovered by slicing the original source at each leaf's span.</param>
    private static RenderBlock Classify(
        MdBlock node, string segment, string slice, int start, int length, Dictionary<string, int> seen)
    {
        switch (node)
        {
            case MdFencedCode fenced:
            {
                var language = (fenced.Info ?? string.Empty).Trim();
                var code = fenced.Lines.ToString();
                if (IsMathFence(language))
                {
                    return new MathBlock
                    {
                        Latex = code,
                        SourceStart = start,
                        SourceLength = length,
                        Key = MakeKey("math", code, seen)
                    };
                }

                return new CodeBlock
                {
                    Language = language,
                    Code = code,
                    LineCount = CountLines(code),
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("code", code, seen)
                };
            }

            case MdCode indented:
            {
                var code = indented.Lines.ToString();
                return new CodeBlock
                {
                    Language = string.Empty,
                    Code = code,
                    LineCount = CountLines(code),
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("code", code, seen)
                };
            }

            case MdHeading heading:
                return new HeadingBlock
                {
                    Level = heading.Level,
                    Markdown = slice,
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("h", slice, seen)
                };

            case MdQuote:
                return new QuoteBlock
                {
                    Markdown = slice,
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("quote", slice, seen)
                };

            case MdList list:
                return new ListBlock
                {
                    Markdown = slice,
                    IsOrdered = list.IsOrdered,
                    Items = FlattenList(list, segment),
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("list", slice, seen)
                };

            case MdTable table:
            {
                var rows = ReadTableRows(table, segment);
                return new TableBlock
                {
                    Markdown = slice,
                    RowCount = table.Count,
                    Rows = rows,
                    HasHeader = table.Count > 0
                                && table[0] is Markdig.Extensions.Tables.TableRow { IsHeader: true },
                    Alignments = ReadTableAlignments(table, rows),
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("table", slice, seen)
                };
            }

            case MdThematicBreak:
                return new ThematicBreakBlock
                {
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("hr", slice, seen)
                };

            case MdParagraph paragraph when TryLoneImage(paragraph) is { } image:
                return new ImageBlock
                {
                    Url = image.Url,
                    Alt = image.Alt,
                    Title = image.Title,
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("img", image.Url, seen)
                };

            default:
                return new ParagraphBlock
                {
                    Markdown = slice,
                    SourceStart = start,
                    SourceLength = length,
                    Key = MakeKey("p", slice, seen)
                };
        }
    }

    /// <summary>
    /// A paragraph whose only visible content is a single image — rendered as a
    /// standalone card rather than as an inline run, matching what the current
    /// renderer does.
    /// </summary>
    private static (string Url, string? Alt, string? Title)? TryLoneImage(MdParagraph paragraph)
    {
        if (paragraph.Inline is null) return null;

        LinkInline? image = null;
        foreach (var inline in paragraph.Inline)
        {
            switch (inline)
            {
                case LinkInline { IsImage: true } link when image is null:
                    image = link;
                    break;
                case LiteralInline literal when literal.Content.ToString().Trim().Length == 0:
                    break;
                case LineBreakInline:
                    break;
                default:
                    return null;
            }
        }

        if (image?.Url is not { Length: > 0 } url) return null;
        var alt = image.FirstChild is LiteralInline alt0 ? alt0.Content.ToString() : null;
        return (url, alt, image.Title);
    }

    /// <summary>
    /// Markdig 0.22.0 under-reports <c>Table.Span.End</c>: it stops inside the
    /// last row's final cell, so slicing by span end truncates that row. Extend
    /// the slice to the end of the line the span lands on. The next block always
    /// starts after that newline, so this never crosses into it. (Carried over
    /// verbatim in intent from MarkdownPresenter.SliceAstBlockSources.)
    /// </summary>
    private static (int Start, int Length) SliceSpan(MdBlock node, string source)
    {
        var span = node.Span;
        if (span.IsEmpty || span.Start < 0) return (0, 0);

        var start = Math.Max(0, span.Start);
        var end = Math.Min(source.Length, span.End + 1);

        if (node is MdTable)
        {
            while (end < source.Length && source[end] != '\n') end++;
            if (end < source.Length) end++;
        }

        return start >= end ? (0, 0) : (start, end - start);
    }

    private static bool IsMathFence(string language)
    {
        foreach (var candidate in s_mathLanguages)
        {
            if (language.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n') lines++;
        }
        return lines;
    }

    /// <summary>
    /// Identity for list diffing: kind plus content hash plus how many identical
    /// blocks came before it. Position is deliberately excluded so inserting a
    /// block does not invalidate the keys of everything after it.
    /// </summary>
    private static string MakeKey(string kind, string content, Dictionary<string, int> seen)
    {
        var baseKey = $"{kind}:{Hash(content)}";
        seen.TryGetValue(baseKey, out var occurrence);
        seen[baseKey] = occurrence + 1;
        return occurrence == 0 ? baseKey : $"{baseKey}#{occurrence}";
    }

    /// <summary>FNV-1a. Not cryptographic — this only needs to be stable, cheap
    /// and collision-resistant enough to key a list of a few thousand blocks.</summary>
    private static string Hash(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash.ToString("x16");
    }
}
