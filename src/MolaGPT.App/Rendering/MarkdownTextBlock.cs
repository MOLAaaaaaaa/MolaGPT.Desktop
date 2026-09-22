using Avalonia;
using Avalonia.Controls;          // ResourceNodeExtensions.TryFindResource
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MolaGPT.Core.Models;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Renders one block's worth of inline markdown — emphasis, inline code, links,
/// strikethrough, inline images — into Avalonia inlines.
///
/// The block-level split already happened in MolaGPT.Presentation; what arrives
/// here is a single paragraph, heading, quote or list item that still carries
/// inline syntax. Parsing it per row rather than per message is what keeps the
/// work proportional to what the viewport shows: a row that is never scrolled to
/// is never parsed.
///
/// Deliberately a SelectableTextBlock and not a rich document host. Selection
/// within a block is what users actually reach for; selection spanning blocks
/// is a separate problem and is not solved by making every row heavier.
/// </summary>
public sealed class MarkdownTextBlock : Avalonia.Controls.SelectableTextBlock
{
    // Autolinks are on because a bare URL in an answer is a link the user will
    // try to click, and CommonMark only treats "<https://…>" as one.
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .Build();

    private static readonly Cursor s_hand = new(StandardCursorType.Hand);

    /// <summary>How far the pointer may travel between press and release and
    /// still count as a click rather than a selection. Four pixels is Windows'
    /// own drag threshold (SM_CXDRAG), which is the number a hand that meant to
    /// click stays inside.</summary>
    private const double ClickSlop = 4;

    /// <summary>
    /// What a <see cref="LineBreak"/> costs in the text source — two, not one:
    /// Avalonia emits it as a TextEndOfLine sized for a CRLF. Counting it as a
    /// single character slides every link after a soft break one position along,
    /// which puts the clickable rectangle one glyph off the words it belongs to.
    /// Measured against Avalonia 12.1.1 with a probe that located links by their
    /// underline and compared that against this bookkeeping; redo that if the
    /// hit region ever starts looking offset by a character.
    /// </summary>
    private const int LineBreakLength = 2;

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    /// <summary>Colour for inline code and link runs. Set from styles so the
    /// theme variant drives it like everything else.</summary>
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> CodeBackgroundProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, IBrush?>(nameof(CodeBackground));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? CodeBackground
    {
        get => GetValue(CodeBackgroundProperty);
        set => SetValue(CodeBackgroundProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Avalonia.Controls.SelectableTextBlock);

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((x, _) => x.Rebuild());
        AccentBrushProperty.Changed.AddClassHandler<MarkdownTextBlock>((x, _) => x.Rebuild());
        CodeBackgroundProperty.Changed.AddClassHandler<MarkdownTextBlock>((x, _) => x.Rebuild());
        FontSizeProperty.Changed.AddClassHandler<MarkdownTextBlock>((x, _) => x.Rebuild());
        FontWeightProperty.Changed.AddClassHandler<MarkdownTextBlock>((x, _) => x.Rebuild());

        // The rebuild when the flag goes *false* is the one that matters. A
        // trailing fade that is never taken off leaves the last words of a
        // finished answer permanently dimmed, and nothing else would ever
        // rebuild a row that has stopped changing.
        StreamTailFade.IsTailProperty.Changed.AddClassHandler<MarkdownTextBlock>(
            (x, _) => x.Rebuild());
    }

    private bool _attached;
    private bool _dirty;
    private FontFamily? _latin;
    private FontFamily? _cjk;
    private IReadOnlyDictionary<string, string>? _protectedMath;
    private bool _containsInlineMath;

    /// <summary>Whether a source pill was drawn into this paragraph. Kept apart
    /// from <see cref="_containsInlineMath"/> because they want the same escape
    /// from a fixed line height for different reasons — a formula is too tall,
    /// a pill is only a little taller than the text but overlaps the next line
    /// all the same.</summary>
    private bool _containsInlineBox;
    private bool _usingAdaptiveLineHeight;
    private double _configuredLineHeight;
    private double _configuredLineSpacing;

    private IBrush? _dimBrush;
    private Color _dimSource;

