using Avalonia;
using Avalonia.Controls;          // ResourceNodeExtensions.TryFindResource
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

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
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

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
    }

    private bool _attached;
    private bool _dirty;
    private FontFamily? _latin;
    private FontFamily? _cjk;
    private IReadOnlyDictionary<string, string>? _protectedMath;
    private bool _containsInlineMath;
    private bool _usingAdaptiveLineHeight;
    private double _configuredLineHeight;
    private double _configuredLineSpacing;

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

    private void Rebuild()
    {
        if (!_attached)
        {
            _dirty = true;
            return;
        }
        _dirty = false;

        var source = Markdown;
        if (string.IsNullOrEmpty(source))
        {
            Inlines?.Clear();
            Text = string.Empty;
            _containsInlineMath = false;
            UpdateLineMetrics();
            return;
        }

        _containsInlineMath = false;

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
            if (!wrote) target.Add(new Run(source));
        }
        catch
        {
            // Never let a malformed fragment blank a row mid-stream.
            target.Clear();
            target.Add(new Run(source));
            _containsInlineMath = false;
        }
        finally
        {
            _protectedMath = null;
            UpdateLineMetrics();
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
                    if (!first || wrote) target.Add(new LineBreak());
                    Append(target, inlines, FontStyle.Normal, FontWeight, strike: false);
                    wrote = true;
                    break;

                case Markdig.Syntax.ListItemBlock item:
                {
                    if (!first || wrote) target.Add(new LineBreak());
                    // A nested list inside a quote still has to read as a list;
                    // the structural MarkdownListView only handles top-level ones.
                    target.Add(new Run("• "));
                    wrote |= AppendBlocks(target, item, first: true);
                    break;
                }

                case ContainerBlock nested:
                    if (AppendBlocks(target, nested, first && !wrote)) wrote = true;
                    break;

                case LeafBlock leaf when leaf.Lines.Count > 0:
                    if (!first || wrote) target.Add(new LineBreak());
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
        foreach (var inline in container)
        {
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
                    target.Add(Style(run, style, weight, strike));
                    break;
                }

                // Images have to be matched before links: LinkInline covers both,
                // and a case on LinkInline alone renders "![alt](url)" as a
                // hyperlink showing the alt text.
                case LinkInline { IsImage: true } image:
                    AppendInlineImage(target, image);
                    break;

                case LinkInline link:
                    AppendText(
                        target, LinkText(link), style, weight, strike,
                        AccentBrush, Avalonia.Media.TextDecorations.Underline);
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case AutolinkInline autolink:
                    AppendText(
                        target, autolink.Url, style, weight, strike,
                        AccentBrush, Avalonia.Media.TextDecorations.Underline);
                    break;

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
    /// Emits prose text, cut at Latin↔CJK boundaries and with punctuation
    /// normalized — the two rules MarkdownPresenter applied to every non-mono
    /// run before handing the document to WPF.
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
                    target.Add(new InlineUIContainer(new MathView
                    {
                        Latex = formula,
                        FormulaSize = FontSize,
                        Foreground = foreground ?? Foreground,
                        Margin = new Thickness(1, 0, 1, 0)
                    })
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    });
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
                target.Add(new InlineUIContainer(new MathView
                {
                    Latex = formula,
                    FormulaSize = FontSize,
                    Foreground = foreground ?? Foreground,
                    Margin = new Thickness(1, 0, 1, 0)
                })
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
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
        if (_containsInlineMath)
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

        text = CjkTypography.NormalizePunctuation(text);

        var latin = _latin;
        var cjk = _cjk;
        if (latin is null || cjk is null)
        {
            target.Add(Decorate(Style(new Run(text), style, weight, strike), foreground, decorations));
            return;
        }

        foreach (var (piece, isCjk) in CjkTypography.SplitByScript(text))
        {
            var run = new Run(piece) { FontFamily = isCjk ? cjk : latin };
            target.Add(Decorate(Style(run, style, weight, strike), foreground, decorations));
        }
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

        target.Add(new InlineUIContainer(control));
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
