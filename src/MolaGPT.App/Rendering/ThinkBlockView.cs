using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;   // FindDataTemplate
using Avalonia.Input.Platform;      // ClipboardExtensions.SetTextAsync
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using MolaGPT.Presentation;

namespace MolaGPT.App.Rendering;

/// <summary>
/// A model's chain-of-thought, as the foldable card the WPF build drew:
///
/// <code>
/// ┌ ▌ ● 思考中… 12.3 s        ▾ │
/// │   (markdown body)            │
/// └──────────────────────────────┘
/// </code>
///
/// Two things here are behavioural, not decorative:
///
///   - Its expanded state is bound to the thinking segment, so virtualization
///     cannot undo the user's choice when the row is realized again.
///   - The body is **not parsed until it is expanded**. Because of the rule
///     above most finished blocks are never opened, so the reasoning — often
///     longer than the answer — costs nothing to have on the page.
/// </summary>
public sealed class ThinkBlockView : TemplatedControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<ThinkBlockView, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsThinkingProperty =
        AvaloniaProperty.Register<ThinkBlockView, bool>(nameof(IsThinking));

    public static readonly StyledProperty<double> ElapsedSecondsProperty =
        AvaloniaProperty.Register<ThinkBlockView, double>(nameof(ElapsedSeconds));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ThinkBlockView, bool>(nameof(IsExpanded), true);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsThinking
    {
        get => GetValue(IsThinkingProperty);
        set => SetValue(IsThinkingProperty, value);
    }

    public double ElapsedSeconds
    {
        get => GetValue(ElapsedSecondsProperty);
        set => SetValue(ElapsedSecondsProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private readonly Ellipse _dot;
    private readonly TextBlock _status;
    private readonly TextBlock _chevron;
    private readonly StackPanel _body;
    private readonly RevealPresenter _reveal;
    private readonly Button _header;
    private readonly Border _stripe;
    private readonly Border _shell;

    /// <summary>The blocks <see cref="_body"/>'s children were built from, index
    /// for index. Compared against the next parse to find what actually changed.</summary>
    private readonly List<RenderBlock> _rendered = new();

    /// <summary>Last parse, so a delta re-parses only the trailing blocks.</summary>
    private RenderDocument? _document;
    private bool _bodyStale = true;

    static ThinkBlockView()
    {
        IsThinkingProperty.Changed.AddClassHandler<ThinkBlockView>((x, e) =>
        {
            x.UpdateStatus();
        });
        ElapsedSecondsProperty.Changed.AddClassHandler<ThinkBlockView>((x, _) => x.UpdateStatus());
        SourceProperty.Changed.AddClassHandler<ThinkBlockView>((x, _) => x.Invalidate());
        IsExpandedProperty.Changed.AddClassHandler<ThinkBlockView>((x, _) => x.ApplyExpanded());
    }

    public ThinkBlockView()
    {
        _dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        _status = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };

        _chevron = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            Transitions =
            [
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(180),
                    Easing = new CubicEaseOut()
                }
            ]
        };
        _chevron.Classes.Add("icon");

        var headerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        headerContent.Children.Add(_dot);
        Grid.SetColumn(_status, 1);
        headerContent.Children.Add(_status);
        Grid.SetColumn(_chevron, 2);
        headerContent.Children.Add(_chevron);

        _header = new Button
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 9),
            MinHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = headerContent
        };
        _header.Click += (_, _) => IsExpanded = !IsExpanded;

        _body = new StackPanel { Margin = new Thickness(12, 0, 12, 10) };
        _reveal = new RevealPresenter
        {
            Child = _body,
            IsOpen = true,
            PerformanceLabel = "thinking"
        };

        var column = new StackPanel();
        column.Children.Add(_header);
        column.Children.Add(_reveal);

        _stripe = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2, 0, 0, 2)
        };

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("4,*") };
        layout.Children.Add(_stripe);
        Grid.SetColumn(column, 1);
        layout.Children.Add(column);

        _shell = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = layout
        };

        LogicalChildren.Add(_shell);
        VisualChildren.Add(_shell);

        BuildContextMenu();
        ApplyExpanded();
    }

    /// <summary>
    /// Brushes are pulled by key rather than bound to a resource observable, and
    /// re-pulled when the variant changes. Binding each one would mean a live
    /// subscription per card for values that change at most when the user flips
    /// the theme.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyBrushes();
        ActualThemeVariantChanged += OnVariantChanged;

        // The block templates are shared with the transcript's own rows and are
        // therefore found by walking up from here. A body built before the card
        // was attached would have found none of them, so the first real build
        // has to wait for this.
        if (IsExpanded) BuildBody();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnVariantChanged;
    }

    private void OnVariantChanged(object? sender, EventArgs e) => ApplyBrushes();

    private void ApplyBrushes()
    {
        _shell.Background = Brush("Brush.Primary.Blockquote");
        _stripe.Background = Brush("Brush.Primary");
        _status.Foreground = Brush("Brush.Text.Secondary");
        UpdateStatus();
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var child = (Control)VisualChildren[0];
        child.Measure(availableSize);
        return child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ((Control)VisualChildren[0]).Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void BuildContextMenu()
    {
        var copy = new MenuItem { Header = "复制思考内容" };
        copy.Click += async (_, _) =>
        {
            if (Source is not { } text || string.IsNullOrWhiteSpace(text)) return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text.Trim());
        };

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Opening += (_, _) => copy.IsEnabled = !string.IsNullOrWhiteSpace(Source);
        ContextMenu = menu;
    }

    private void UpdateStatus()
    {
        _status.Text = IsThinking
            ? $"思考中… {ElapsedSeconds:0.0} s"
            : ElapsedSeconds > 0
                ? $"思考已完成 · 用时 {ElapsedSeconds:0.0} 秒"
                : "思考已完成";

        _dot.Classes.Set("pulsing", IsThinking);
        _dot.Fill = Brush(IsThinking ? "Brush.Primary" : "Brush.Success");
    }

    private void Invalidate()
    {
        _bodyStale = true;
        if (IsExpanded) BuildBody();
    }

    private void ApplyExpanded()
    {
        _chevron.RenderTransform = TransformOperations.Parse(IsExpanded ? "rotate(90deg)" : "rotate(0deg)");

        // Built before the reveal starts, so the animation has something with a
        // real height to travel over rather than opening onto an empty panel
        // that fills in a frame later.
        if (IsExpanded) BuildBody();
        _reveal.IsOpen = IsExpanded;
    }

    /// <summary>
    /// Parses on first expand and again on each delta while open, reusing
    /// whatever the last parse already established. The card starts collapsed
    /// once the model is done, so a finished block nobody opens still costs
    /// nothing.
    ///
    /// Reasoning routinely runs far longer than the answer it precedes, and it
    /// arrives as one markdown block far more often than the answer does — a
    /// numbered point with indented paragraphs under it is a single list no
    /// matter how long it grows. So neither the transcript's row virtualization
    /// nor its per-block reuse bounds the work here, and this method has to.
    /// </summary>
    private void BuildBody()
    {
        if (!_bodyStale) return;

        // Detached: the templates below are found through the tree, so anything
        // built now would render as raw source. Stay stale; the attach handler
        // comes back for this.
        if (VisualRoot is null) return;
        _bodyStale = false;
        var traceStarted = AnimationPerformanceTrace.Timestamp();

        if (Source is not { Length: > 0 } source)
        {
            _document = null;
            _rendered.Clear();
            _body.Children.Clear();
            AnimationPerformanceTrace.ThinkingBodyBuilt(traceStarted, 0, 0);
            return;
        }

        // Incremental, not Parse: a full re-read of the whole segment on every
        // delta is quadratic over a segment that streams for a minute.
        _document = MessageDocumentParser.ParseIncremental(_document, source);
        SyncBody(_document.Blocks);
        AnimationPerformanceTrace.ThinkingBodyBuilt(
            traceStarted,
            source.Length,
            _document.Blocks.Count);
    }

    /// <summary>
    /// Brings the body's children to <paramref name="next"/> with the least work.
    ///
    /// Three cases, cheapest first:
    ///
    ///   key unchanged  — the block is final; its child never hears about the
    ///                    delta at all.
    ///   same type      — the block grew. The child already there is handed the
    ///                    new value through its DataContext, so a view that can
    ///                    update itself cheaply gets the chance to; a long list
    ///                    then rebuilds the one row that changed.
    ///   different type — half-streamed markdown changed its mind (a paragraph
    ///                    became a heading, a fence closed). Rebuild that child.
    ///
    /// An <see cref="ItemsControl"/> cannot express the middle case: replacing an
    /// item discards its container, so the growing block's control was rebuilt
    /// from nothing on every delta however cheap its own update would have been.
    /// </summary>
    private void SyncBody(IReadOnlyList<RenderBlock> next)
    {
        for (var i = 0; i < next.Count; i++)
        {
            var block = next[i];

            if (i >= _rendered.Count)
            {
                _body.Children.Add(BuildChild(block));
                _rendered.Add(block);
                continue;
            }

            if (string.Equals(_rendered[i].Key, block.Key, StringComparison.Ordinal)) continue;

            if (_rendered[i].GetType() == block.GetType())
                _body.Children[i].DataContext = block;
            else
                _body.Children[i] = BuildChild(block);

            _rendered[i] = block;
        }

        while (_rendered.Count > next.Count)
        {
            _rendered.RemoveAt(_rendered.Count - 1);
            _body.Children.RemoveAt(_body.Children.Count - 1);
        }
    }

    /// <summary>
    /// The block templates live on the transcript, shared with the answer's own
    /// rows, so they are looked up rather than duplicated here. The DataContext
    /// is set explicitly because nothing else is going to: this is the panel a
    /// ContentPresenter would otherwise be standing in for.
    /// </summary>
    private Control BuildChild(RenderBlock block)
    {
        if (this.FindDataTemplate(block)?.Build(block) is { } templated)
        {
            templated.DataContext = block;
            return templated;
        }

        // No templates in scope — the card is being hosted outside the
        // transcript. Show the block's own source rather than nothing, and keep
        // it updating from the DataContext like every other child, because
        // SyncBody's reuse rule assumes exactly that.
        var fallback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        fallback.DataContextChanged += (sender, _) =>
        {
            var target = (TextBlock)sender!;
            target.Text = target.DataContext is RenderBlock current ? SourceOf(current) : null;
        };
        fallback.DataContext = block;
        return fallback;
    }

    private string SourceOf(RenderBlock block) =>
        Source is { } source
        && block.SourceStart >= 0
        && block.SourceStart + block.SourceLength <= source.Length
            ? source.Substring(block.SourceStart, block.SourceLength)
            : string.Empty;
}
