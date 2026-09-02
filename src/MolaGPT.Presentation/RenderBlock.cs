namespace MolaGPT.Presentation;

/// <summary>
/// One unit of a rendered answer. Deliberately pure data: no UIElement, no
/// FlowDocument, no Dispatcher, nothing from System.Windows or Microsoft.UI.
/// That is what lets the whole document be built on a background thread and
/// handed to a virtualizing list which only realizes what the viewport needs.
///
/// Every block carries the source slice it came from. Rendering a block never
/// requires looking at the rest of the document, which is the property the old
/// single-FlowDocument-per-message design did not have.
/// </summary>
public abstract record RenderBlock
{
    /// <summary>Byte range of this block in the message body it was parsed from.</summary>
    public required int SourceStart { get; init; }

    public required int SourceLength { get; init; }

    /// <summary>
    /// Stable identity for list diffing: same key means the realized element can
    /// be reused untouched. Derived from position plus a content hash so an edit
    /// that shifts everything below it does not invalidate blocks whose text is
    /// unchanged.
    /// </summary>
    public required string Key { get; init; }
}

/// <summary>Prose. <see cref="Markdown"/> still carries inline syntax
/// (emphasis, links, inline code) for the view layer to turn into inlines.</summary>
public sealed record ParagraphBlock : RenderBlock
{
    public required string Markdown { get; init; }
}

public sealed record HeadingBlock : RenderBlock
{
    public required int Level { get; init; }
    public required string Markdown { get; init; }
}

public sealed record CodeBlock : RenderBlock
{
    public required string Language { get; init; }
    public required string Code { get; init; }

    /// <summary>Line count, precomputed so the view can decide whether to show a
    /// truncated preview without touching the string again on the UI thread.</summary>
    public required int LineCount { get; init; }
}

public sealed record QuoteBlock : RenderBlock
{
    public required string Markdown { get; init; }
}

/// <summary>One entry in a list, flattened with its nesting depth.</summary>
/// <param name="Markdown">Inline markdown for this item's own text, excluding
/// any nested list underneath it.</param>
/// <param name="Depth">0 for a top-level item, 1 for the first nesting, and so on.</param>
/// <param name="Ordered">Whether this item's immediate parent list is numbered.</param>
/// <param name="Number">1-based position within its parent, for ordered lists.</param>
/// <param name="IsContinuation">A further block belonging to the item above it —
/// a second paragraph, or text that resumes after a nested list. Drawn at the
/// same indent but with no marker, because it is not a new entry.
///
/// One entry per block, rather than one entry per item with the item's blocks
/// concatenated, is what keeps this list's rendering cost proportional to the
/// paragraph that changed instead of to the whole item. A reasoning model
/// writing one numbered point with forty indented paragraphs under it used to
/// produce a single entry tens of thousands of characters long.</param>
public readonly record struct ListItem(
    string Markdown,
    int Depth,
    bool Ordered,
    int Number,
    bool IsContinuation = false);

public sealed record ListBlock : RenderBlock
{
    public required string Markdown { get; init; }
    public required bool IsOrdered { get; init; }

    /// <summary>
    /// The list flattened into items with depth, so the view can draw real
    /// bullets and numbers. Without this the view had nothing but the raw
    /// source and rendered "- foo" literally.
    /// </summary>
    public required IReadOnlyList<ListItem> Items { get; init; }
}

public sealed record TableBlock : RenderBlock
{
    public required string Markdown { get; init; }
    public required int RowCount { get; init; }

    /// <summary>Cells as inline markdown, first row first. Ragged rows are
    /// padded by the view, not here — the parser reports what was written.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    /// <summary>True when the first row is a header row.</summary>
    public required bool HasHeader { get; init; }

    /// <summary>Per-column alignment: -1 left/unset, 0 centre, 1 right.</summary>
    public required IReadOnlyList<int> Alignments { get; init; }
}

public sealed record MathBlock : RenderBlock
{
    public required string Latex { get; init; }
}

public sealed record ImageBlock : RenderBlock
{
    public required string Url { get; init; }
    public string? Alt { get; init; }
    public string? Title { get; init; }
}

public sealed record ThematicBreakBlock : RenderBlock;

/// <summary>
/// A MolaGPT tool-status / analysis / image-card unit lifted out of the body by
/// <see cref="MolaGptMarkupSplitter"/>.
///
/// The whole <see cref="MolaGptMarkupSplitter.MarkupUnit"/> is carried rather
/// than a flattened copy of it, so both desktop renderers receive the parsed
/// label, phase and search chips without parsing the HTML a second time.
/// </summary>
public sealed record MarkupUnitBlock : RenderBlock
{
    public required MolaGptMarkupSplitter.MarkupUnit Unit { get; init; }

    /// <summary>
    /// True when some rendered content follows this unit (another card, an
    /// image, or non-blank prose). A DSanalysis panel collapses itself once the
    /// answer has moved past it, so the card builder needs this.
    /// </summary>
    public required bool HasFollowingContent { get; init; }

    public MarkupUnitKind UnitKind => Unit.Kind;
}

/// <summary>Anything the parser could not classify — rendered as plain text
/// rather than dropped, which is what the old renderer did on a parse failure.</summary>
public sealed record RawTextBlock : RenderBlock
{
    public required string Text { get; init; }
}

/// <summary>
/// The parsed form of one message body: an ordered, self-contained block list
/// plus the hash of the source it was produced from, so a re-render can be
/// skipped when nothing changed.
/// </summary>
public sealed class RenderDocument
{
    public RenderDocument(string sourceHash, string source, IReadOnlyList<RenderBlock> blocks)
    {
        SourceHash = sourceHash;
        Source = source;
        Blocks = blocks;
    }

    /// <summary>Hash of the <em>original</em> body, for change detection.</summary>
    public string SourceHash { get; }

    /// <summary>
    /// The body after <see cref="MolaGptMarkupSplitter.NormalizeOutputSegmentMarkers"/>.
    /// Every block's <see cref="RenderBlock.SourceStart"/> indexes into this
    /// string, not into the raw body it was parsed from.
    /// </summary>
    public string Source { get; }

    public IReadOnlyList<RenderBlock> Blocks { get; }

    public static RenderDocument Empty { get; } = new(string.Empty, string.Empty, Array.Empty<RenderBlock>());
}