    /// <summary>
    /// Where each link sits in the text the layout was built from.
    ///
    /// Offsets rather than references to the runs a link was drawn as: the
    /// trailing fade rewrites the last run after the fact, and a map keyed on
    /// run identity would come apart on exactly the row that is still being
    /// written. Offsets survive it, because the text does not change — only how
    /// many runs it is split across.
    /// </summary>
    private readonly record struct LinkSpan(int Start, int Length, string Url);

    private readonly List<LinkSpan> _links = new();

    /// <summary>The same links as drawn rectangles, in this control's
    /// coordinates. Filled after every layout pass; the only thing a pointer
    /// ever consults. See <see cref="UpdateLinkRects"/> for why the hit test
    /// may not go near the text layout itself.</summary>
    private readonly List<(Rect Bounds, string Url)> _linkRects = new();

    /// <summary>Running index into the text source while inlines are being
    /// built. This is the index space <c>TextLayout</c> hit tests report in: a
    /// Run counts its characters, an InlineUIContainer counts one, a LineBreak
    /// counts <see cref="LineBreakLength"/>. Everything is appended through
    /// <see cref="Add"/> so the count cannot drift away from what the layout
    /// sees.</summary>
    private int _cursor;

    private string? _pressedUrl;
    private Point _pressedPoint;

    /// <summary>
    /// Building is deferred until the control is in the tree, because the Latin
    /// and CJK faces are theme resources and a detached control resolves neither.
    /// Building eagerly and again on attach would parse every row twice, which is
    /// precisely the per-message cost this renderer exists to remove.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _latin = Resolve("Font.Latin");
        _cjk = Resolve("Font.Cjk");
        if (_dirty) Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    /// <summary>
    /// The URL under a point in this control's coordinates, or null.
    ///
    /// A plain walk of the rectangles cached by the last layout pass — see
    /// <see cref="UpdateLinkRects"/> for why it must not consult the text
    /// layout itself. Public because every caller that wants to know "is the
    /// pointer on a link" — this control, and anything that later wants a
    /// context menu on one — has to ask the same question.
    /// </summary>
    public string? LinkAt(Point point)
    {
        foreach (var (bounds, url) in _linkRects)
        {
            if (bounds.Contains(point)) return url;
        }

        return null;
    }

    /// <summary>
    /// Turns the link offsets into the rectangles they were drawn in, once per
    /// layout pass.
    ///
    /// This has to happen here and not on the pointer event, because by the
    /// time a press is handled the layout may be gone: SelectableTextBlock
    /// invalidates it whenever the selection moves — selection colouring
    /// re-splits the runs — and the base class sets a selection on every press.
    /// Touching <c>TextLayout</c> after that rebuilds it outside a measure, with
    /// no width constraint, and it comes back empty: no lines, no rectangles,
    /// no link. That is what made the first click on a link do nothing and a
    /// second one work if a layout pass happened to land in between.
    ///
    /// Cached rectangles also go stale in the harmless direction. A selection
    /// change does not move a glyph, so yesterday's rectangles are still
    /// today's; and anything that does move text invalidates measure, which
    /// brings us straight back here.
    /// </summary>
    private void UpdateLinkRects()
    {
        _linkRects.Clear();
        if (_links.Count == 0) return;

        var layout = TextLayout;
        if (layout.TextLines.Count == 0) return;

        var offset = new Vector(Padding.Left, Padding.Top);
        foreach (var span in _links)
        {
            foreach (var rect in layout.HitTestTextRange(span.Start, span.Length))
            {
                _linkRects.Add((rect.Translate(offset), span.Url));
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateLinkRects();
        return size;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ShowLinkCursor(LinkAt(e.GetPosition(this)) is not null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ShowLinkCursor(false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Base first: it takes the pointer capture and starts the selection,
        // and a link must not cost the user the ability to select the sentence
        // it sits in.
        base.OnPointerPressed(e);

        _pressedUrl = null;
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

        // The second click of a double click is a word select, the third a line
        // select. Only the first one is ever a link click — and it has already
        // opened the link by then, which is what a browser does too.
        if (e.ClickCount > 1) return;

        _pressedPoint = e.GetPosition(this);
        _pressedUrl = LinkAt(_pressedPoint);
    }

    /// <summary>
    /// A link opens on release, not on press. Opening on press would fire on
    /// every drag that happens to start on a link, and dragging across a
    /// sentence that contains one is the more common gesture by far.
    /// </summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var url = _pressedUrl;
        _pressedUrl = null;

        base.OnPointerReleased(e);

        if (url is null || e.InitialPressMouseButton != MouseButton.Left) return;

        // Distance is the only thing separating a click from a drag-select.
        //
        // Emphatically *not* "the selection is still empty": the base class
        // extends the selection on every pointer move while pressed, and a
        // move of one or two pixels is enough to cross a glyph's midpoint and
        // land on the next index. Requiring an empty selection therefore threw
        // away most real clicks and kept the ones made with a perfectly steady
        // hand, which reads as "links work maybe one time in five".
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressedPoint.X) > ClickSlop
            || Math.Abs(point.Y - _pressedPoint.Y) > ClickSlop) return;

        if (LinkAt(point) != url) return;

        // The press left a character or two selected on its way here. Opening a
        // link is not a selection gesture, so it should not leave one behind.
        ClearSelection();

        LinkLauncher.Open(url);
        e.Handled = true;
    }

    /// <summary>Local value while over a link, cleared back to the styled one
    /// after — the theme's I-beam is what prose is supposed to show.</summary>
    private void ShowLinkCursor(bool over)
    {
        if (over)
        {
            if (!ReferenceEquals(Cursor, s_hand)) Cursor = s_hand;
        }
        else if (ReferenceEquals(Cursor, s_hand))
        {
            ClearValue(CursorProperty);
        }
    }

    /// <summary>
    /// Splits the newest words off the trailing run and draws them dimmed.
    ///
    /// Deliberately a function of the text rather than an animation. This app
    /// draws through a low-latency swap chain that stops producing frames when
    /// nothing changes, so an animated value can come to rest short of its final
    /// keyframe and stay there; opacity baked into a run has no in-between state
    /// to be stranded in. It is also why the flag turning off rebuilds the
    /// inlines rather than fading them back up: the effect is removed, not
    /// animated away, so there is no frame it can stop on.
    ///
    /// Only the final run is split, and only once — see
    /// <see cref="StreamTailFade"/> for why a second boundary is not affordable.
    /// When something else ends the paragraph, a closing bold marker or an
    /// inline image, the tail is shorter for the frame or two until plain text
    /// follows it, which is not worth walking the inline list backwards to
    /// avoid.
    /// </summary>
    private void ApplyTailFade(InlineCollection target)
    {
        if (!StreamTailFade.IsEnabled || !GetValue(StreamTailFade.IsTailProperty)) return;
        if (target.Count == 0 || target[^1] is not Run tail) return;
        if (tail.Text is not { Length: > 0 } text) return;
        if ((tail.Foreground ?? Foreground) is not ISolidColorBrush solid) return;

        var start = StreamTailFade.TailStart(text);
        if (start >= text.Length) return;

        // Straight to the collection, not through Add: this splits one run into
        // two without changing a character, so the link map still holds.
        target.RemoveAt(target.Count - 1);
        if (start > 0) target.Add(CopyRun(tail, text[..start], tail.Foreground));
        target.Add(CopyRun(tail, text[start..], Dim(solid.Color)));
    }

    /// <summary>Same run, different text and colour. Everything the inline
    /// builders set has to come across, or the tail of a bold sentence loses its
    /// weight and a code span loses its tint.</summary>
    private static Run CopyRun(Run source, string text, IBrush? foreground) => new(text)
    {
        FontFamily = source.FontFamily,
        FontStyle = source.FontStyle,
        FontWeight = source.FontWeight,
        Background = source.Background,
        TextDecorations = source.TextDecorations,
        Foreground = foreground
    };

    /// <summary>Cached against the colour it came from: the same brush is wanted
    /// again on every delta of every streaming row.</summary>
    private IBrush Dim(Color color)
    {
        if (_dimBrush is not null && _dimSource == color) return _dimBrush;

        _dimSource = color;
        return _dimBrush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(color.A * StreamTailFade.TailOpacity), color.R, color.G, color.B));
    }

    private void Rebuild()
    {
        if (!_attached)
        {
            _dirty = true;
            return;
        }
        _dirty = false;

        // The rectangles go too: the text is about to change under them, and
        // until the layout pass that follows refills them a link is better
        // inert than pointing at where it used to be.
        _links.Clear();
        _linkRects.Clear();
        _cursor = 0;

        var source = Markdown;
        if (string.IsNullOrEmpty(source))
        {
            Inlines?.Clear();
            Text = string.Empty;
            _containsInlineMath = false;
            _containsInlineBox = false;
            ShowLinkCursor(false);
            UpdateLineMetrics();
            return;
        }

        _containsInlineMath = false;
        _containsInlineBox = false;

        InlineCollection target;
        if (Inlines is null)
        {
            target = new InlineCollection();
            Inlines = target;
        }
        else
        {
            target = Inlines;
            target.Clear();
        }

        try
        {
            var protectedSource = InlineMath.Protect(source, out _protectedMath);
            var document = Markdig.Markdown.Parse(protectedSource, s_pipeline);
            var wrote = AppendBlocks(target, document, first: true);

            // A block Markdig folds away entirely (a lone reference definition,
            // stray syntax) must still show something rather than vanish.
            if (!wrote) Add(target, new Run(source), source.Length);
        }
        catch
        {
            // Never let a malformed fragment blank a row mid-stream.
            target.Clear();
            _links.Clear();
            _cursor = 0;
            Add(target, new Run(source), source.Length);
            _containsInlineMath = false;
            _containsInlineBox = false;
        }
        finally
        {
            _protectedMath = null;
            ApplyTailFade(target);
            UpdateLineMetrics();

            // A streaming row can lose its last link between deltas. Nothing
            // else would take the hand cursor back until the pointer moved.
            if (_links.Count == 0) ShowLinkCursor(false);
        }
    }

    /// <summary>
    /// Walks the block tree, not just its top level.
    ///
    /// A blockquote and a list are ContainerBlocks whose text lives one or more
    /// levels down. Matching only top-level leaves meant a quote produced no
    /// inlines at all and fell through to printing its own source, which is how
    /// "&gt; **bold**" ended up on screen with the markers still in it.
    /// </summary>
    private bool AppendBlocks(InlineCollection target, ContainerBlock container, bool first)
    {
        var wrote = false;

        foreach (var block in container)
        {
            switch (block)
            {
                case LeafBlock { Inline: { } inlines }:
                    if (!first || wrote) Add(target, new LineBreak(), LineBreakLength);
                    Append(target, inlines, FontStyle.Normal, FontWeight, strike: false);
                    wrote = true;
                    break;

                case Markdig.Syntax.ListItemBlock item:
                {
                    if (!first || wrote) Add(target, new LineBreak(), LineBreakLength);
                    // A nested list inside a quote still has to read as a list;
                    // the structural MarkdownListView only handles top-level ones.
                    Add(target, new Run("• "), 2);
                    wrote |= AppendBlocks(target, item, first: true);
                    break;
                }

                case ContainerBlock nested:
                    if (AppendBlocks(target, nested, first && !wrote)) wrote = true;
                    break;

                case LeafBlock leaf when leaf.Lines.Count > 0:
                    if (!first || wrote) Add(target, new LineBreak(), LineBreakLength);
                    AppendText(
                        target, leaf.Lines.ToString(),
                        FontStyle.Normal, FontWeight, strike: false, null, null);
                    wrote = true;
                    break;
            }
        }

        return wrote;
    }

    private void Append(
        InlineCollection target, ContainerInline container,
        FontStyle style, FontWeight weight, bool strike)
    {
        // One <ref> naming three sources arrives here as three adjacent citation
        // links, and it has to leave as one pill. Folding them means looking past
        // the current inline, so the ones already folded in are counted off here
        // rather than visited again.
        var folded = 0;

        foreach (var inline in container)
        {
            if (folded > 0)
            {
                folded--;
                continue;
            }

            switch (inline)
            {
                case LiteralInline literal:
                    AppendText(target, literal.Content.ToString(), style, weight, strike, null, null);
                    break;

                case EmphasisInline emphasis:
                {
                    var (nextStyle, nextWeight, nextStrike) = emphasis.DelimiterChar switch
                    {
                        '~' => (style, weight, true),
                        _ when emphasis.DelimiterCount >= 2 => (style, FontWeight.SemiBold, strike),
                        _ => (FontStyle.Italic, weight, strike)
                    };
                    Append(target, emphasis, nextStyle, nextWeight, nextStrike);
                    break;
                }

                case CodeInline code:
                {
                    // Code keeps the author's bytes: no script split, no
                    // full-width punctuation, no substitute face.
                    //
                    // Foreground is left inherited on purpose. Tinting it with
                    // the accent turned every inline span brand-pink, which in a
                    // table of library names is most of the cell — the original
                    // marks code with the tinted background alone.
                    var run = new Run(code.Content) { FontFamily = MonoFamily() };
                    if (CodeBackground is { } background) run.Background = background;
                    Add(target, Style(run, style, weight, strike), code.Content.Length);
                    break;
                }

                // Images have to be matched before links: LinkInline covers both,
                // and a case on LinkInline alone renders "![alt](url)" as a
                // hyperlink showing the alt text.
                case LinkInline { IsImage: true } image:
                    AppendInlineImage(target, image);
                    break;

                case LinkInline citation when IsCitation(citation):
                {
                    var group = new List<Citation> { ToCitation(citation) };
                    for (var probe = citation.NextSibling;
                         probe is LinkInline more && IsCitation(more);
                         probe = probe.NextSibling)
                    {
                        group.Add(ToCitation(more));
                        folded++;
                    }

                    AppendCitation(target, group);
                    break;
                }

                case LinkInline link:
                {
                    var start = _cursor;
                    AppendText(
                        target, LinkText(link), style, weight, strike,
                        AccentBrush, Avalonia.Media.TextDecorations.Underline);
                    Track(link.Url, start);
                    break;
                }

                case LineBreakInline:
                    Add(target, new LineBreak(), LineBreakLength);
                    break;

                case AutolinkInline autolink:
                {
                    var start = _cursor;
                    AppendText(
                        target, autolink.Url, style, weight, strike,
                        AccentBrush, Avalonia.Media.TextDecorations.Underline);
                    Track(autolink.Url, start);
                    break;
                }

                case HtmlEntityInline entity:
                    AppendText(
                        target, entity.Transcoded.ToString(), style, weight, strike,
                        null, null);
                    break;

                case ContainerInline nested:
                    Append(target, nested, style, weight, strike);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Emits prose text with font fallback at Latin↔CJK boundaries.
    /// </summary>
    private void AppendText(
        InlineCollection target, string text,
        FontStyle style, FontWeight weight, bool strike,
        IBrush? foreground, TextDecorationCollection? decorations)
    {
        if (text.Length == 0) return;

        if (AppendProtectedMath(target, text, style, weight, strike, foreground, decorations))
            return;

        // Math is extracted before anything else touches the text: full-width
        // punctuation inside a formula would corrupt it, and the script splitter
        // would cut it into pieces.
        if (InlineMath.Find(text) is { Count: > 0 } formulas)
        {
            var last = 0;
            foreach (Match match in formulas)
            {
                if (match.Index > last)
                    AppendPlain(target, text[last..match.Index], style, weight, strike, foreground, decorations);

                var formula = InlineMath.Formula(match);
                if (formula.Length == 0)
                    AppendPlain(target, match.Value, style, weight, strike, foreground, decorations);
                else
                {
                    _containsInlineMath = true;
                    Add(target, new InlineUIContainer(new MathView
                    {
                        Latex = formula,
                        FormulaSize = FontSize,
                        Foreground = foreground ?? Foreground,
                        Margin = new Thickness(1, 0, 1, 0)
                    })
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    }, 1);
                }

                last = match.Index + match.Length;
            }

            if (last < text.Length)
                AppendPlain(target, text[last..], style, weight, strike, foreground, decorations);
            return;
        }

        AppendPlain(target, text, style, weight, strike, foreground, decorations);
    }

    private bool AppendProtectedMath(
        InlineCollection target, string text,
        FontStyle style, FontWeight weight, bool strike,
        IBrush? foreground, TextDecorationCollection? decorations)
    {
        var formulas = _protectedMath;
        if (formulas is null
            || text.IndexOf(InlineMath.PlaceholderPrefix, StringComparison.Ordinal) < 0)
        {
            return false;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = text.IndexOf(InlineMath.PlaceholderPrefix, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                AppendPlain(target, text[cursor..], style, weight, strike, foreground, decorations);
                break;
            }

            if (start > cursor)
                AppendPlain(target, text[cursor..start], style, weight, strike, foreground, decorations);

            var end = text.IndexOf(
                InlineMath.PlaceholderSuffix,
                start + InlineMath.PlaceholderPrefix.Length,
                StringComparison.Ordinal);
            if (end < 0)
            {
                AppendPlain(target, text[start..], style, weight, strike, foreground, decorations);
                break;
            }

            var placeholder = text[start..(end + InlineMath.PlaceholderSuffix.Length)];
            if (formulas.TryGetValue(placeholder, out var formula))
            {
                _containsInlineMath = true;
                Add(target, new InlineUIContainer(new MathView
                {
                    Latex = formula,
                    FormulaSize = FontSize,
                    Foreground = foreground ?? Foreground,
                    Margin = new Thickness(1, 0, 1, 0)
                })
                {
                    BaselineAlignment = BaselineAlignment.Center
                }, 1);
            }
            else
            {
                AppendPlain(target, placeholder, style, weight, strike, foreground, decorations);
            }

            cursor = end + InlineMath.PlaceholderSuffix.Length;
        }

        return true;
    }

    /// <summary>
    /// A fixed LineHeight clips an InlineUIContainer to that line even when a
    /// fraction, radical or stacked script is much taller. Use natural line
    /// measurement for paragraphs containing math, and convert the configured
    /// 24px line height into spacing so ordinary text lines keep the same rhythm.
    /// </summary>
    private void UpdateLineMetrics()
    {
        if (_containsInlineMath || _containsInlineBox)
        {
            if (!_usingAdaptiveLineHeight)
            {
                _configuredLineHeight = LineHeight;
                _configuredLineSpacing = LineSpacing;
                _usingAdaptiveLineHeight = true;
            }

            LineHeight = double.NaN;
            if (!double.IsNaN(_configuredLineHeight))
            {
                LineSpacing = Math.Max(
                    _configuredLineSpacing,
                    Math.Max(0, _configuredLineHeight - FontSize * 1.2));
            }
            return;
        }

        if (!_usingAdaptiveLineHeight) return;
        LineHeight = _configuredLineHeight;
        LineSpacing = _configuredLineSpacing;
        _usingAdaptiveLineHeight = false;
    }

    private void AppendPlain(
        InlineCollection target, string text,
        FontStyle style, FontWeight weight, bool strike,
        IBrush? foreground, TextDecorationCollection? decorations)
    {
        if (text.Length == 0) return;

        var latin = _latin;
        var cjk = _cjk;
        if (latin is null || cjk is null)
        {
            Add(
                target,
                Decorate(Style(new Run(text), style, weight, strike), foreground, decorations),
                text.Length);
            return;
        }

        foreach (var (piece, isCjk) in CjkTypography.SplitByScript(text))
        {
            var run = new Run(piece) { FontFamily = isCjk ? cjk : latin };
            Add(
                target,
                Decorate(Style(run, style, weight, strike), foreground, decorations),
                piece.Length);
        }
    }

    /// <summary>
    /// The one way an inline reaches the collection, so the running index stays
    /// in step with what the layout will report. Getting a length wrong here
    /// does not misdraw anything — it silently shifts every link after it, and
    /// the pointer starts resolving to the wrong URL.
    /// </summary>
    private void Add(InlineCollection target, Avalonia.Controls.Documents.Inline inline, int length)
    {
        target.Add(inline);
        _cursor += length;
    }

    /// <summary>Records the text just appended as a link. Unopenable schemes are
    /// dropped here rather than at click time, so the cursor and the click agree
    /// on what is live.</summary>
    private void Track(string? url, int start)
    {
        if (_cursor <= start || !LinkLauncher.CanOpen(url)) return;
        _links.Add(new LinkSpan(start, _cursor - start, url!));
    }

    /// <summary>
    /// An image that shares a paragraph with text. Sized modestly and loaded
    /// off the UI thread; a lone image is a <see cref="MarkdownImageView"/> card
    /// instead, decided by the parser.
    /// </summary>
    private void AppendInlineImage(InlineCollection target, LinkInline image)
    {
        if (image.Url is not { Length: > 0 } url)
        {
            AppendText(target, LinkText(image), FontStyle.Normal, FontWeight.Normal, false, null, null);
            return;
        }

        var control = new Image
        {
            MaxWidth = 360,
            MaxHeight = 240,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapInterpolationMode(control, BitmapInterpolationMode.HighQuality);
        ToolTip.SetTip(control, url);

        Add(target, new InlineUIContainer(control), 1);
        LoadInline(control, url);
    }

    private static async void LoadInline(Image target, string url)
    {
        try
        {
            var bitmap = await ImageSourceLoader.LoadAsync(url, 540);
            if (bitmap is null) return;
            await Dispatcher.UIThread.InvokeAsync(() => target.Source = bitmap);
        }
        catch (OperationCanceledException)
        {
            // Row went away while the fetch was in flight.
        }
    }

    /// <summary>Whether this link is a citation marker rather than prose the
    /// model wrote. See <see cref="SourceReference.CitationTitlePrefix"/> for why
    /// the flag rides in the title.</summary>
    private static bool IsCitation(LinkInline link) =>
        link.Title is { } title
        && title.StartsWith(SourceReference.CitationTitlePrefix, StringComparison.Ordinal);

    /// <summary>
    /// A citation drawn as the source pill from the web client: favicon, site,
    /// and "+n" when the one <c>&lt;ref&gt;</c> named more than one source.
    ///
    /// The pill is an <see cref="InlineUIContainer"/>, which this control has to
    /// be careful with. Two rules, both learned on screen:
    ///
    /// <list type="bullet">
    /// <item>The paragraph must drop its fixed LineHeight — see
    /// <see cref="UpdateLineMetrics"/>. A pill is taller than a line of prose,
    /// and against a fixed line height the lines it sits on overlap the ones
    /// after them.</item>
    /// <item>The pill handles its own clicks. The rectangle bookkeeping in
    /// <see cref="UpdateLinkRects"/> that every prose link rides on is no use
    /// here: a hit test over an InlineUIContainer does not fall through to the
    /// text block, it falls past it to the window, so a pointer over a
    /// non-hit-testable pill reaches nothing at all.</item>
    /// </list>
    /// </summary>
    private void AppendCitation(InlineCollection target, IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0) return;

        _containsInlineBox = true;

        Add(
            target,
            new InlineUIContainer(CitationPill.Build(citations, FontSize, this))
            {
                // Centre is the closest of the three positions Avalonia actually
                // distinguishes here — Top/Baseline/TextTop/Superscript all land
                // on the line's top edge, Bottom/TextBottom/Subscript on its
                // bottom. The residue is taken out by the pill's own render
                // transform; see CitationPill.Build.
                BaselineAlignment = BaselineAlignment.Center
            },
            1);
    }

    /// <summary>Unpacks one citation link into what the pill and its card need.
    /// The site is derived here rather than carried, because the backend does not
    /// send one — see <see cref="SourceReference.SiteOf"/>.</summary>
    private static Citation ToCitation(LinkInline link)
    {
        SourceReference.TryUnpack(link.Title, out var date, out var title);
        var url = link.Url ?? string.Empty;
        return new Citation(SourceReference.SiteOf(url), title, url, date);
    }

    private static Run Decorate(Run run, IBrush? foreground, TextDecorationCollection? decorations)
    {
        if (foreground is not null) run.Foreground = foreground;
        if (decorations is not null && run.TextDecorations is null) run.TextDecorations = decorations;
        return run;
    }

    private static Run Style(Run run, FontStyle style, FontWeight weight, bool strike)
    {
        run.FontStyle = style;
        run.FontWeight = weight;
        if (strike) run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
        return run;
    }

    private static string LinkText(LinkInline link)
    {
        if (link.FirstChild is null) return link.Url ?? string.Empty;

        var text = string.Empty;
        foreach (var child in link)
        {
            if (child is LiteralInline literal) text += literal.Content.ToString();
        }
        return text.Length > 0 ? text : link.Url ?? string.Empty;
    }

    private FontFamily MonoFamily() => Resolve("Font.Mono") ?? FontFamily.Default;

    private FontFamily? Resolve(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as FontFamily : null;
}
