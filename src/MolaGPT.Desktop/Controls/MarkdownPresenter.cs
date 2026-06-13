using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using System.Diagnostics;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Views;
using Markdig;
using Markdig.Syntax;
using WpfMath.Controls;
using WpfBlock = System.Windows.Documents.Block;
using MdContainerInline = Markdig.Syntax.Inlines.ContainerInline;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdLineBreakInline = Markdig.Syntax.Inlines.LineBreakInline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using MdLiteralInline = Markdig.Syntax.Inlines.LiteralInline;

namespace MolaGPT.Desktop.Controls;

/// <summary>
/// Streaming-friendly Markdown viewer with MolaGPT-specific markup support.
///
/// Two-tier diff strategy (informed by chatboxai/chatbox's React.memo +
/// per-component memoization implemented as a manual reconciler because WPF
/// has no virtual DOM):
///
///   FAST PATH — pure markdown (no &lt;blockquote class="tool-status"&gt; or
///   &lt;DSanalysis&gt; markers in the source). Used for the typical chat
///   stream:
///     1. Parse the full source to a Markdig AST (cheap, ~ms even for 10KB).
///     2. Slice source per AST top-level block.
///     3. Compare against cached slice list — keep matching prefix's
///        already-rendered <see cref="Block"/>s in place; only re-render
///        trailing changed/new blocks.
///
///   MIXED PATH — markup is interleaved with custom MolaGPT tool blocks
///   (chator.php / chatv4.php emit <c>&lt;blockquote class="tool-status..."&gt;</c>
///   and <c>&lt;DSanalysis&gt;</c> directly inside <c>delta.content</c>):
///     1. Pre-split via <see cref="MolaGptMarkupSplitter"/>.
///     2. Standard unit-level prefix diff.
///     3. For each changed Markdown unit, fall back to AST-block diff
///        within (so a markdown unit that's still streaming doesn't
///        re-render the whole thing on every chunk).
///
/// Both paths reuse the same FlowDocument — no allocation churn, no full
/// layout invalidation. ToolStatus and DSAnalysis units render via
/// <see cref="MolaGptMarkupBlocks"/> as <see cref="BlockUIContainer"/>s
/// embedded in the FlowDocument.
///
/// Other knobs:
///   - Throttle: re-render every <see cref="ThrottleMs"/> ms (default 32).
///   - Animations: opacity fade only on first paint and on stream end.
///     Per-tick fades during streaming were the dominant source of "卡顿".
///   - Resource lookup: <see cref="FrameworkElement.TryFindResource"/>.
///   - Defensive parse: malformed partial markdown / partial &lt;DSanalysis&gt;
///     falls back to plain text without taking down the UI thread.
/// </summary>
public sealed partial class MarkdownPresenter : ContentControl
{
    private const double MessageTextFontSize = 15;
    private const double MessageTextLineHeight = 24;
    private const double CodeBlockMaxWidth = 1440;
    private const double MarkdownImageMaxWidth = 720;
    private const double MarkdownImageMaxHeight = 640;
    private const double MarkdownImageCardMaxWidth = 640;
    private const double MarkdownImageCardMinWidth = 240;
    private const double MarkdownImageCardAspectRatio = 16d / 9d;
    private const double AiImageCardMaxSize = 480;
    private const double SelectionAutoScrollEdge = 48;
    private const double SelectionAutoScrollMinStep = 4;
    private const double SelectionAutoScrollMaxStep = 22;
    private const string InlineMathPlaceholderPrefix = "\uE000MolaMath";
    private const string InlineMathPlaceholderSuffix = "\uE001";

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownPresenter),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(MarkdownPresenter),
        new PropertyMetadata(false, OnIsStreamingChanged));

    public static readonly DependencyProperty SourcesProperty = DependencyProperty.Register(
        nameof(Sources), typeof(IReadOnlyList<SourceReference>), typeof(MarkdownPresenter),
        new PropertyMetadata(null, OnSourcesChanged));

    public static readonly DependencyProperty ThrottleMsProperty = DependencyProperty.Register(
        nameof(ThrottleMs), typeof(int), typeof(MarkdownPresenter),
        new PropertyMetadata(32));

    public static readonly DependencyProperty CodeBlockMaxHeightProperty = DependencyProperty.Register(
        nameof(CodeBlockMaxHeight), typeof(double), typeof(MarkdownPresenter),
        new PropertyMetadata(double.PositiveInfinity));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public IReadOnlyList<SourceReference>? Sources
    {
        get => (IReadOnlyList<SourceReference>?)GetValue(SourcesProperty);
        set => SetValue(SourcesProperty, value);
    }

    public int ThrottleMs
    {
        get => (int)GetValue(ThrottleMsProperty);
        set => SetValue(ThrottleMsProperty, value);
    }

    public double CodeBlockMaxHeight
    {
        get => (double)GetValue(CodeBlockMaxHeightProperty);
        set => SetValue(CodeBlockMaxHeightProperty, value);
    }

    private static readonly MarkdownPipeline s_streamingPipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UsePipeTables()
        .UseGridTables()
        .DisableHtml()
        .Build();

    private static readonly MarkdownPipeline s_finalPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    [GeneratedRegex(@"^\s*```(?<lang>[^\r\n`]*)\r?\n(?<code>[\s\S]*?)(?:\r?\n)?[ \t]*```\s*$")]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"^\s*```(?<lang>[^\r\n`]*)\r?\n(?<code>[\s\S]*)$")]
    private static partial Regex StreamingFencedCodeRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<fence>`{3,}|~{3,})(?<lang>[^\r\n]*)\r?\n(?<code>[\s\S]*?)^\k<indent>\k<fence>[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex EmbeddedFencedCodeRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<fence>`{3,}|~{3,})(?<lang>[^\r\n]*)\r?\n(?<code>[\s\S]*)\z", RegexOptions.Multiline)]
    private static partial Regex EmbeddedStreamingFencedCodeRegex();

    [GeneratedRegex(@"^\s*(?:\$\$(?<math>[\s\S]*?)\$\$|```(?:math|latex|tex)\s*\r?\n(?<fenced>[\s\S]*?)(?:\r?\n)?```\s*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MathBlockRegex();

    [GeneratedRegex(@"(?<!\\)\$(?<dollar>[^$\r\n]+?)(?<!\\)\$|\\\((?<paren>[\s\S]+?)\\\)|\\\[(?<bracket>[\s\S]+?)\\\]")]
    private static partial Regex LatexInlineMathRegex();

    [GeneratedRegex(@"<ref\b(?<attrs>[^>]*)>(?<inner>[\s\S]*?)</ref>|<ref\b(?<attrs2>[^>]*)/?>", RegexOptions.IgnoreCase)]
    private static partial Regex RefTagRegex();

    [GeneratedRegex(@"\bsource\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s/>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex RefSourceRegex();

    [GeneratedRegex(@"\\begin\{(?<env>pmatrix|bmatrix|Bmatrix|vmatrix|Vmatrix|matrix|smallmatrix|cases|alignedat|aligned|align\*?|gather\*?|split|array)\}(?<body>[\s\S]*?)\\end\{\k<env>\}")]
    private static partial Regex MathEnvironmentRegex();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+(?:[/:?#].*)?$")]
    private static partial Regex BareDomainRegex();

    [GeneratedRegex(@"^(?:localhost|127(?:\.\d{1,3}){3}|\[::1\])(?::\d+)?(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalhostUrlRegex();

    // A line whose ENTIRE content is a single markdown image (optionally a
    // linked image), with <4 leading spaces so 4-space indented code is left
    // alone. Used to force a blank line around such lines so Markdig parses
    // them as standalone image blocks (the block-card render path) instead of
    // fusing them into the following caption paragraph (the inline path, which
    // overflows the image over the caption text).
    [GeneratedRegex(@"^ {0,3}!\[[^\]]*\]\([^)\r\n]+\)\s*$")]
    private static partial Regex StandaloneImageLineRegex();

    [GeneratedRegex(@"^ {0,3}\[!\[[^\]]*\]\([^)\r\n]+\)\]\([^)\r\n]+\)\s*$")]
    private static partial Regex StandaloneLinkedImageLineRegex();

    [GeneratedRegex(@"^\s*(?:`{3,}|~{3,})")]
    private static partial Regex CodeFenceLineRegex();

    private static readonly DependencyProperty HyperlinkMouseDownPointProperty =
        DependencyProperty.RegisterAttached(
            "HyperlinkMouseDownPoint",
            typeof(Point),
            typeof(MarkdownPresenter),
            new PropertyMetadata(new Point(double.NaN, double.NaN)));

    private static readonly DependencyProperty HyperlinkLastOpenTickProperty =
        DependencyProperty.RegisterAttached(
            "HyperlinkLastOpenTick",
            typeof(long),
            typeof(MarkdownPresenter),
            new PropertyMetadata(0L));

    private static readonly HashSet<string> s_cSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "async", "await", "bool", "break", "case", "catch", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum",
        "event", "false", "finally", "for", "foreach", "if", "in", "int", "interface",
        "internal", "is", "long", "namespace", "new", "null", "object", "out", "override",
        "private", "protected", "public", "readonly", "record", "return", "sealed", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "using", "var", "void",
        "while"
    };

    private readonly FlowDocumentScrollViewer _viewer;
    private readonly FlowDocument _doc;
    private readonly DispatcherTimer _throttleTimer;

    private enum CachePath { Empty, FastAst, MixedUnits, StreamingOpenCode }

    private CachePath _cachePath = CachePath.Empty;

    /// <summary>FastAst-path cache: per-AST-block source slices.</summary>
    private readonly List<string> _fastBlockSources = new();

    /// <summary>MixedUnits-path cache: one entry per markup unit.</summary>
    private sealed class CachedUnit
    {
        public MarkupUnitKind Kind;
        public string Source = string.Empty;
        public string? Tag;
        public string? Inner;
        public MolaGptMarkupSplitter.Variant ToolVariant;
        public string? AnalysisPhase;
        public int BlockCount;
        public UIElement? Element;
        // Markdown-only: per-AST-block sources for in-place AST diff when this
        // unit is the streaming tail.
        public List<string>? AstBlockSources;
    }

    private sealed class MarkdownImageCardState
    {
        public MarkdownImageCardState(bool isAiGenerated)
        {
            IsAiGenerated = isAiGenerated;
        }

        public bool IsAiGenerated { get; }
        public Image? Image { get; set; }
    }

    private readonly List<CachedUnit> _mixedUnits = new();
    private readonly Dictionary<string, BitmapImage> _markdownImageCache = new(StringComparer.Ordinal);
    private bool _preserveMarkdownImagesDuringThemeRefresh;

    private string _lastRenderedSource = string.Empty;
    private MarkdownPipeline _lastUsedPipeline;
    private string _pendingSource = string.Empty;
    private bool _hasFirstPaintAnimated;
    private bool _plainTextAppendMode;
    private string _streamingOpenCodePrefix = string.Empty;
    private string _streamingOpenCodeLanguage = string.Empty;
    private TextBox? _streamingOpenCodeTextBox;
    private bool _isDraggingSelection;
    private Point _selectionDragStart = new(double.NaN, double.NaN);
    private double _selectionAutoScrollStep;
    private ScrollViewer? _selectionAutoScrollViewer;
    private Hyperlink? _pendingClickHyperlink;
    private Point _pendingClickHyperlinkStart = new(double.NaN, double.NaN);
    private Point _markdownImagePreviewStart = new(double.NaN, double.NaN);
    private sealed class SmoothTextBoxScrollState
    {
        public TextBox? Owner;
        public EventHandler? FrameHandler;
        public double StartOffset;
        public double TargetOffset;
        public DateTime AnimationStart;
        public bool Animating;
    }

    public MarkdownPresenter()
    {
        _doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Left,
            LineHeight = MessageTextLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            FontSize = MessageTextFontSize
        };

        _viewer = new FlowDocumentScrollViewer
        {
            Document = _doc,
            IsToolBarVisible = false,
            IsSelectionEnabled = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = true,
            Cursor = Cursors.IBeam
        };

        // FlowDocumentScrollViewer is a known WPF MouseWheel sink — even with
        // VerticalScrollBarVisibility=Disabled it marks every wheel tick as
        // Handled, breaking the parent ScrollViewer's scrolling. Forward the
        // event to whichever ancestor wants it (typically MainWindow's
        // MessagesScroll). This is the canonical fix; see e.g. SO #3727439.
        _viewer.PreviewMouseWheel += OnInnerPreviewMouseWheel;
        _viewer.PreviewMouseLeftButtonDown += OnViewerPreviewMouseLeftButtonDown;
        _viewer.PreviewMouseMove += OnViewerPreviewMouseMove;
        _viewer.PreviewMouseLeftButtonUp += OnViewerPreviewMouseLeftButtonUp;
        _viewer.LostKeyboardFocus += (_, _) =>
        {
            StopSelectionAutoScroll();
            ClearPendingClickHyperlink();
        };

        Content = _viewer;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        SizeChanged += (_, _) => RefreshMarkdownImageConstraints();
        Focusable = true;
        Cursor = Cursors.IBeam;

        _throttleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };
        _throttleTimer.Tick += (_, _) => Flush();

        _lastUsedPipeline = s_streamingPipeline;

        Loaded += (_, _) => ApplyTypography();
        // Defensive: when the container is recycled by VirtualizingStackPanel
        // the DataContext changes but the FlowDocument keeps stale Blocks.
        // Reset our caches on unload so the new DataContext starts clean.
        Unloaded += (_, _) => ResetAll();
    }

    private void OnViewerPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindNavigableHyperlink(e.OriginalSource as DependencyObject) is { } hyperlink)
        {
            StopSelectionAutoScroll();
            _pendingClickHyperlink = hyperlink;
            _pendingClickHyperlinkStart = e.GetPosition(_viewer);
            _viewer.Focus();
            _viewer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (IsEmbeddedInteractiveSurface(e.OriginalSource as DependencyObject))
            return;

        _viewer.Focus();
        _isDraggingSelection = true;
        _selectionDragStart = e.GetPosition(_viewer);
        _selectionAutoScrollViewer = FindOuterScrollViewer();
        SetSelectionAutoScrollStep(0);
    }

    private void OnViewerPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSelection)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopSelectionAutoScroll();
            return;
        }

        var position = e.GetPosition(_viewer);
        if (!HasSelectionDragMovedEnough(_selectionDragStart, position))
        {
            SetSelectionAutoScrollStep(0);
            return;
        }

        UpdateSelectionAutoScroll(position);
    }

    private void OnViewerPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingClickHyperlink is { } hyperlink)
        {
            var start = _pendingClickHyperlinkStart;
            ClearPendingClickHyperlink();
            if (hyperlink.NavigateUri is not null
                && IsClickGesture(start, e.GetPosition(_viewer))
                && TryOpenHyperlinkOnce(hyperlink, hyperlink.NavigateUri))
            {
                e.Handled = true;
            }
            return;
        }

        StopSelectionAutoScroll();
    }

    private void ClearPendingClickHyperlink()
    {
        _pendingClickHyperlink = null;
        _pendingClickHyperlinkStart = new Point(double.NaN, double.NaN);
        if (_viewer.IsMouseCaptured)
            _viewer.ReleaseMouseCapture();
    }

    private void UpdateSelectionAutoScroll(Point position)
    {
        var scrollViewer = _selectionAutoScrollViewer ??= FindOuterScrollViewer();
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0 || _viewer.ActualHeight <= 0)
        {
            SetSelectionAutoScrollStep(0);
            return;
        }

        Point positionInScrollViewer;
        try
        {
            positionInScrollViewer = _viewer.TranslatePoint(position, scrollViewer);
        }
        catch (InvalidOperationException)
        {
            SetSelectionAutoScrollStep(0);
            return;
        }

        var height = scrollViewer.ActualHeight;
        if (height <= 0)
            height = scrollViewer.ViewportHeight;
        if (height <= 0)
        {
            SetSelectionAutoScrollStep(0);
            return;
        }

        var step = 0d;
        if (positionInScrollViewer.Y < SelectionAutoScrollEdge)
        {
            var pressure = Math.Clamp((SelectionAutoScrollEdge - positionInScrollViewer.Y) / SelectionAutoScrollEdge, 0, 1);
            step = -CalculateSelectionAutoScrollStep(pressure);
        }
        else if (positionInScrollViewer.Y > height - SelectionAutoScrollEdge)
        {
            var pressure = Math.Clamp((positionInScrollViewer.Y - (height - SelectionAutoScrollEdge)) / SelectionAutoScrollEdge, 0, 1);
            step = CalculateSelectionAutoScrollStep(pressure);
        }

        SetSelectionAutoScrollStep(step);
    }

    private static bool HasSelectionDragMovedEnough(Point start, Point current) =>
        !double.IsNaN(start.X)
        && !double.IsNaN(start.Y)
        && (Math.Abs(current.X - start.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance);

    private static double CalculateSelectionAutoScrollStep(double pressure) =>
        SelectionAutoScrollMinStep + ((SelectionAutoScrollMaxStep - SelectionAutoScrollMinStep) * pressure);

    private void SetSelectionAutoScrollStep(double step)
    {
        if (Math.Abs(step) < 0.1)
        {
            _selectionAutoScrollStep = 0;
            CompositionTarget.Rendering -= OnSelectionAutoScrollFrame;
            return;
        }

        var wasIdle = Math.Abs(_selectionAutoScrollStep) < 0.1;
        _selectionAutoScrollStep = step;
        if (wasIdle)
            CompositionTarget.Rendering += OnSelectionAutoScrollFrame;
    }

    private void OnSelectionAutoScrollFrame(object? sender, EventArgs e)
    {
        var scrollViewer = _selectionAutoScrollViewer;
        if (!_isDraggingSelection
            || Mouse.LeftButton != MouseButtonState.Pressed
            || scrollViewer is null
            || Math.Abs(_selectionAutoScrollStep) < 0.1)
        {
            StopSelectionAutoScroll();
            return;
        }

        var next = Math.Clamp(
            scrollViewer.VerticalOffset + _selectionAutoScrollStep,
            0,
            scrollViewer.ScrollableHeight);
        scrollViewer.ScrollToVerticalOffset(next);
    }

    private void StopSelectionAutoScroll()
    {
        _isDraggingSelection = false;
        _selectionDragStart = new Point(double.NaN, double.NaN);
        _selectionAutoScrollStep = 0;
        _selectionAutoScrollViewer = null;
        CompositionTarget.Rendering -= OnSelectionAutoScrollFrame;
    }

    /// <summary>
    /// Bubble the wheel event up so the outer ScrollViewer (e.g. MainWindow's
    /// MessagesScroll) actually scrolls. We replay the same delta as a
    /// fresh routed event whose source is our parent UIElement, which is
    /// what ScrollViewer expects.
    /// </summary>
    private void OnInnerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (FindScrollableAncestor(e.OriginalSource as DependencyObject, e.Delta) is not null)
            return;
        ForwardMouseWheelToParent(e);
    }

    private void OnCodeViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is TextBox textBox && TrySmoothScrollCodeViewer(textBox, e.Delta))
        {
            e.Handled = true;
            return;
        }
        ForwardMouseWheelToParent(e);
    }

    private void ForwardMouseWheelToParent(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var ev = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this
        };
        // Raise on the immediate visual parent — the routed event then bubbles
        // up to whatever ScrollViewer wraps the message list.
        var parent = VisualTreeHelper.GetParent(this) as UIElement;
        parent?.RaiseEvent(ev);
    }

    private static bool TrySmoothScrollCodeViewer(TextBox textBox, int delta)
    {
        if (textBox.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
            return false;
        if (textBox.ExtentHeight <= textBox.ViewportHeight)
            return false;

        var scrollableHeight = Math.Max(0, textBox.ExtentHeight - textBox.ViewportHeight);
        var state = GetSmoothScrollState(textBox);
        var origin = state.Animating ? state.TargetOffset : textBox.VerticalOffset;
        var target = Math.Clamp(origin - (delta * 0.92), 0, scrollableHeight);
        if (Math.Abs(target - origin) < 0.25) return false;

        state.StartOffset = textBox.VerticalOffset;
        state.TargetOffset = target;
        state.AnimationStart = DateTime.UtcNow;

        if (!state.Animating)
        {
            state.Animating = true;
            state.Owner = textBox;
            state.FrameHandler ??= (_, _) => AnimateCodeViewerScrollFrame(state);
            CompositionTarget.Rendering += state.FrameHandler;
        }
        return true;
    }

    private static SmoothTextBoxScrollState GetSmoothScrollState(TextBox textBox)
    {
        if (textBox.Tag is SmoothTextBoxScrollState state) return state;
        state = new SmoothTextBoxScrollState();
        textBox.Tag = state;
        return state;
    }

    private static void AnimateCodeViewerScrollFrame(SmoothTextBoxScrollState state)
    {
        var textBox = state.Owner;
        if (textBox is null)
            return;

        var scrollableHeight = Math.Max(0, textBox.ExtentHeight - textBox.ViewportHeight);
        var elapsed = (DateTime.UtcNow - state.AnimationStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / 170, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        var offset = state.StartOffset + ((state.TargetOffset - state.StartOffset) * eased);

        textBox.ScrollToVerticalOffset(Math.Clamp(offset, 0, scrollableHeight));

        if (t < 1 && Math.Abs(textBox.VerticalOffset - state.TargetOffset) > 0.25) return;

        textBox.ScrollToVerticalOffset(Math.Clamp(state.TargetOffset, 0, scrollableHeight));
        if (state.FrameHandler is not null)
            CompositionTarget.Rendering -= state.FrameHandler;
        state.Animating = false;
    }

    private ScrollViewer? FindScrollableAncestor(DependencyObject? source, int delta)
    {
        var node = source;
        while (node is not null)
        {
            if (node is ScrollViewer sv && !ReferenceEquals(sv, _viewer) && sv.ScrollableHeight > 0)
            {
                var canScroll = delta < 0
                    ? sv.VerticalOffset < sv.ScrollableHeight
                    : sv.VerticalOffset > 0;
                if (canScroll) return sv;
            }
            node = GetTreeParent(node);
        }
        return null;
    }

    private ScrollViewer? FindOuterScrollViewer()
    {
        DependencyObject? node = this;
        while ((node = GetTreeParent(node)) is not null)
        {
            if (node is ScrollViewer scrollViewer)
                return scrollViewer;
        }

        return null;
    }

    private static bool IsEmbeddedInteractiveSurface(DependencyObject? source) =>
        FindTreeAncestor<ButtonBase>(source) is not null
        || FindTreeAncestor<TextBoxBase>(source) is not null
        || FindTreeAncestor<PasswordBox>(source) is not null
        || FindTreeAncestor<ComboBox>(source) is not null
        || FindTreeAncestor<ScrollBar>(source) is not null;

    private static Hyperlink? FindNavigableHyperlink(DependencyObject? source)
    {
        var hyperlink = FindTreeAncestor<Hyperlink>(source);
        return hyperlink?.NavigateUri is null ? null : hyperlink;
    }

    private static T? FindTreeAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var node = source;
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = GetTreeParent(node);
        }

        return null;
    }

    private static DependencyObject? GetTreeParent(DependencyObject? node)
    {
        if (node is null)
            return null;

        if (node is Visual or Visual3D)
            return VisualTreeHelper.GetParent(node);
        if (node is FrameworkContentElement fce)
            return fce.Parent;
        return null;
    }

    private void ApplyTypography()
    {
        _doc.Language = System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN");
        if (TryFindResource("Font.UI") is FontFamily uiFont) _doc.FontFamily = uiFont;
        if (TryFindResource("Brush.Text.Primary") is Brush fg) _doc.Foreground = fg;
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownPresenter mp) mp.OnSourceChanged((string?)e.NewValue ?? string.Empty);
    }

    private static void OnIsStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownPresenter mp && e.NewValue is false)
        {
            mp.Flush(useFinalPipeline: true);
        }
    }

    private static void OnSourcesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownPresenter mp)
        {
            mp.ResetAll();
            mp.OnSourceChanged(mp.Markdown);
        }
    }

    public void RefreshTheme()
    {
        _preserveMarkdownImagesDuringThemeRefresh = true;
        try
        {
            _lastRenderedSource = string.Empty;
            _lastUsedPipeline = null!;
            ResetAll(preserveMarkdownImages: true);
            Flush(useFinalPipeline: !IsStreaming);
        }
        finally
        {
            _preserveMarkdownImagesDuringThemeRefresh = false;
        }
    }

    private void OnSourceChanged(string newValue)
    {
        _pendingSource = newValue ?? string.Empty;

        if (string.IsNullOrEmpty(_lastRenderedSource) || !IsStreaming)
        {
            Flush();
            return;
        }

        var throttleMs = IsSimplePlainTextStreamContent(_pendingSource) ? 4 : Math.Max(16, ThrottleMs);
        _throttleTimer.Interval = TimeSpan.FromMilliseconds(throttleMs);
        if (!_throttleTimer.IsEnabled) _throttleTimer.Start();
    }

    private void Flush(bool useFinalPipeline = false)
    {
        _throttleTimer.Stop();
        // Strip PY_OUTPUT/MCP_OUTPUT marker comments up front, BEFORE any path
        // decision. The output frame of a Python/MCP call carries the closing
        // marker (and the result body) after the <DSanalysis> opener was
        // consumed in an earlier frame; on an incremental/restore render the
        // current src may hold "...END-->" with no custom-markup opener, so
        // ContainsCustomMarkup would route it to the plain markdown path and
        // the marker would leak as literal text (and the output body would
        // render outside the DSanalysis card). Stripping here makes every
        // downstream path marker-safe. Idempotent: Split() re-strips harmlessly,
        // and _lastRenderedSource is stored post-strip so the append-only
        // prefix check stays consistent frame to frame.
        var src = EnsureBlankLinesAroundStandaloneImages(
            MolaGptMarkupSplitter.NormalizeOutputSegmentMarkers(
                ProcessCitationRefs(_pendingSource ?? string.Empty)));
        var pipeline = (useFinalPipeline || !IsStreaming) ? s_finalPipeline : s_streamingPipeline;
        bool pipelineChanged = pipeline != _lastUsedPipeline;

        if (src == _lastRenderedSource && !pipelineChanged) return;

        ApplyTypography();

        if (string.IsNullOrEmpty(src))
        {
            ResetAll();
            _lastRenderedSource = src;
            _lastUsedPipeline = pipeline;
            return;
        }

        // Quick scan — does the source contain MolaGPT custom markers? If
        // not, use the fast AST-block-diff path. If yes, switch to the
        // mixed-units path (which handles markdown segments around the
        // tool blocks).
        bool hasMarkup = ContainsCustomMarkup(src);

        if (TryAppendOnlyPlainTextStream(src, useFinalPipeline))
        {
            _lastRenderedSource = src;
            _lastUsedPipeline = pipeline;
            _hasFirstPaintAnimated = true;
            return;
        }

        if (!hasMarkup && TryRenderStreamingOpenCodeBlock(src, pipeline, useFinalPipeline))
        {
            _lastRenderedSource = src;
            _lastUsedPipeline = pipeline;
            _hasFirstPaintAnimated = true;
            return;
        }

        // If the path is changing or the pipeline changed, reset caches so
        // the new path starts from scratch.
        if (pipelineChanged
            || _cachePath == CachePath.StreamingOpenCode
            || (hasMarkup && _cachePath == CachePath.FastAst)
            || (!hasMarkup && _cachePath == CachePath.MixedUnits))
        {
            ResetAll();
        }

        // Set the pipeline marker BEFORE Flush*. ApplyMolaMarkdownStyles and
        // TryAppendMathBlock peek at _lastUsedPipeline to decide whether to
        // run the expensive WpfMath path; without this assignment they'd
        // see the previous pipeline and never typeset the math on the
        // terminal-state Flush.
        _lastUsedPipeline = pipeline;

        if (hasMarkup)
            FlushMixed(src, pipeline);
        else
            FlushFast(src, pipeline);

        _lastRenderedSource = src;

        // Animation policy: only fade on (a) the first time a streaming
        // message paints, and (b) when streaming finishes. For static
        // historical loads (IsStreaming was false from the very first
        // OnSourceChanged) we skip the per-message fade entirely — running
        // 100+ 220ms storyboards in parallel was a major source of "卡顿"
        // when opening a long conversation. The user gets instant content
        // and the UserControl-level slide-in (MessageItemView.xaml) still
        // covers the perceived motion.
        bool wasFreshStream = !_hasFirstPaintAnimated && IsStreaming;
        if (wasFreshStream || useFinalPipeline)
        {
            AnimateFade();
            _hasFirstPaintAnimated = true;
        }
        else if (!_hasFirstPaintAnimated)
        {
            // Mark as "painted" so a subsequent stream start (rare — same
            // VM going from idle to streaming) gets one fade.
            _hasFirstPaintAnimated = true;
        }
    }

    private static bool ContainsCustomMarkup(string src)
    {
        // Quick string check before regex split — both markers are
        // distinctive enough that a contains-test is enough to decide path.
        return src.IndexOf("<steel-step", StringComparison.OrdinalIgnoreCase) >= 0
            || src.IndexOf("<DSanalysis", StringComparison.OrdinalIgnoreCase) >= 0
            || src.IndexOf("ai-image-pending-skeleton", StringComparison.OrdinalIgnoreCase) >= 0
            || src.IndexOf("ai-image-error-card", StringComparison.OrdinalIgnoreCase) >= 0
            || (src.IndexOf("<blockquote", StringComparison.OrdinalIgnoreCase) >= 0
                && src.IndexOf("tool-status", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void ResetAll(bool preserveMarkdownImages = false)
    {
        StopSelectionAutoScroll();
        ClearPendingClickHyperlink();
        preserveMarkdownImages |= _preserveMarkdownImagesDuringThemeRefresh;

        _doc.Blocks.Clear();
        // The rendered document is now empty, so the "already rendered" cache
        // must be invalidated too — otherwise the next Flush() can see an
        // unchanged source (e.g. OnSourcesChanged re-renders the same plain-text
        // body that carries no <ref> to re-link, or a recycled container gets an
        // identical Markdown) and early-return without refilling the blocks we
        // just cleared, leaving the message body permanently blank.
        _lastRenderedSource = string.Empty;
        _fastBlockSources.Clear();
        _mixedUnits.Clear();
        _cachePath = CachePath.Empty;
        _plainTextAppendMode = false;
        _streamingOpenCodePrefix = string.Empty;
        _streamingOpenCodeLanguage = string.Empty;
        _streamingOpenCodeTextBox = null;

        if (!preserveMarkdownImages)
        {
            _markdownImageCache.Clear();
        }
    }

    private bool TryAppendOnlyPlainTextStream(string src, bool useFinalPipeline)
    {
        if (!IsStreaming || useFinalPipeline) return false;
        if (!IsSimplePlainTextStreamContent(src)) return false;

        if (!_plainTextAppendMode)
        {
            ResetAll();
            _cachePath = CachePath.FastAst;
            _plainTextAppendMode = true;
            AppendPlainTextChunk(src);
            _fastBlockSources.Clear();
            _fastBlockSources.Add(src);
            return true;
        }

        if (!src.StartsWith(_lastRenderedSource, StringComparison.Ordinal)) return false;
        if (src.Length <= _lastRenderedSource.Length) return true;

        AppendPlainTextChunk(src[_lastRenderedSource.Length..]);
        _fastBlockSources.Clear();
        _fastBlockSources.Add(src);
        return true;
    }

    private static bool IsSimplePlainTextStreamContent(string text)
    {
        if (text.Length == 0) return true;
        if (Regex.IsMatch(text, @"[<][^>]*>")) return false;
        if (Regex.IsMatch(text, @"```|`[^`]*`")) return false;
        if (Regex.IsMatch(text, @"\[[^\]]+\]\([^)]+\)|!\[[^\]]*\]\([^)]+\)")) return false;
        if (Regex.IsMatch(text, @"^\s{0,3}#{1,6}\s", RegexOptions.Multiline)) return false;
        if (Regex.IsMatch(text, @"^\s{0,3}(?:[-*+]\s|\d+\.\s)", RegexOptions.Multiline)) return false;
        if (Regex.IsMatch(text, @"^\s{0,3}>\s", RegexOptions.Multiline)) return false;
        if (Regex.IsMatch(text, @"\$\$|\\\(|\\\[|\\begin\{")) return false;
        if (Regex.IsMatch(text, @"(^|[\s(])(?:\*\*|__|~~)")) return false;
        if (text.Contains('|', StringComparison.Ordinal)) return false;
        return true;
    }

    private void AppendPlainTextChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        Paragraph paragraph;
        if (_doc.Blocks.Count == 1 && _doc.Blocks.FirstBlock is Paragraph existing)
        {
            paragraph = existing;
        }
        else
        {
            _doc.Blocks.Clear();
            paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 8),
                FontFamily = _doc.FontFamily,
                Foreground = _doc.Foreground,
                LineHeight = _doc.LineHeight
            };
            _doc.Blocks.Add(paragraph);
        }

        var start = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            var ch = chunk[i];
            if (ch is not '\r' and not '\n') continue;

            if (i > start)
                paragraph.Inlines.Add(new Run(chunk[start..i]));

            if (ch == '\r' && i + 1 < chunk.Length && chunk[i + 1] == '\n')
                i++;

            paragraph.Inlines.Add(new LineBreak());
            start = i + 1;
        }

        if (start < chunk.Length)
            paragraph.Inlines.Add(new Run(chunk[start..]));
    }

    private bool TryRenderStreamingOpenCodeBlock(string src, MarkdownPipeline pipeline, bool useFinalPipeline)
    {
        if (!IsStreaming || useFinalPipeline) return false;
        if (!TryFindOpenFence(src, out var fence)) return false;

        var prefix = src[..fence.Start];
        var language = NormalizeCodeLanguage(fence.Language);
        var code = RemoveFenceIndent(src[fence.CodeStart..], fence.Indent).TrimEnd('\r', '\n');

        if (_cachePath == CachePath.StreamingOpenCode
            && _streamingOpenCodeTextBox is not null
            && _streamingOpenCodePrefix == prefix
            && _streamingOpenCodeLanguage == language)
        {
            if (_streamingOpenCodeTextBox.Text != code)
                _streamingOpenCodeTextBox.Text = code;
            return true;
        }

        ResetAll();
        _cachePath = CachePath.StreamingOpenCode;
        _streamingOpenCodePrefix = prefix;
        _streamingOpenCodeLanguage = language;

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            MarkdownDocument prefixAst;
            try
            {
                prefixAst = Markdig.Markdown.Parse(prefix, pipeline);
                foreach (var slice in SliceAstBlockSources(prefixAst, prefix))
                    AppendMarkdownSlice(slice, pipeline, anchorAfter: null);
            }
            catch
            {
                _doc.Blocks.Add(CreatePlainParagraph(prefix));
            }
        }

        var codeBlock = BuildCodeBlockView(code, language);
        _streamingOpenCodeTextBox = codeBlock.Editor;
        _doc.Blocks.Add(new BlockUIContainer(codeBlock.Root)
        {
            Margin = new Thickness(0, 10, 0, 12),
            Padding = new Thickness(0)
        });
        return true;
    }

    private readonly record struct OpenFenceInfo(int Start, int CodeStart, string Language, string Indent);

    private static bool TryFindOpenFence(string source, out OpenFenceInfo info)
    {
        info = default;
        if (string.IsNullOrEmpty(source)) return false;

        var lineStart = 0;
        var openStart = -1;
        var openCodeStart = -1;
        var openIndent = string.Empty;
        var openFenceChar = '\0';
        var openFenceLength = 0;
        var openLanguage = string.Empty;

        while (lineStart <= source.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < source.Length && source[lineEnd] is not '\r' and not '\n')
                lineEnd++;

            var line = source[lineStart..lineEnd];
            var trimmedEnd = line.TrimEnd(' ', '\t');

            if (openStart < 0)
            {
                if (TryParseOpeningFenceLine(trimmedEnd, out var indent, out var fenceChar, out var fenceLength, out var language))
                {
                    openStart = lineStart;
                    openIndent = indent;
                    openFenceChar = fenceChar;
                    openFenceLength = fenceLength;
                    openLanguage = language;
                    openCodeStart = lineEnd;
                    if (lineEnd < source.Length && source[lineEnd] == '\r') openCodeStart++;
                    if (openCodeStart < source.Length && source[openCodeStart] == '\n') openCodeStart++;
                }
            }
            else if (IsClosingFenceLine(trimmedEnd, openIndent, openFenceChar, openFenceLength))
            {
                openStart = -1;
                openCodeStart = -1;
                openIndent = string.Empty;
                openFenceChar = '\0';
                openFenceLength = 0;
                openLanguage = string.Empty;
            }

            if (lineEnd >= source.Length) break;
            lineStart = lineEnd + 1;
            if (source[lineEnd] == '\r' && lineStart < source.Length && source[lineStart] == '\n')
                lineStart++;
        }

        if (openStart < 0 || openCodeStart < 0) return false;
        info = new OpenFenceInfo(openStart, openCodeStart, openLanguage, openIndent);
        return true;
    }

    private static bool TryParseOpeningFenceLine(
        string line,
        out string indent,
        out char fenceChar,
        out int fenceLength,
        out string language)
    {
        indent = string.Empty;
        fenceChar = '\0';
        fenceLength = 0;
        language = string.Empty;

        var i = 0;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        if (i >= line.Length || line[i] is not ('`' or '~')) return false;

        fenceChar = line[i];
        var fenceStart = i;
        while (i < line.Length && line[i] == fenceChar) i++;
        fenceLength = i - fenceStart;
        if (fenceLength < 3) return false;

        indent = line[..fenceStart];
        language = line[i..].Trim();
        if (fenceChar == '`' && language.Contains('`', StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsClosingFenceLine(string line, string indent, char fenceChar, int fenceLength)
    {
        if (!line.StartsWith(indent, StringComparison.Ordinal)) return false;
        var i = indent.Length;
        var count = 0;
        while (i < line.Length && line[i] == fenceChar)
        {
            count++;
            i++;
        }
        if (count < fenceLength) return false;
        while (i < line.Length)
        {
            if (line[i] is not ' ' and not '\t') return false;
            i++;
        }
        return true;
    }

    private static bool OpenHyperlink(Uri uri)
    {
        var target = string.IsNullOrWhiteSpace(uri.OriginalString)
            ? uri.ToString()
            : uri.OriginalString;
        return TryOpenExternal(target);
    }

    private static void OnHyperlinkRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri is null) return;
        if (sender is Hyperlink hyperlink && TryOpenHyperlinkOnce(hyperlink, e.Uri))
            e.Handled = true;
    }

    private static void WireHyperlink(Hyperlink hyperlink)
    {
        if (hyperlink.NavigateUri is null) return;
        hyperlink.Cursor = Cursors.Hand;
        hyperlink.Command = null;
        hyperlink.RequestNavigate += OnHyperlinkRequestNavigate;
        hyperlink.Click += OnHyperlinkClick;
        hyperlink.PreviewMouseLeftButtonDown += OnHyperlinkPreviewMouseLeftButtonDown;
        hyperlink.PreviewMouseLeftButtonUp += OnHyperlinkPreviewMouseLeftButtonUp;
    }

    private static void OnHyperlinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { NavigateUri: { } uri } hyperlink
            && TryOpenHyperlinkOnce(hyperlink, uri))
        {
            e.Handled = true;
        }
    }

    private static void OnHyperlinkPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Hyperlink hyperlink || hyperlink.NavigateUri is null) return;
        hyperlink.SetValue(HyperlinkMouseDownPointProperty, e.GetPosition(hyperlink));
        if (TryOpenHyperlinkOnce(hyperlink, hyperlink.NavigateUri))
            e.Handled = true;
    }

    private static void OnHyperlinkPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Hyperlink hyperlink || hyperlink.NavigateUri is null) return;

        var start = (Point)hyperlink.GetValue(HyperlinkMouseDownPointProperty);
        hyperlink.SetValue(HyperlinkMouseDownPointProperty, new Point(double.NaN, double.NaN));
        if (!IsClickGesture(start, e.GetPosition(hyperlink)))
            return;

        if (TryOpenHyperlinkOnce(hyperlink, hyperlink.NavigateUri))
            e.Handled = true;
    }

    private static bool TryOpenHyperlinkOnce(Hyperlink hyperlink, Uri uri)
    {
        var now = Environment.TickCount64;
        var last = (long)hyperlink.GetValue(HyperlinkLastOpenTickProperty);
        if (last > 0 && now >= last && now - last < 750)
            return true;

        if (!OpenHyperlink(uri))
            return false;

        hyperlink.SetValue(HyperlinkLastOpenTickProperty, now);
        return true;
    }

    private static void WireHyperlinks(InlineCollection inlines)
    {
        foreach (var inline in inlines.Cast<Inline>().ToList())
        {
            if (inline is Hyperlink hyperlink)
            {
                WireHyperlink(hyperlink);
            }
            else if (inline is Span span)
            {
                WireHyperlinks(span.Inlines);
            }
        }
    }

    // ---------- Fast AST-block-diff path (pure markdown) ----------

    private void FlushFast(string src, MarkdownPipeline pipeline)
    {
        _cachePath = CachePath.FastAst;

        MarkdownDocument ast;
        try
        {
            ast = Markdig.Markdown.Parse(src, pipeline);
        }
        catch
        {
            FallbackPlain(src);
            return;
        }

        var newSources = SliceAstBlockSources(ast, src);

        int stableCount = 0;
        int max = Math.Min(_fastBlockSources.Count, newSources.Count);
        while (stableCount < max
               && _fastBlockSources[stableCount] == newSources[stableCount])
        {
            stableCount++;
        }

        // Remove blocks past stableCount.
        while (_doc.Blocks.Count > stableCount)
        {
            var last = _doc.Blocks.LastBlock;
            if (last == null) break;
            _doc.Blocks.Remove(last);
        }

        // Render and append the new tail blocks.
        for (int i = stableCount; i < newSources.Count; i++)
        {
            var slice = newSources[i];
            AppendMarkdownSlice(slice, pipeline, anchorAfter: null);
        }

        _fastBlockSources.Clear();
        _fastBlockSources.AddRange(newSources);
    }

    private static List<string> SliceAstBlockSources(MarkdownDocument ast, string src)
    {
        var slices = new List<string>(ast.Count);
        foreach (var block in ast)
        {
            var span = block.Span;
            if (span.IsEmpty || span.Start < 0)
            {
                slices.Add(string.Empty);
                continue;
            }
            int start = Math.Max(0, span.Start);
            int end = Math.Min(src.Length, span.End + 1);   // Markdig.End is inclusive.
            int len = Math.Max(0, end - start);
            slices.Add(len > 0 ? src.Substring(start, len) : string.Empty);
        }
        return slices;
    }

    private void FallbackPlain(string src)
    {
        _doc.Blocks.Clear();
        _doc.Blocks.Add(CreatePlainParagraph(src));
        _fastBlockSources.Clear();
        _fastBlockSources.Add(src);
    }

    private Paragraph CreatePlainParagraph(string text)
    {
        var paragraph = new Paragraph(new Run(text));
        ApplyParagraphTypography(paragraph);
        paragraph.Margin = new Thickness(0, 0, 0, 8);
        return paragraph;
    }

    // ---------- Mixed unit-diff path (markdown + custom markers) ----------

    private void FlushMixed(string src, MarkdownPipeline pipeline)
    {
        _cachePath = CachePath.MixedUnits;

        var newUnits = MolaGptMarkupSplitter.Split(src);

        int stableCount = 0;
        while (stableCount < _mixedUnits.Count && stableCount < newUnits.Count)
        {
            if (IsStableUnit(_mixedUnits[stableCount], newUnits[stableCount]))
            {
                stableCount++;
                continue;
            }

            if (_mixedUnits[stableCount].Kind == MarkupUnitKind.Markdown
                && newUnits[stableCount].Kind == MarkupUnitKind.Markdown
                && DiffMarkdownUnitInPlace(stableCount, newUnits[stableCount].Source, pipeline))
            {
                stableCount++;
                continue;
            }

            if (TryUpdateCustomUnitInPlace(stableCount, newUnits[stableCount], HasFollowingContent(newUnits, stableCount)))
            {
                stableCount++;
                continue;
            }

            break;
        }

        // Drop blocks belonging to invalidated units.
        int blocksToRemove = 0;
        for (int i = stableCount; i < _mixedUnits.Count; i++)
            blocksToRemove += _mixedUnits[i].BlockCount;
        while (blocksToRemove > 0 && _doc.Blocks.Count > 0)
        {
            var last = _doc.Blocks.LastBlock;
            if (last == null) break;
            _doc.Blocks.Remove(last);
            blocksToRemove--;
        }
        _mixedUnits.RemoveRange(stableCount, _mixedUnits.Count - stableCount);

        // Render and append new tail units.
        for (int i = stableCount; i < newUnits.Count; i++)
        {
            var u = newUnits[i];
            var (added, astSources, element) = AppendUnitForMixed(u, pipeline, HasFollowingContent(newUnits, i));
            _mixedUnits.Add(new CachedUnit
            {
                Kind = u.Kind,
                Source = u.Source,
                Tag = u.Tag,
                Inner = u.Inner,
                ToolVariant = u.ToolVariant,
                AnalysisPhase = u.AnalysisPhase,
                BlockCount = added,
                Element = element,
                AstBlockSources = astSources
            });
        }
    }

    /// <summary>
    /// True when some unit after <paramref name="index"/> carries visible,
    /// non-whitespace content (ignoring tool-status blockquotes). Mirrors the
    /// web client's auto-collapse guard: a completed Python/MCP analysis card
    /// only collapses once the model's following answer has started — until
    /// then the card stays expanded so its execution output is visible.
    /// </summary>
    private static bool HasFollowingContent(List<MolaGptMarkupSplitter.MarkupUnit> units, int index)
    {
        for (int i = index + 1; i < units.Count; i++)
        {
            var u = units[i];
            if (u.Kind == MarkupUnitKind.ToolStatus) continue;
            if (u.Kind == MarkupUnitKind.Markdown)
            {
                if (!string.IsNullOrWhiteSpace(u.Source)) return true;
                continue;
            }
            // Any other rendered unit (another DSanalysis, image, steel step…)
            // counts as following content.
            return true;
        }
        return false;
    }

    private static bool IsStableUnit(CachedUnit oldU, MolaGptMarkupSplitter.MarkupUnit newU)
    {        return oldU.Kind == newU.Kind
            && oldU.Tag == newU.Tag
            && oldU.ToolVariant == newU.ToolVariant
            && oldU.AnalysisPhase == newU.AnalysisPhase
            && oldU.Source == newU.Source;
    }

    private bool TryUpdateCustomUnitInPlace(int cachedIdx, MolaGptMarkupSplitter.MarkupUnit unit, bool hasFollowingContent)
    {
        var cached = _mixedUnits[cachedIdx];
        if (cached.Kind != unit.Kind || cached.Tag != unit.Tag || cached.Element is null) return false;

        bool updated = unit.Kind switch
        {
            MarkupUnitKind.ToolStatus => MolaGptMarkupBlocks.TryUpdateToolStatus(cached.Element, unit, this),
            MarkupUnitKind.DsAnalysis => MolaGptMarkupBlocks.TryUpdateDsAnalysis(cached.Element, unit, this, hasFollowingContent),
            MarkupUnitKind.SteelStep => MolaGptMarkupBlocks.TryUpdateSteelStep(cached.Element, unit, this),
            MarkupUnitKind.ImagePendingSkeleton => MolaGptMarkupBlocks.TryUpdateImagePendingSkeleton(cached.Element, unit, this),
            MarkupUnitKind.ImageErrorCard => MolaGptMarkupBlocks.TryUpdateImageErrorCard(cached.Element, unit, this),
            _ => false
        };
        if (!updated) return false;

        cached.Source = unit.Source;
        cached.Inner = unit.Inner;
        cached.ToolVariant = unit.ToolVariant;
        cached.AnalysisPhase = unit.AnalysisPhase;
        return true;
    }

    /// <summary>
    /// In-place AST-block diff for the streaming-tail Markdown unit at
    /// <paramref name="cachedIdx"/>. Reuses cached AstBlockSources to drop
    /// only the changed trailing AST blocks. Position calculus:
    /// <c>start = sum of BlockCount for units [0..cachedIdx)</c>.
    /// </summary>
    private bool DiffMarkdownUnitInPlace(int cachedIdx, string newSource, MarkdownPipeline pipeline)
    {
        var cached = _mixedUnits[cachedIdx];
        var oldAstSources = cached.AstBlockSources ?? new List<string>();

        MarkdownDocument ast;
        try
        {
            ast = Markdig.Markdown.Parse(newSource, pipeline);
        }
        catch
        {
            // Defensive: rebuild the unit from scratch via the standard path.
            // Leave stableCount unchanged in the outer flow so the unit's old
            // blocks are removed and rebuilt by AppendUnitForMixed.
            return false;
        }

        var newAstSources = SliceAstBlockSources(ast, newSource);

        int stableAst = 0;
        int max = Math.Min(oldAstSources.Count, newAstSources.Count);
        while (stableAst < max && oldAstSources[stableAst] == newAstSources[stableAst])
        {
            stableAst++;
        }

        // Position in _doc.Blocks where this unit's blocks start.
        int startIdx = 0;
        for (int i = 0; i < cachedIdx; i++) startIdx += _mixedUnits[i].BlockCount;

        // Remove the trailing AST blocks of THIS unit (positions:
        // [startIdx + stableAst, startIdx + cached.BlockCount)).
        int toRemove = cached.BlockCount - stableAst;
        for (int k = 0; k < toRemove; k++)
        {
            int removeIdx = startIdx + stableAst;        // always remove at the same index, list shrinks
            if (removeIdx >= 0 && removeIdx < _doc.Blocks.Count)
            {
                var blk = _doc.Blocks.ElementAt(removeIdx);
                _doc.Blocks.Remove(blk);
            }
        }

        // Insert new tail AST blocks at startIdx + stableAst.
        // To insert at a specific index in BlockCollection we use
        // InsertAfter / InsertBefore relative to a sibling. If we're
        // inserting at the very end-of-unit (which equals end-of-doc here
        // because everything past this unit was removed by the outer flow),
        // we can just Add. But if the next unit's blocks already exist, we
        // need InsertBefore the next unit's first block.
        WpfBlock? anchorAfter = null;
        if (startIdx + stableAst < _doc.Blocks.Count)
        {
            anchorAfter = _doc.Blocks.ElementAt(startIdx + stableAst);
        }

        for (int i = stableAst; i < newAstSources.Count; i++)
        {
            var slice = newAstSources[i];
            AppendMarkdownSlice(slice, pipeline, anchorAfter);
        }

        // Update cache.
        cached.Source = newSource;
        cached.AstBlockSources = newAstSources;
        cached.BlockCount = ComputeBlockCountAt(startIdx, anchorAfter);
        return true;
    }

    private int ComputeBlockCountAt(int startIdx, WpfBlock? anchorAfter)
    {
        if (anchorAfter is null) return _doc.Blocks.Count - startIdx;
        // Count blocks strictly before anchorAfter.
        int idx = 0;
        foreach (var b in _doc.Blocks)
        {
            if (ReferenceEquals(b, anchorAfter)) return Math.Max(0, idx - startIdx);
            idx++;
        }
        return _doc.Blocks.Count - startIdx;
    }

    /// <summary>
    /// Renders one unit and appends its blocks to the live FlowDocument,
    /// returning (blockCount, astBlockSources). astBlockSources is null
    /// for non-markdown units.
    /// </summary>
    private (int Count, List<string>? AstSources, UIElement? Element) AppendUnitForMixed(
        MolaGptMarkupSplitter.MarkupUnit unit,
        MarkdownPipeline pipeline,
        bool hasFollowingContent = false)
    {
        switch (unit.Kind)
        {
            case MarkupUnitKind.ToolStatus:
            {
                var ui = MolaGptMarkupBlocks.BuildToolStatus(unit, this);
                _doc.Blocks.Add(new BlockUIContainer(ui)
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0)
                });
                return (1, null, ui);
            }
            case MarkupUnitKind.DsAnalysis:
            {
                if (!MolaGptMarkupBlocks.ShouldRenderDsAnalysis(unit))
                    return (0, null, null);

                var ui = MolaGptMarkupBlocks.BuildDsAnalysis(unit, this, hasFollowingContent);
                _doc.Blocks.Add(new BlockUIContainer(ui)
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0)
                });
                return (1, null, ui);
            }
            case MarkupUnitKind.SteelStep:
            {
                var ui = MolaGptMarkupBlocks.BuildSteelStep(unit, this);
                _doc.Blocks.Add(new BlockUIContainer(ui)
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0)
                });
                return (1, null, ui);
            }
            case MarkupUnitKind.ImagePendingSkeleton:
            {
                var ui = MolaGptMarkupBlocks.BuildImagePendingSkeleton(unit, this);
                _doc.Blocks.Add(new BlockUIContainer(ui)
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0)
                });
                return (1, null, ui);
            }
            case MarkupUnitKind.ImageErrorCard:
            {
                var ui = MolaGptMarkupBlocks.BuildImageErrorCard(unit, this);
                _doc.Blocks.Add(new BlockUIContainer(ui)
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0)
                });
                return (1, null, ui);
            }
            case MarkupUnitKind.Markdown:
            default:
            {
                MarkdownDocument ast;
                try
                {
                    ast = Markdig.Markdown.Parse(unit.Source, pipeline);
                }
                catch
                {
                    _doc.Blocks.Add(CreatePlainParagraph(unit.Source));
                    return (1, new List<string> { unit.Source }, null);
                }

                var astSources = SliceAstBlockSources(ast, unit.Source);
                int blocksAdded = 0;
                foreach (var slice in astSources)
                {
                    blocksAdded += AppendMarkdownSlice(slice, pipeline, anchorAfter: null);
                }
                return (blocksAdded, astSources, null);
            }
        }
    }

    private int AppendMarkdownSlice(string slice, MarkdownPipeline pipeline, WpfBlock? anchorAfter)
    {
        if (TryAppendSpecialBlock(slice, anchorAfter))
            return 1;

        var matches = EmbeddedFencedCodeRegex().Matches(slice);
        if (matches.Count == 0)
        {
            if (IsStreaming && EmbeddedStreamingFencedCodeRegex().Match(slice) is { Success: true } streamingMatch)
                return AppendEmbeddedCodeMatch(slice, pipeline, anchorAfter, streamingMatch);

            return AppendRenderedMarkdown(slice, pipeline, anchorAfter);
        }

        var count = 0;
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index > cursor)
                count += AppendRenderedMarkdown(slice[cursor..match.Index], pipeline, anchorAfter);

            count += AppendCodeMatch(match, pipeline, anchorAfter);

            cursor = match.Index + match.Length;
        }

        if (cursor < slice.Length)
            count += AppendRenderedMarkdown(slice[cursor..], pipeline, anchorAfter);

        return count;
    }

    private int AppendEmbeddedCodeMatch(string slice, MarkdownPipeline pipeline, WpfBlock? anchorAfter, Match match)
    {
        var count = 0;
        if (match.Index > 0)
            count += AppendRenderedMarkdown(slice[..match.Index], pipeline, anchorAfter);

        return count + AppendCodeMatch(match, pipeline, anchorAfter);
    }

    private int AppendCodeMatch(Match match, MarkdownPipeline pipeline, WpfBlock? anchorAfter)
    {
        var lang = NormalizeCodeLanguage(match.Groups["lang"].Value);
        var code = RemoveFenceIndent(match.Groups["code"].Value, match.Groups["indent"].Value)
            .TrimEnd('\r', '\n');
        if (IsMathFenceLanguage(lang))
        {
            var mathSlice = match.Value;
            if (!TryAppendMathBlock(mathSlice, anchorAfter))
                return AppendRenderedMarkdown(mathSlice, pipeline, anchorAfter);
            return 1;
        }

        var block = new BlockUIContainer(BuildCodeBlock(code, lang))
        {
            Margin = new Thickness(0, 10, 0, 12),
            Padding = new Thickness(0)
        };

        if (anchorAfter is not null) _doc.Blocks.InsertBefore(anchorAfter, block);
        else _doc.Blocks.Add(block);
        return 1;
    }

    private int AppendRenderedMarkdown(string source, MarkdownPipeline pipeline, WpfBlock? anchorAfter)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 0;

        try
        {
            var protectedSource = ProtectInlineMathForMarkdig(source, out var inlineMath);
            var subDoc = Markdig.Wpf.Markdown.ToFlowDocument(protectedSource, pipeline);
            var blocks = subDoc.Blocks.ToList();
            foreach (var b in blocks)
            {
                subDoc.Blocks.Remove(b);
                ApplyMolaMarkdownStyles(b, inlineMath);
                if (anchorAfter is not null) _doc.Blocks.InsertBefore(anchorAfter, b);
                else _doc.Blocks.Add(b);
            }
            return blocks.Count;
        }
        catch
        {
            var fallback = CreatePlainParagraph(source);
            if (anchorAfter is not null) _doc.Blocks.InsertBefore(anchorAfter, fallback);
            else _doc.Blocks.Add(fallback);
            return 1;
        }
    }

    private static string RemoveFenceIndent(string code, string indent)
    {
        if (string.IsNullOrEmpty(indent) || string.IsNullOrEmpty(code))
            return code;

        var normalized = code.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(indent, StringComparison.Ordinal))
                lines[i] = lines[i][indent.Length..];
        }
        return string.Join(Environment.NewLine, lines);
    }

    private bool TryAppendSpecialBlock(string slice, WpfBlock? anchorAfter)
    {
        return TryAppendMarkdownImageBlock(slice, anchorAfter)
            || TryAppendMathBlock(slice, anchorAfter)
            || TryAppendCodeBlock(slice, anchorAfter)
            || TryAppendMarkdownQuote(slice, anchorAfter);
    }

    private bool TryAppendMarkdownImageBlock(string slice, WpfBlock? anchorAfter)
    {
        if (slice.IndexOf("![", StringComparison.Ordinal) < 0)
            return false;

        if (!TryExtractStandaloneMarkdownImage(slice, out var imageUrl, out var linkUrl))
            return false;

        var block = new BlockUIContainer(BuildMarkdownImageCard(imageUrl, linkUrl))
        {
            Margin = new Thickness(0, 8, 0, 12),
            Padding = new Thickness(0)
        };

        if (anchorAfter is not null) _doc.Blocks.InsertBefore(anchorAfter, block);
        else _doc.Blocks.Add(block);
        return true;
    }

    private static bool TryExtractStandaloneMarkdownImage(
        string slice,
        out string imageUrl,
        out string? linkUrl)
    {
        imageUrl = string.Empty;
        linkUrl = null;

        MarkdownDocument ast;
        try
        {
            ast = Markdig.Markdown.Parse(slice);
        }
        catch
        {
            return false;
        }

        if (ast.Count != 1 || ast[0] is not ParagraphBlock { Inline: { } inline })
            return false;

        var meaningful = GetMeaningfulInlines(inline).ToList();
        if (meaningful.Count != 1)
            return false;

        return TryExtractImageInline(meaningful[0], outerLinkUrl: null, out imageUrl, out linkUrl);
    }

    private static IEnumerable<MdInline> GetMeaningfulInlines(MdContainerInline container)
    {
        foreach (var inline in container)
        {
            if (inline is MdLineBreakInline)
                continue;

            if (inline is MdLiteralInline literal && string.IsNullOrWhiteSpace(literal.Content.ToString()))
                continue;

            yield return inline;
        }
    }

    private static bool TryExtractImageInline(
        MdInline inline,
        string? outerLinkUrl,
        out string imageUrl,
        out string? linkUrl)
    {
        imageUrl = string.Empty;
        linkUrl = null;

        if (inline is not MdLinkInline link)
            return false;

        if (link.IsImage)
        {
            if (string.IsNullOrWhiteSpace(link.Url))
                return false;

            imageUrl = link.Url!;
            linkUrl = outerLinkUrl;
            return true;
        }

        var children = GetMeaningfulInlines(link).ToList();
        return children.Count == 1
            && TryExtractImageInline(children[0], link.Url, out imageUrl, out linkUrl);
    }

    private UIElement BuildMarkdownImageCard(string imageUrl, string? linkUrl)
    {
        var isAiGenerated = IsAiGeneratedImageUrl(imageUrl);
        var (width, height) = CalculateMarkdownImageCardSize(isAiGenerated);
        var state = new MarkdownImageCardState(isAiGenerated);
        var root = new Border
        {
            Width = width,
            Height = height,
            MaxWidth = width,
            MaxHeight = height,
            Background = ResolveBrush("Brush.Bg.Tertiary", new SolidColorBrush(Color.FromRgb(241, 243, 245))),
            BorderBrush = ResolveBrush("Brush.Border", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = state
        };
        root.Loaded += OnMarkdownImageCardLoaded;

        var grid = new Grid();
        root.Child = grid;

        var overlay = BuildAiImageSkeletonOverlay();
        var bitmap = GetOrCreateMarkdownBitmap(imageUrl, width);
        if (bitmap is not null)
        {
            var image = new Image
            {
                Source = bitmap,
                Width = width,
                Height = height,
                MaxWidth = width,
                MaxHeight = height,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                Tag = state
            };
            state.Image = image;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            image.Loaded += (_, _) => FadeOutAiImageOverlay(overlay);
            if (!bitmap.IsFrozen)
            {
                bitmap.DownloadCompleted += (_, _) => Dispatcher.BeginInvoke(
                    new Action(() => FadeOutAiImageOverlay(overlay)),
                    DispatcherPriority.Loaded);
                bitmap.DownloadFailed += (_, _) => Dispatcher.BeginInvoke(
                    new Action(() => FadeOutAiImageOverlay(overlay)),
                    DispatcherPriority.Loaded);
            }

            grid.Children.Add(image);
        }
        grid.Children.Add(overlay);

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            root.Cursor = Cursors.Hand;
            root.ToolTip = imageUrl;
            WireMarkdownImagePreview(root, imageUrl);
        }

        return root;
    }

    private (double Width, double Height) CalculateMarkdownImageCardSize(bool isAiGenerated)
    {
        var available = ActualWidth;
        if (double.IsNaN(available) || available <= 0)
            available = _viewer.ActualWidth;
        if (double.IsNaN(available) || available <= 0)
            available = MarkdownImageCardMaxWidth;

        if (isAiGenerated)
        {
            var size = Math.Max(MarkdownImageCardMinWidth, Math.Min(AiImageCardMaxSize, available - 8));
            return (size, size);
        }

        var width = Math.Max(MarkdownImageCardMinWidth, Math.Min(MarkdownImageCardMaxWidth, available - 8));
        return (width, width / MarkdownImageCardAspectRatio);
    }

    private BitmapImage? GetOrCreateMarkdownBitmap(string imageUrl, double decodeWidth)
    {
        if (_markdownImageCache.TryGetValue(imageUrl, out var cached))
            return cached;

        var bitmap = CreateBitmapImage(imageUrl, decodeWidth);
        if (bitmap is not null)
            _markdownImageCache[imageUrl] = bitmap;
        return bitmap;
    }

    private static BitmapImage? CreateBitmapImage(string imageUrl, double decodeWidth)
    {
        var decodePixelWidth = Math.Max(1, (int)Math.Ceiling(decodeWidth * 1.5));
        if (TryResolveLocalImagePath(imageUrl, out var localPath))
            return LoadLocalBitmap(localPath, decodePixelWidth);

        try
        {
            if (!Uri.TryCreate(imageUrl, UriKind.RelativeOrAbsolute, out var uri))
                return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnDemand;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveLocalImagePath(string imageUrl, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(imageUrl))
            return false;

        var raw = imageUrl.Trim().Trim('"', '\'');
        if (raw.Length == 0)
            return false;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile)
            {
                path = absoluteUri.LocalPath;
                return File.Exists(path);
            }
        }

        var unescaped = SafeUnescape(raw).Trim().Trim('"', '\'');
        if (LooksLikeWindowsPath(unescaped))
        {
            path = unescaped.Replace('/', Path.DirectorySeparatorChar);
            return File.Exists(path);
        }

        if (File.Exists(raw))
        {
            path = Path.GetFullPath(raw);
            return true;
        }

        if (!string.Equals(raw, unescaped, StringComparison.Ordinal) && File.Exists(unescaped))
        {
            path = Path.GetFullPath(unescaped);
            return true;
        }

        return false;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static bool LooksLikeWindowsPath(string value)
    {
        if (value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/'))
        {
            return true;
        }

        return value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal);
    }

    private static BitmapImage? LoadLocalBitmap(string path, int decodePixelWidth)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            file.CopyTo(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = memory;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            if (bitmap.CanFreeze)
                bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private bool TryAppendCodeBlock(string slice, WpfBlock? anchorAfter)
    {
        var match = FencedCodeRegex().Match(slice);
        if (!match.Success)
        {
            if (!IsStreaming) return false;
            match = StreamingFencedCodeRegex().Match(slice);
            if (!match.Success) return false;
        }

        var lang = NormalizeCodeLanguage(match.Groups["lang"].Value);
        if (IsMathFenceLanguage(lang)) return false;
        var code = match.Groups["code"].Value.TrimEnd('\r', '\n');

        var block = new BlockUIContainer(BuildCodeBlock(code, lang))
        {
            Margin = new Thickness(0, 10, 0, 12),
            Padding = new Thickness(0)
        };

        if (anchorAfter is not null) _doc.Blocks.InsertBefore(anchorAfter, block);
        else _doc.Blocks.Add(block);
        return true;
    }

    private bool TryAppendMathBlock(string slice, WpfBlock? anchorAfter)
    {
        var match = MathBlockRegex().Match(slice);
        if (!match.Success) return false;

        var formula = match.Groups["math"].Success
            ? match.Groups["math"].Value
            : match.Groups["fenced"].Value;
        formula = formula.Trim();
        if (formula.Length == 0) return false;

        var block = new BlockUIContainer(BuildMathBlock(formula))
        {
            Margin = new Thickness(0, 10, 0, 12),
            Padding = new Thickness(0)
        };

        if (anchorAfter is not null) _doc.Blocks.InsertBefore(anchorAfter, block);
        else _doc.Blocks.Add(block);
        return true;
    }

    private UIElement BuildCodeBlock(string code, string language)
    {
        return BuildCodeBlockView(code, language).Root;
    }

    private (UIElement Root, TextBox Editor) BuildCodeBlockView(string code, string language)
    {
        var border = new Border
        {
            Background = ResolveBrush("Brush.Bg.Secondary", new SolidColorBrush(Color.FromRgb(248, 249, 250))),
            BorderBrush = ResolveBrush("Brush.Border", new SolidColorBrush(Color.FromRgb(222, 226, 230))),
            BorderThickness = new Thickness(1),
            CornerRadius = ResolveCornerRadius("Radius.Md", new CornerRadius(10)),
            ClipToBounds = true,
            MaxWidth = CodeBlockMaxWidth
        };

        var root = new DockPanel { LastChildFill = true };
        border.Child = root;

        var header = new DockPanel
        {
            LastChildFill = false,
            Background = ResolveBrush("Brush.Bg.Tertiary", new SolidColorBrush(Color.FromRgb(241, 243, 245))),
            Height = 34
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "code" : language,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveBrush("Brush.Text.Secondary", Brushes.DimGray),
            FontFamily = ResolveFont("Font.Mono", _doc.FontFamily),
            FontSize = 12
        };
        header.Children.Add(label);

        var copy = new Button
        {
            Padding = new Thickness(0),
            Width = 44,
            Height = 22,
            Margin = new Thickness(0, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = ResolveBrush("Brush.Border", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Foreground = ResolveBrush("Brush.Text.Secondary", Brushes.DimGray),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var copyLabel = new TextBlock
        {
            Text = "复制",
            FontSize = 12,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        copy.Content = copyLabel;
        TextBox? codeViewerForCopy = null;
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(codeViewerForCopy?.Text ?? code);
                copyLabel.Text = "已复制";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    copyLabel.Text = "复制";
                };
                timer.Start();
            }
            catch
            {
                copyLabel.Text = "复制失败";
            }
        };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);

        var codeBlockMaxHeight = CodeBlockMaxHeight;
        var hasCodeBlockMaxHeight = IsFinitePositive(codeBlockMaxHeight);

        var codeViewer = new TextBox
        {
            Text = code,
            Padding = new Thickness(14, 12, 14, 12),
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            AcceptsTab = true,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = ResolveFont("Font.Mono", new FontFamily("Consolas")),
            FontSize = 13,
            Foreground = ResolveBrush("Brush.Text.Primary", Brushes.Black),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = hasCodeBlockMaxHeight
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled
        };
        codeViewer.PreviewMouseWheel += OnCodeViewerPreviewMouseWheel;
        if (hasCodeBlockMaxHeight)
            codeViewer.MaxHeight = codeBlockMaxHeight;
        codeViewerForCopy = codeViewer;
        root.Children.Add(codeViewer);
        return (border, codeViewer);
    }

    private static bool IsFinitePositive(double value)
    {
        return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool IsMathFenceLanguage(string language)
    {
        return language.Equals("math", StringComparison.OrdinalIgnoreCase)
            || language.Equals("latex", StringComparison.OrdinalIgnoreCase)
            || language.Equals("tex", StringComparison.OrdinalIgnoreCase);
    }

    private UIElement BuildMathBlock(string formula)
    {
        var border = new Border
        {
            Background = ResolveBrush("Brush.Bg.Secondary", new SolidColorBrush(Color.FromRgb(248, 249, 250))),
            BorderBrush = ResolveBrush("Brush.Border", new SolidColorBrush(Color.FromRgb(222, 226, 230))),
            BorderThickness = new Thickness(1),
            CornerRadius = ResolveCornerRadius("Radius.Md", new CornerRadius(10)),
            Padding = new Thickness(16, 14, 16, 14),
            MaxWidth = 900
        };

        if (TryBuildStructuredMathBlock(formula) is { } structured)
        {
            border.Child = structured;
            return border;
        }

        try
        {
            border.Child = new FormulaControl
            {
                Formula = formula,
                Scale = 18,
                Foreground = ResolveBrush("Brush.Text.Primary", Brushes.Black),
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        catch
        {
            border.Child = new TextBlock
            {
                Text = "$$" + Environment.NewLine + formula + Environment.NewLine + "$$",
                TextWrapping = TextWrapping.Wrap,
                FontFamily = ResolveFont("Font.Mono", new FontFamily("Consolas")),
                Foreground = ResolveBrush("Brush.Text.Secondary", Brushes.DimGray)
            };
        }

        return border;
    }

    private UIElement? TryBuildStructuredMathBlock(string formula)
    {
        var match = MathEnvironmentRegex().Match(formula);
        if (!match.Success) return null;

        var env = match.Groups["env"].Value;
        var prefix = formula[..match.Index].Trim();
        var suffix = formula[(match.Index + match.Length)..].Trim();
        var explicitLeft = ConsumeOuterDelimiter(ref prefix, left: true);
        var explicitRight = ConsumeOuterDelimiter(ref suffix, left: false);
        var body = match.Groups["body"].Value.Trim();
        if (env.Equals("array", StringComparison.Ordinal) || env.Equals("alignedat", StringComparison.Ordinal))
            body = Regex.Replace(body, @"^\s*\{[^}]*\}", string.Empty).Trim();

        var rows = SplitMathRows(body)
            .Select(row => SplitMathCells(row).Select(c => c.Trim()).ToList())
            .Where(row => row.Count > 0 && row.Any(c => c.Length > 0))
            .ToList();
        if (rows.Count == 0) return null;

        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var columnCount = Math.Max(1, rows.Max(r => r.Count));
        for (int i = 0; i < columnCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < rows.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count; c++)
            {
                var cell = BuildMathCell(rows[r][c], env, c);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        var wrapped = WrapStructuredMath(grid, env, rows.Count, explicitLeft, explicitRight);
        FrameworkElement content = wrapped;
        if (prefix.Length > 0 || suffix.Length > 0)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (prefix.Length > 0)
                row.Children.Add(BuildInlineMathElement(prefix, new Thickness(0, 0, 10, 0)));
            row.Children.Add(wrapped);
            if (suffix.Length > 0)
                row.Children.Add(BuildInlineMathElement(suffix, new Thickness(10, 0, 0, 0)));
            content = row;
        }

        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private FrameworkElement WrapStructuredMath(Grid grid, string env, int rowCount, string? leftOverride, string? rightOverride)
    {
        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        var left = leftOverride ?? BracketForEnvironment(env, left: true);
        var right = rightOverride ?? BracketForEnvironment(env, left: false);

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrEmpty(left))
        {
            var leftText = BuildMathDelimiter(left, rowCount);
            root.Children.Add(leftText);
            Grid.SetColumn(leftText, 0);
        }

        grid.Margin = new Thickness(string.IsNullOrEmpty(left) ? 0 : 8, 0, string.IsNullOrEmpty(right) ? 0 : 8, 0);
        root.Children.Add(grid);
        Grid.SetColumn(grid, 1);

        if (!string.IsNullOrEmpty(right))
        {
            var rightText = BuildMathDelimiter(right, rowCount);
            root.Children.Add(rightText);
            Grid.SetColumn(rightText, 2);
        }

        return root;
    }

    private TextBlock BuildMathDelimiter(string text, int rowCount) => new()
    {
        Text = text,
        FontSize = Math.Max(42, 24 + rowCount * 12),
        FontFamily = new FontFamily("Cambria Math"),
        Foreground = ResolveBrush("Brush.Text.Primary", Brushes.Black),
        VerticalAlignment = VerticalAlignment.Center,
        LineHeight = Math.Max(42, 24 + rowCount * 12)
    };

    private static string BracketForEnvironment(string env, bool left) => env switch
    {
        "pmatrix" => left ? "(" : ")",
        "bmatrix" => left ? "[" : "]",
        "Bmatrix" or "cases" => left ? "{" : (env == "cases" ? string.Empty : "}"),
        "vmatrix" => "|",
        "Vmatrix" => "||",
        _ => string.Empty
    };

    private static string? ConsumeOuterDelimiter(ref string formulaPart, bool left)
    {
        var text = formulaPart.Trim();
        var pattern = left
            ? @"^\\left\s*(?<d>\\?\{|\[|\(|\||\.)$"
            : @"^\\right\s*(?<d>\\?\}|\]|\)|\||\.)$";
        var match = Regex.Match(text, pattern);
        if (!match.Success) return null;
        formulaPart = string.Empty;
        return MapLatexDelimiter(match.Groups["d"].Value);
    }

    private static string MapLatexDelimiter(string delimiter) => delimiter switch
    {
        @"\{" => "{",
        @"\}" => "}",
        "." => string.Empty,
        _ => delimiter
    };

    private FrameworkElement BuildMathCell(string source, string env, int column)
    {
        var normalized = NormalizeStructuredCell(source);
        var margin = new Thickness(column == 0 ? 0 : 14, 3, 0, 3);

        if (normalized.Length == 0)
        {
            return new TextBlock { Text = " ", Margin = margin };
        }

        if (env == "cases" && column > 0 && LooksPlainTextMathCell(normalized))
        {
            return new TextBlock
            {
                Text = StripLatexTextCommands(normalized),
                Margin = margin,
                FontSize = 15,
                Foreground = ResolveBrush("Brush.Text.Secondary", Brushes.DimGray),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        try
        {
            return new FormulaControl
            {
                Formula = normalized,
                Scale = 16,
                Foreground = ResolveBrush("Brush.Text.Primary", Brushes.Black),
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch
        {
            return new TextBlock
            {
                Text = StripLatexTextCommands(normalized),
                Margin = margin,
                FontFamily = ResolveFont("Font.Mono", new FontFamily("Consolas")),
                FontSize = 14,
                Foreground = ResolveBrush("Brush.Text.Secondary", Brushes.DimGray),
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }

    private FrameworkElement BuildInlineMathElement(string formula, Thickness margin)
    {
        try
        {
            return new FormulaControl
            {
                Formula = NormalizeStructuredCell(formula),
                Scale = 16,
                Foreground = ResolveBrush("Brush.Text.Primary", Brushes.Black),
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch
        {
            return new TextBlock
            {
                Text = StripLatexTextCommands(formula),
                Margin = margin,
                FontFamily = ResolveFont("Font.Mono", new FontFamily("Consolas")),
                FontSize = 14,
                Foreground = ResolveBrush("Brush.Text.Secondary", Brushes.DimGray),
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }

    private static List<string> SplitMathRows(string body)
    {
        return Regex.Split(body, @"(?<!\\)\\\\")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static List<string> SplitMathCells(string row)
    {
        return Regex.Split(row, @"(?<!\\)&").ToList();
    }

    private static string NormalizeStructuredCell(string source)
    {
        return source
            .Replace(@"\displaystyle", string.Empty, StringComparison.Ordinal)
            .Replace(@"\textstyle", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static bool LooksPlainTextMathCell(string source)
    {
        return source.Contains(@"\text", StringComparison.Ordinal)
            || Regex.IsMatch(source, @"[A-Za-z]{3,}");
    }

    private static string StripLatexTextCommands(string source)
    {
        var text = Regex.Replace(source, @"\\text\{(?<text>[^}]*)\}", "${text}");
        text = text.Replace(@"\quad", " ", StringComparison.Ordinal)
            .Replace(@"\,", " ", StringComparison.Ordinal)
            .Replace(@"\ ", " ", StringComparison.Ordinal);
        return text.Trim();
    }

    private bool TryAppendMarkdownQuote(string slice, WpfBlock? anchorAfter)
    {
        if (!IsTopLevelMarkdownQuote(slice)) return false;

        var quote = MolaGptMarkupBlocks.BuildMarkdownQuote(UnquoteMarkdown(slice), this);
        var block = new BlockUIContainer(quote)
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0)
        };

        if (anchorAfter is not null) _doc.Blocks.InsertBefore(anchorAfter, block);
        else _doc.Blocks.Add(block);
        return true;
    }

    private static bool IsTopLevelMarkdownQuote(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var trimmed = source.TrimStart(' ', '\t', '\r', '\n');
        return trimmed.StartsWith(">", StringComparison.Ordinal);
    }

    private static string NormalizeCodeLanguage(string info)
    {
        var lang = (info ?? string.Empty).Trim();
        if (lang.Length == 0) return string.Empty;
        var first = lang.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        first = first.Trim().TrimStart('.');
        return first.Equals("csharp", StringComparison.OrdinalIgnoreCase) ? "c#" : first.ToLowerInvariant();
    }

    private void FillCodeInlines(TextBlock text, string code, string language)
    {
        if (language is "c#" or "cs" or "csharp")
        {
            FillCSharpCodeInlines(text, code);
            return;
        }

        text.Text = code;
    }

    private void FillCSharpCodeInlines(TextBlock text, string code)
    {
        var keywordBrush = ResolveBrush("Brush.Primary.Hover", Brushes.IndianRed);
        var commentBrush = ResolveBrush("Brush.Text.Muted", Brushes.Gray);
        var stringBrush = ResolveBrush("Brush.Success", Brushes.SeaGreen);
        var defaultBrush = ResolveBrush("Brush.Text.Primary", Brushes.Black);
        var pattern = @"//.*?$|@?""(?:[^""]|"""")*""|'(?:\\.|[^'\\])'|\b[A-Za-z_][A-Za-z0-9_]*\b";

        int last = 0;
        foreach (Match match in Regex.Matches(code, pattern, RegexOptions.Multiline))
        {
            if (match.Index > last)
                text.Inlines.Add(new Run(code[last..match.Index]) { Foreground = defaultBrush });

            var token = match.Value;
            Brush brush = defaultBrush;
            if (token.StartsWith("//", StringComparison.Ordinal))
                brush = commentBrush;
            else if (token.StartsWith("\"", StringComparison.Ordinal) || token.StartsWith("@\"", StringComparison.Ordinal) || token.StartsWith("'", StringComparison.Ordinal))
                brush = stringBrush;
            else if (s_cSharpKeywords.Contains(token))
                brush = keywordBrush;

            text.Inlines.Add(new Run(token) { Foreground = brush });
            last = match.Index + match.Length;
        }

        if (last < code.Length)
            text.Inlines.Add(new Run(code[last..]) { Foreground = defaultBrush });
    }

    private void ApplyInlineMath(Paragraph paragraph)
    {
        ReplaceInlineMath(paragraph.Inlines);
    }

    private void ReplaceInlineMath(InlineCollection inlines)
    {
        foreach (var inline in inlines.Cast<Inline>().ToList())
        {
            if (inline is Run run)
            {
                ReplaceInlineMathRun(inlines, run);
            }
            else if (inline is Span span)
            {
                ReplaceInlineMath(span.Inlines);
            }
        }
    }

    private void ReplaceInlineMathRun(InlineCollection owner, Run run)
    {
        var original = run.Text;
        if (string.IsNullOrEmpty(original) || !MayContainLatexInlineMath(original)) return;

        var matches = LatexInlineMathRegex().Matches(original);
        if (matches.Count == 0) return;

        var replacements = new List<Inline>();
        var last = 0;
        foreach (Match match in matches)
        {
            if (match.Index > last)
                replacements.Add(CloneRun(run, original[last..match.Index]));

            var formula = ExtractLatexInlineFormula(match);
            replacements.Add(formula.Length == 0
                ? CloneRun(run, match.Value)
                : new InlineUIContainer(BuildInlineMathElement(formula, new Thickness(1, 0, 1, 0)))
                {
                    BaselineAlignment = BaselineAlignment.Center
                });

            last = match.Index + match.Length;
        }

        if (last < original.Length)
            replacements.Add(CloneRun(run, original[last..]));

        foreach (var replacement in replacements)
            owner.InsertBefore(run, replacement);
        owner.Remove(run);
    }

    private static bool MayContainLatexInlineMath(string source)
        => source.IndexOf('$') >= 0
           || source.IndexOf(@"\(", StringComparison.Ordinal) >= 0
           || source.IndexOf(@"\[", StringComparison.Ordinal) >= 0;

    private static string ExtractLatexInlineFormula(Match match)
    {
        if (match.Groups["dollar"].Success) return match.Groups["dollar"].Value.Trim();
        if (match.Groups["paren"].Success) return match.Groups["paren"].Value.Trim();
        if (match.Groups["bracket"].Success) return match.Groups["bracket"].Value.Trim();
        if (match.Groups["math"].Success) return match.Groups["math"].Value.Trim();
        return string.Empty;
    }

    private static Run CloneRun(Run template, string text) => new(text)
    {
        FontFamily = template.FontFamily,
        FontSize = template.FontSize,
        FontStyle = template.FontStyle,
        FontWeight = template.FontWeight,
        Foreground = template.Foreground,
        Background = template.Background,
        TextDecorations = template.TextDecorations
    };

    private Brush ResolveBrush(string key, Brush fallback)
    {
        try { return TryFindResource(key) as Brush ?? fallback; }
        catch { return fallback; }
    }

    private FontFamily ResolveFont(string key, FontFamily fallback)
    {
        try { return TryFindResource(key) as FontFamily ?? fallback; }
        catch { return fallback; }
    }

    private CornerRadius ResolveCornerRadius(string key, CornerRadius fallback)
    {
        try { return TryFindResource(key) is CornerRadius cr ? cr : fallback; }
        catch { return fallback; }
    }

    private static string UnquoteMarkdown(string source)
    {
        var normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int pos = 0;
            int spaces = 0;
            while (pos < line.Length && line[pos] == ' ' && spaces < 3)
            {
                pos++;
                spaces++;
            }

            if (pos < line.Length && line[pos] == '>')
            {
                pos++;
                if (pos < line.Length && line[pos] == ' ') pos++;
                lines[i] = line.Substring(pos);
            }
        }

        return string.Join("\n", lines).Trim();
    }

    private string ProcessCitationRefs(string source)
    {
        if (string.IsNullOrEmpty(source) || !source.Contains("<ref", StringComparison.OrdinalIgnoreCase))
            return source;

        var sources = Sources ?? Array.Empty<SourceReference>();
        var hasSources = sources.Count > 0;

        return RefTagRegex().Replace(source, match =>
        {
            if (!hasSources) return string.Empty;

            var attrs = match.Groups["attrs"].Success ? match.Groups["attrs"].Value : match.Groups["attrs2"].Value;
            var sourceMatch = RefSourceRegex().Match(attrs);
            if (!sourceMatch.Success)
                return match.Groups["inner"].Success ? match.Groups["inner"].Value : string.Empty;

            var ids = ParseSourceIds(sourceMatch.Groups["value"].Value);
            if (ids.Count == 0)
                return match.Groups["inner"].Success ? match.Groups["inner"].Value : string.Empty;

            var links = new List<string>();
            foreach (var id in ids)
            {
                var sourceRef = sources.FirstOrDefault(s => s.Id == id);
                if (sourceRef is null) continue;
                var url = string.IsNullOrWhiteSpace(sourceRef.Url) ? "#" : EscapeMarkdownLinkUrl(sourceRef.Url);
                var title = EscapeMarkdownTitle(sourceRef.Title);
                links.Add(string.IsNullOrEmpty(title)
                    ? $"[[来源 {id}]]({url})"
                    : $"[[来源 {id}]]({url} \"{title}\")");
            }

            if (links.Count == 0)
                return match.Groups["inner"].Success ? match.Groups["inner"].Value : string.Empty;

            return string.Join(" ", links) + (match.Groups["inner"].Success ? match.Groups["inner"].Value : string.Empty);
        });
    }

    private static List<int> ParseSourceIds(string sourceAttr)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var part in sourceAttr.Split(new[] { ',', '，', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var range = Regex.Match(part, @"^(\d+)\s*[-~]\s*(\d+)$");
            if (range.Success
                && int.TryParse(range.Groups[1].Value, out var start)
                && int.TryParse(range.Groups[2].Value, out var end))
            {
                var min = Math.Min(start, end);
                var max = Math.Max(start, end);
                for (var id = min; id <= max; id++)
                {
                    if (seen.Add(id)) result.Add(id);
                }
                continue;
            }

            if (int.TryParse(part, out var single) && seen.Add(single))
                result.Add(single);
        }

        return result;
    }

    private static string EscapeMarkdownLinkUrl(string value) =>
        value.Replace(")", "%29");

    private static string EscapeMarkdownTitle(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void ApplyMolaMarkdownStyles(WpfBlock block, IReadOnlyDictionary<string, string>? inlineMath = null)
    {
        block.FontFamily = _doc.FontFamily;
        block.FontSize = MessageTextFontSize;
        block.LineHeight = MessageTextLineHeight;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.TextAlignment = TextAlignment.Left;
        block.Foreground = _doc.Foreground;
        block.Margin = block switch
        {
            Paragraph => new Thickness(0, 0, 0, 8),
            List => new Thickness(0, 4, 0, 10),
            Table => new Thickness(0, 8, 0, 12),
            _ => block.Margin
        };

        if (block is Paragraph paragraph)
            ApplyParagraphTypography(paragraph, inlineMath);
        else if (block is System.Windows.Documents.List list)
            ApplyListTypography(list, inlineMath: inlineMath);
        else if (block is Table table)
            ApplyTableTypography(table, inlineMath: inlineMath);

        if (block is Section section)
        {
            section.BorderBrush = TryFindResource("Brush.Primary") as Brush ?? Brushes.HotPink;
            section.BorderThickness = new Thickness(3, 0, 0, 0);
            section.Background = TryFindResource("Brush.Primary.Blockquote") as Brush ?? new SolidColorBrush(Color.FromArgb(13, 190, 114, 127));
            section.Foreground = TryFindResource("Brush.Text.Secondary") as Brush ?? Brushes.DimGray;
            section.FontStyle = FontStyles.Italic;
            section.Padding = new Thickness(16, 8, 12, 8);
            section.Margin = new Thickness(0, 12, 0, 12);

            foreach (var child in section.Blocks.Cast<WpfBlock>().ToList())
            {
                ApplyNestedBlockTypography(child, section.Foreground, inlineMath);
            }
        }
    }

    private void ApplyParagraphTypography(Paragraph paragraph, IReadOnlyDictionary<string, string>? inlineMath = null)
    {
        paragraph.FontSize = MessageTextFontSize;
        paragraph.LineHeight = MessageTextLineHeight;
        paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        paragraph.TextAlignment = TextAlignment.Left;
        ApplyMarkdownImageLayout(paragraph);
        RestoreInlineMathPlaceholders(paragraph.Inlines, inlineMath);
        ApplyInlineMath(paragraph);
        ApplyCjkPunctuation(paragraph.Inlines);
        ApplyWebFontFallback(paragraph.Inlines);
        WireHyperlinks(paragraph.Inlines);
        ApplyCitationControls(paragraph.Inlines);
        StyleInlineCode(paragraph);
    }

    /// <summary>
    /// Ensures a blank line before and after any line that is solely a markdown
    /// image. Without the blank line, CommonMark fuses "![img]\ncaption" into a
    /// single paragraph, so the image renders as an inline <c>InlineUIContainer</c>
    /// that overflows downward and paints over the caption text. Promoting it to
    /// its own block routes it through the fixed-size image-card path instead.
    /// Lines inside fenced code blocks and 4-space indented code are left alone.
    /// Partial images still streaming (no closing "](url)") don't match and are
    /// untouched until complete.
    /// </summary>
    private static string EnsureBlankLinesAroundStandaloneImages(string src)
    {
        if (string.IsNullOrEmpty(src) || src.IndexOf("![", StringComparison.Ordinal) < 0)
            return src;

        var lines = src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var outLines = new List<string>(lines.Length + 8);
        bool inFence = false;
        bool changed = false;

        foreach (var line in lines)
        {
            if (CodeFenceLineRegex().IsMatch(line))
                inFence = !inFence;

            bool isImageLine = !inFence
                && (StandaloneImageLineRegex().IsMatch(line)
                    || StandaloneLinkedImageLineRegex().IsMatch(line));

            if (isImageLine)
            {
                if (outLines.Count > 0 && outLines[^1].Length != 0)
                {
                    outLines.Add(string.Empty);
                    changed = true;
                }
                outLines.Add(line);
                outLines.Add(string.Empty);
            }
            else
            {
                // Collapse the speculative blank we appended after an image when
                // a blank already follows in the source, so spacing stays stable.
                if (outLines.Count > 0 && outLines[^1].Length == 0 && line.Length == 0)
                    continue;
                outLines.Add(line);
            }
        }

        // Trim a trailing speculative blank so we don't grow the source forever
        // across streaming frames (which would defeat the append-only prefix check).
        if (outLines.Count > 0 && outLines[^1].Length == 0
            && (lines.Length == 0 || lines[^1].Length != 0))
        {
            outLines.RemoveAt(outLines.Count - 1);
        }

        if (!changed && outLines.Count == lines.Length)
            return src;

        return string.Join("\n", outLines);
    }

    private void ApplyMarkdownImageLayout(Paragraph paragraph)
    {
        var snapshot = paragraph.Inlines.Cast<Inline>().ToList();
        var hasImage = false;
        foreach (var inline in snapshot)
        {
            if (!TryStyleMarkdownImageInline(inline))
                continue;

            hasImage = true;
            if (inline.PreviousInline is not null and not LineBreak)
                paragraph.Inlines.InsertBefore(inline, new LineBreak());
            if (inline.NextInline is not null and not LineBreak)
                paragraph.Inlines.InsertAfter(inline, new LineBreak());
        }

        // When an image shares a paragraph with text (markdown image directly
        // followed by a caption line, no blank line between them), the whole
        // paragraph would otherwise inherit LineStackingStrategy.BlockLineHeight
        // with a fixed 24px LineHeight — which clamps the image's line box to
        // 24px and lets the image overflow downward, painting on top of the
        // following caption lines. Switch such paragraphs to MaxHeight so each
        // line grows to its tallest inline (the image), and clear the fixed
        // LineHeight so text lines still size naturally.
        if (hasImage)
        {
            paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            paragraph.LineHeight = double.NaN;
        }
    }

    private bool TryStyleMarkdownImageInline(Inline inline)
    {
        var found = false;
        if (inline is InlineUIContainer { Child: Image image } container)
        {
            var source = ResolveImageSource(image);
            if (IsAiGeneratedImageUrl(source))
            {
                container.Child = null;
                container.Child = BuildAiGeneratedImageCard(image, source!);
            }
            else
            {
                StyleMarkdownImage(image);
            }
            found = true;
        }
        else if (inline is Span span)
        {
            foreach (var child in span.Inlines.Cast<Inline>().ToList())
                found |= TryStyleMarkdownImageInline(child);
        }

        return found;
    }

    private FrameworkElement BuildAiGeneratedImageCard(Image image, string source)
    {
        StyleMarkdownImage(image);
        image.Margin = new Thickness(0);
        image.HorizontalAlignment = HorizontalAlignment.Stretch;
        image.VerticalAlignment = VerticalAlignment.Top;
        image.Stretch = Stretch.Uniform;
        image.MaxWidth = double.PositiveInfinity;
        image.MaxHeight = double.PositiveInfinity;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var overlay = BuildAiImageSkeletonOverlay();
        var imageHost = new Grid
        {
            Children =
            {
                image,
                overlay
            }
        };

        image.Loaded += (_, _) => FadeOutAiImageOverlay(overlay);
        if (image.Source is BitmapImage bitmap && !bitmap.IsDownloading)
            Dispatcher.BeginInvoke(new Action(() => FadeOutAiImageOverlay(overlay)), DispatcherPriority.Loaded);
        else if (image.Source is BitmapImage downloadingBitmap)
            downloadingBitmap.DownloadCompleted += (_, _) => Dispatcher.BeginInvoke(new Action(() => FadeOutAiImageOverlay(overlay)), DispatcherPriority.Loaded);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10)
        };
        actions.Children.Add(BuildAiImageButton("下载", "\uE896", () => OpenExternal(source)));
        actions.Children.Add(BuildAiImageButton("预览", "\uE8A7", () => OpenExternal(source)));

        var root = new Grid
        {
            Children =
            {
                imageHost,
                new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = new LinearGradientBrush(
                        Color.FromArgb(0, 0, 0, 0),
                        Color.FromArgb(140, 0, 0, 0),
                        new Point(0.5, 0),
                        new Point(0.5, 1)),
                    Child = actions
                }
            }
        };

        return new Border
        {
            MaxWidth = 480,
            MinHeight = 240,
            Background = ResolveBrush("Brush.Bg.Tertiary", new SolidColorBrush(Color.FromRgb(241, 243, 245))),
            BorderBrush = ResolveBrush("Brush.Border", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Margin = new Thickness(0, 10, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = root
        };
    }

    private UIElement BuildAiImageSkeletonOverlay()
    {
        var overlay = new Grid
        {
            Background = BuildAiImageSkeletonBrush(),
            IsHitTestVisible = false,
            Opacity = 1
        };
        overlay.Children.Add(new Border
        {
            Width = 110,
            Background = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = new TranslateTransform(-130, 0)
        });

        if (overlay.Children[0] is Border shimmer && shimmer.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-130, 520, TimeSpan.FromSeconds(1.8))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        return overlay;
    }

    private static Brush BuildAiImageSkeletonBrush()
    {
        var group = new DrawingGroup();
        void Add(Color color, Point center, double radiusX, double radiusY)
        {
            var brush = new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = radiusX,
                RadiusY = radiusY,
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            brush.GradientStops.Add(new GradientStop(color, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        }

        Add(Color.FromArgb(190, 220, 20, 60), new Point(0.12, 0.25), 0.55, 0.45);
        Add(Color.FromArgb(180, 255, 200, 0), new Point(0.88, 0.12), 0.42, 0.55);
        Add(Color.FromArgb(166, 0, 200, 130), new Point(0.75, 0.88), 0.55, 0.42);
        Add(Color.FromArgb(174, 0, 140, 255), new Point(0.08, 0.75), 0.45, 0.52);
        Add(Color.FromArgb(128, 180, 0, 255), new Point(0.55, 0.45), 0.36, 0.36);
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    private static void FadeOutAiImageOverlay(UIElement overlay)
    {
        overlay.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        overlay.IsHitTestVisible = false;
    }

    private Button BuildAiImageButton(string label, string icon, Action action)
    {
        var button = new Button
        {
            Width = 34,
            Height = 34,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = label,
            Content = new TextBlock
            {
                Text = icon,
                FontFamily = ResolveFont("Font.Icon", new FontFamily("Segoe MDL2 Assets")),
                Foreground = Brushes.White,
                FontSize = 14,
                TextAlignment = TextAlignment.Center
            }
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static void OpenExternal(string url)
    {
        _ = TryOpenExternal(url);
    }

    private static bool TryOpenExternal(string? url)
    {
        if (!TryNormalizeExternalTarget(url, out var target))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open external link '{target}': {ex.Message}");
            return false;
        }
    }

    private static bool TryNormalizeExternalTarget(string? url, out string target)
    {
        target = string.Empty;
        var trimmed = url?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "#")
            return false;

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            target = "https:" + trimmed;
            return true;
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            || BareDomainRegex().IsMatch(trimmed))
        {
            target = "https://" + trimmed;
            return true;
        }

        if (LocalhostUrlRegex().IsMatch(trimmed))
        {
            target = "http://" + trimmed;
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && !string.IsNullOrWhiteSpace(absolute.Scheme))
        {
            target = trimmed;
            return true;
        }

        return false;
    }

    private static void WireExternalClick(FrameworkElement element, string target)
    {
        var start = new Point(double.NaN, double.NaN);
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(element);
        };
        element.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var end = e.GetPosition(element);
            var shouldOpen = IsClickGesture(start, end);
            start = new Point(double.NaN, double.NaN);
            if (!shouldOpen)
                return;

            if (TryOpenExternal(target))
                e.Handled = true;
        };
    }

    private void WireMarkdownImagePreview(FrameworkElement element, string imageUrl)
    {
        var start = new Point(double.NaN, double.NaN);
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(element);
        };
        element.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var end = e.GetPosition(element);
            var shouldOpen = IsClickGesture(start, end);
            start = new Point(double.NaN, double.NaN);
            if (!shouldOpen)
                return;

            ImagePreviewWindow.Show(Window.GetWindow(this), imageUrl, Path.GetFileName(SafeUnescape(imageUrl)));
            e.Handled = true;
        };
    }

    private static bool IsClickGesture(Point start, Point end) =>
        !double.IsNaN(start.X)
        && !double.IsNaN(start.Y)
        && Math.Abs(end.X - start.X) <= SystemParameters.MinimumHorizontalDragDistance
        && Math.Abs(end.Y - start.Y) <= SystemParameters.MinimumVerticalDragDistance;

    private static string? ResolveImageSource(Image image)
    {
        return image.Source switch
        {
            BitmapImage bitmap when bitmap.UriSource is not null => bitmap.UriSource.ToString(),
            BitmapFrame frame when frame.Decoder?.ToString() is { Length: > 0 } value => value,
            { } source => source.ToString(),
            _ => null
        };
    }

    private static bool IsAiGeneratedImageUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && (url.Contains("=imgtemp", StringComparison.OrdinalIgnoreCase)
               || url.Contains("imgtempdel", StringComparison.OrdinalIgnoreCase));

    private void StyleMarkdownImage(Image image)
    {
        image.Stretch = Stretch.Uniform;
        image.StretchDirection = StretchDirection.DownOnly;
        image.HorizontalAlignment = HorizontalAlignment.Left;
        image.VerticalAlignment = VerticalAlignment.Top;
        image.Margin = new Thickness(0, 8, 0, 10);
        image.SnapsToDevicePixels = true;
        ApplyMarkdownImageConstraint(image);
        image.Loaded -= OnMarkdownImageLoaded;
        image.Loaded += OnMarkdownImageLoaded;
        image.Cursor = Cursors.Hand;
        image.PreviewMouseLeftButtonDown -= OnMarkdownInlineImagePreviewMouseLeftButtonDown;
        image.PreviewMouseLeftButtonUp -= OnMarkdownInlineImagePreviewMouseLeftButtonUp;
        image.PreviewMouseLeftButtonDown += OnMarkdownInlineImagePreviewMouseLeftButtonDown;
        image.PreviewMouseLeftButtonUp += OnMarkdownInlineImagePreviewMouseLeftButtonUp;
    }

    private void OnMarkdownInlineImagePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
            _markdownImagePreviewStart = e.GetPosition(element);
    }

    private void OnMarkdownInlineImagePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image image)
            return;

        var end = e.GetPosition(image);
        var shouldOpen = IsClickGesture(_markdownImagePreviewStart, end);
        _markdownImagePreviewStart = new Point(double.NaN, double.NaN);
        if (!shouldOpen)
            return;

        var source = ResolveImageSource(image);
        if (string.IsNullOrWhiteSpace(source))
            return;

        ImagePreviewWindow.Show(Window.GetWindow(this), source, Path.GetFileName(SafeUnescape(source)));
        e.Handled = true;
    }

    private void OnMarkdownImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image && image.Tag is not MarkdownImageCardState)
            ApplyMarkdownImageConstraint(image);
    }

    private void OnMarkdownImageCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border root && root.Tag is MarkdownImageCardState state)
            ApplyMarkdownImageCardConstraint(root, state);
    }

    private void RefreshMarkdownImageConstraints()
    {
        foreach (var card in EnumerateVisualChildren<Border>(_viewer))
        {
            if (card.Tag is MarkdownImageCardState state)
                ApplyMarkdownImageCardConstraint(card, state);
        }

        foreach (var image in EnumerateVisualChildren<Image>(_viewer))
        {
            if (image.Tag is MarkdownImageCardState)
                continue;

            ApplyMarkdownImageConstraint(image);
        }
    }

    private void ApplyMarkdownImageCardConstraint(Border root, MarkdownImageCardState state)
    {
        var (width, height) = CalculateMarkdownImageCardSize(state.IsAiGenerated);
        root.Width = width;
        root.Height = height;
        root.MaxWidth = width;
        root.MaxHeight = height;

        if (state.Image is not { } image)
            return;

        image.Width = width;
        image.Height = height;
        image.MaxWidth = width;
        image.MaxHeight = height;
    }

    private void ApplyMarkdownImageConstraint(Image image)
    {
        var available = ActualWidth;
        if (double.IsNaN(available) || available <= 0)
            available = _viewer.ActualWidth;
        if (double.IsNaN(available) || available <= 0)
            available = MarkdownImageMaxWidth;

        image.MaxWidth = Math.Max(240, Math.Min(MarkdownImageMaxWidth, available - 8));
        image.MaxHeight = MarkdownImageMaxHeight;
    }

    private static IEnumerable<T> EnumerateVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in EnumerateVisualChildren<T>(child))
                yield return nested;
        }
    }

    private void ApplyCitationControls(InlineCollection inlines)
    {
        var snapshot = inlines.Cast<Inline>().ToList();
        foreach (var inline in snapshot)
        {
            if (inline is Hyperlink hyperlink && TryGetCitationText(hyperlink, out var label))
            {
                var replacement = BuildCitationInline(hyperlink, label);
                inlines.InsertBefore(hyperlink, replacement);
                inlines.Remove(hyperlink);
                continue;
            }

            if (inline is Span span)
                ApplyCitationControls(span.Inlines);
        }
    }

    private InlineUIContainer BuildCitationInline(Hyperlink hyperlink, string label)
    {
        var primary = ResolveBrush("Brush.Primary", Brushes.HotPink);
        var border = new Border
        {
            Background = ResolveBrush("Brush.Primary.Tint", new SolidColorBrush(Color.FromArgb(20, 190, 114, 127))),
            BorderBrush = ResolveBrush("Brush.Primary.Border", new SolidColorBrush(Color.FromArgb(50, 190, 114, 127))),
            BorderThickness = new Thickness(1),
            CornerRadius = ResolveCornerRadius("Radius.Md", new CornerRadius(8)),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(3, 0, 3, 0),
            Cursor = Cursors.Hand,
            ToolTip = BuildCitationToolTip(hyperlink, label),
            Child = new TextBlock
            {
                Text = label,
                Foreground = primary,
                FontSize = MessageTextFontSize * 0.75,
                FontWeight = FontWeights.Medium,
                LineHeight = 14,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            }
        };

        var uri = hyperlink.NavigateUri;
        if (uri is not null && uri.ToString() != "#")
        {
            var target = string.IsNullOrWhiteSpace(uri.OriginalString)
                ? uri.ToString()
                : uri.OriginalString;
            WireExternalClick(border, target);
        }

        return new InlineUIContainer(border)
        {
            BaselineAlignment = BaselineAlignment.Center
        };
    }

    private object BuildCitationToolTip(Hyperlink hyperlink, string label)
    {
        var stack = new StackPanel { MaxWidth = 320 };
        stack.Children.Add(new TextBlock
        {
            Text = hyperlink.ToolTip as string ?? label,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("Brush.Text.Primary", Brushes.Black),
            Margin = new Thickness(0, 0, 0, 6)
        });
        if (hyperlink.NavigateUri is { } uri && uri.ToString() != "#")
        {
            stack.Children.Add(new TextBlock
            {
                Text = uri.ToString(),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ResolveBrush("Brush.Text.Secondary", Brushes.DimGray),
                FontSize = 12
            });
        }
        return stack;
    }

    private static bool TryGetCitationText(Hyperlink hyperlink, out string label)
    {
        label = GetInlineText(hyperlink).Trim();
        return Regex.IsMatch(label, @"^\[来源\s+\d+\]$");
    }

    private static string GetInlineText(Span span)
    {
        var parts = new List<string>();
        foreach (var inline in span.Inlines)
        {
            if (inline is Run run) parts.Add(run.Text);
            else if (inline is Span child) parts.Add(GetInlineText(child));
        }
        return string.Concat(parts);
    }

    private static string ProtectInlineMathForMarkdig(string source, out Dictionary<string, string>? inlineMath)
    {
        inlineMath = null;
        if (string.IsNullOrEmpty(source) || !MayContainLatexInlineMath(source))
            return source;

        // Markdig's advanced pipeline includes its own math parser, while
        // Markdig.Wpf 0.5 does not render those math nodes. Protect formulas
        // before parsing so we can restore them as WpfMath controls afterward.
        var matches = LatexInlineMathRegex().Matches(source);
        if (matches.Count == 0)
            return source;

        var builder = new StringBuilder(source.Length);
        var last = 0;
        var index = 0;

        foreach (Match match in matches)
        {
            var formula = ExtractLatexInlineFormula(match);
            if (formula.Length == 0)
                continue;

            inlineMath ??= new Dictionary<string, string>();
            var placeholder = InlineMathPlaceholderPrefix + index++ + InlineMathPlaceholderSuffix;
            inlineMath[placeholder] = formula;

            builder.Append(source, last, match.Index - last);
            builder.Append(placeholder);
            last = match.Index + match.Length;
        }

        if (inlineMath is null)
            return source;

        builder.Append(source, last, source.Length - last);
        return builder.ToString();
    }

    private void RestoreInlineMathPlaceholders(
        InlineCollection inlines,
        IReadOnlyDictionary<string, string>? inlineMath)
    {
        if (inlineMath is null || inlineMath.Count == 0)
            return;

        foreach (var inline in inlines.Cast<Inline>().ToList())
        {
            if (inline is Run run)
            {
                ReplaceInlineMathPlaceholderRun(inlines, run, inlineMath);
            }
            else if (inline is Span span)
            {
                RestoreInlineMathPlaceholders(span.Inlines, inlineMath);
            }
        }
    }

    private void ReplaceInlineMathPlaceholderRun(
        InlineCollection owner,
        Run run,
        IReadOnlyDictionary<string, string> inlineMath)
    {
        var original = run.Text;
        if (string.IsNullOrEmpty(original)
            || original.IndexOf(InlineMathPlaceholderPrefix, StringComparison.Ordinal) < 0)
        {
            return;
        }

        var replacements = new List<Inline>();
        var cursor = 0;

        while (cursor < original.Length)
        {
            var start = original.IndexOf(InlineMathPlaceholderPrefix, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                if (cursor < original.Length)
                    replacements.Add(CloneRun(run, original[cursor..]));
                break;
            }

            if (start > cursor)
                replacements.Add(CloneRun(run, original[cursor..start]));

            var end = original.IndexOf(
                InlineMathPlaceholderSuffix,
                start + InlineMathPlaceholderPrefix.Length,
                StringComparison.Ordinal);
            if (end < 0)
            {
                replacements.Add(CloneRun(run, original[start..]));
                break;
            }

            var placeholder = original[start..(end + InlineMathPlaceholderSuffix.Length)];
            if (inlineMath.TryGetValue(placeholder, out var formula))
            {
                replacements.Add(new InlineUIContainer(BuildInlineMathElement(formula, new Thickness(1, 0, 1, 0)))
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
            }
            else
            {
                replacements.Add(CloneRun(run, placeholder));
            }

            cursor = end + InlineMathPlaceholderSuffix.Length;
        }

        foreach (var replacement in replacements)
            owner.InsertBefore(run, replacement);
        owner.Remove(run);
    }

    private void ApplyNestedBlockTypography(
        WpfBlock block,
        Brush? foreground = null,
        IReadOnlyDictionary<string, string>? inlineMath = null)
    {
        block.FontFamily = _doc.FontFamily;
        block.FontSize = MessageTextFontSize;
        block.LineHeight = MessageTextLineHeight;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.TextAlignment = TextAlignment.Left;
        if (foreground is not null) block.Foreground = foreground;

        if (block is Paragraph paragraph)
            ApplyParagraphTypography(paragraph, inlineMath);
        else if (block is System.Windows.Documents.List list)
            ApplyListTypography(list, foreground, inlineMath);
        else if (block is Table table)
            ApplyTableTypography(table, foreground, inlineMath);
        else if (block is Section section)
        {
            foreach (var child in section.Blocks.Cast<WpfBlock>().ToList())
                ApplyNestedBlockTypography(child, foreground, inlineMath);
        }
    }

    private void ApplyListTypography(
        System.Windows.Documents.List list,
        Brush? foreground = null,
        IReadOnlyDictionary<string, string>? inlineMath = null)
    {
        list.FontFamily = _doc.FontFamily;
        list.FontSize = MessageTextFontSize;
        list.LineHeight = MessageTextLineHeight;
        list.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        list.TextAlignment = TextAlignment.Left;
        if (foreground is not null) list.Foreground = foreground;

        foreach (var item in list.ListItems.Cast<ListItem>().ToList())
        {
            ApplyListItemTypography(item, foreground);
            foreach (var child in item.Blocks.Cast<WpfBlock>().ToList())
                ApplyNestedBlockTypography(child, foreground, inlineMath);
        }
    }

    private void ApplyListItemTypography(ListItem item, Brush? foreground = null)
    {
        item.FontFamily = _doc.FontFamily;
        item.FontSize = MessageTextFontSize;
        item.TextAlignment = TextAlignment.Left;
        if (foreground is not null) item.Foreground = foreground;
    }

    private void ApplyTableTypography(
        Table table,
        Brush? foreground = null,
        IReadOnlyDictionary<string, string>? inlineMath = null)
    {
        var borderBrush = ResolveBrush("Brush.Border", new SolidColorBrush(Color.FromRgb(222, 226, 230)));
        var headerBackground = ResolveBrush("Brush.Bg.Tertiary", new SolidColorBrush(Color.FromRgb(241, 243, 245)));
        var cellBackground = ResolveBrush("Brush.Bg.Primary", Brushes.White);
        var textBrush = foreground ?? ResolveBrush("Brush.Text.Primary", Brushes.Black);

        table.CellSpacing = 0;
        table.BorderThickness = new Thickness(0);
        table.FontSize = MessageTextFontSize * 0.95;
        table.LineHeight = MessageTextLineHeight;
        table.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        table.TextAlignment = TextAlignment.Left;

        var rowIndex = 0;
        foreach (var group in table.RowGroups.Cast<TableRowGroup>().ToList())
        {
            foreach (var row in group.Rows.Cast<TableRow>().ToList())
            {
                var isHeaderRow = rowIndex == 0;
                var cellIndex = 0;
                foreach (var cell in row.Cells.Cast<TableCell>().ToList())
                {
                    cell.BorderBrush = borderBrush;
                    cell.BorderThickness = new Thickness(
                        cellIndex == 0 ? 1 : 0,
                        rowIndex == 0 ? 1 : 0,
                        1,
                        1);
                    cell.Padding = new Thickness(10, 6, 10, 6);
                    cell.Background = isHeaderRow ? headerBackground : cellBackground;
                    cell.Foreground = textBrush;
                    cell.FontWeight = isHeaderRow ? FontWeights.SemiBold : FontWeights.Normal;
                    cell.TextAlignment = TextAlignment.Left;
                    cell.LineHeight = MessageTextLineHeight;
                    cell.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

                    foreach (var child in cell.Blocks.Cast<WpfBlock>().ToList())
                    {
                        ApplyNestedBlockTypography(child, foreground, inlineMath);
                        ApplyTableCellBlockSpacing(child);
                    }

                    cellIndex++;
                }

                rowIndex++;
            }
        }
    }

    private static void ApplyTableCellBlockSpacing(WpfBlock block)
    {
        block.Margin = new Thickness(0);
        block.Padding = new Thickness(0);

        if (block is Section section)
        {
            foreach (var child in section.Blocks.Cast<WpfBlock>().ToList())
                ApplyTableCellBlockSpacing(child);
        }
        else if (block is System.Windows.Documents.List list)
        {
            list.Margin = new Thickness(0, 2, 0, 2);
            list.Padding = new Thickness(14, 0, 0, 0);
        }
    }

    private void ApplyWebFontFallback(InlineCollection inlines)
    {
        var latinFont = ResolveFont("Font.Latin", _doc.FontFamily);
        var cjkFont = ResolveFont("Font.Cjk", _doc.FontFamily);
        var snapshot = inlines.Cast<Inline>().ToList();

        foreach (var inline in snapshot)
        {
            if (inline is Span span)
            {
                ApplyWebFontFallback(span.Inlines);
                continue;
            }

            if (inline is not Run run || string.IsNullOrEmpty(run.Text) || IsMonoRun(run))
                continue;

            var pieces = SplitWebFontRuns(run, latinFont, cjkFont);
            if (pieces.Count == 1 && ReferenceEquals(pieces[0], run))
                continue;

            inlines.InsertBefore(run, pieces[0]);
            for (var i = 1; i < pieces.Count; i++)
                inlines.InsertAfter(pieces[i - 1], pieces[i]);
            inlines.Remove(run);
        }
    }

    private static void ApplyCjkPunctuation(InlineCollection inlines)
    {
        foreach (var inline in inlines.Cast<Inline>().ToList())
        {
            if (inline is Run run)
            {
                if (!string.IsNullOrEmpty(run.Text) && !IsMonoRun(run))
                    run.Text = NormalizeCjkPunctuation(run.Text);
            }
            else if (inline is Span span)
            {
                ApplyCjkPunctuation(span.Inlines);
            }
        }
    }

    private static string NormalizeCjkPunctuation(string text)
    {
        if (text.Length == 0 || !MayNeedCjkPunctuation(text))
            return text;

        var builder = new StringBuilder(text.Length);
        var doubleQuoteOpen = true;
        var singleQuoteOpen = true;
        var changed = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var replacement = ch switch
            {
                ',' when HasCjkAround(text, i) => '，',
                ':' when HasCjkAround(text, i) => '：',
                ';' when HasCjkAround(text, i) => '；',
                '?' when HasCjkAround(text, i) => '？',
                '!' when HasCjkAround(text, i) => '！',
                '(' when HasCjkAround(text, i) => '（',
                ')' when HasCjkAround(text, i) => '）',
                '"' when HasCjkAround(text, i) => ConsumeQuote(ref doubleQuoteOpen, '“', '”'),
                '\'' when HasCjkAround(text, i) => ConsumeQuote(ref singleQuoteOpen, '‘', '’'),
                '.' when ShouldUseCjkPeriod(text, i) => '。',
                _ => ch
            };

            if (replacement != ch) changed = true;
            builder.Append(replacement);
        }

        return changed ? builder.ToString() : text;
    }

    private static bool MayNeedCjkPunctuation(string text) =>
        text.IndexOfAny(new[] { ',', '.', ':', ';', '?', '!', '(', ')', '"', '\'' }) >= 0
        && text.Any(IsCjkTextChar);

    private static char ConsumeQuote(ref bool open, char openChar, char closeChar)
    {
        var result = open ? openChar : closeChar;
        open = !open;
        return result;
    }

    private static bool ShouldUseCjkPeriod(string text, int index)
    {
        if (!HasCjkAround(text, index))
            return false;

        var prev = FindPreviousMeaningfulChar(text, index);
        var next = FindNextMeaningfulChar(text, index);
        return !IsAsciiLetterOrDigit(prev) && !IsAsciiLetterOrDigit(next);
    }

    private static bool HasCjkAround(string text, int index) =>
        IsCjkTextChar(FindPreviousMeaningfulChar(text, index))
        || IsCjkTextChar(FindNextMeaningfulChar(text, index));

    private static char FindPreviousMeaningfulChar(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return '\0';
    }

    private static char FindNextMeaningfulChar(string text, int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return '\0';
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        ch is >= '0' and <= '9'
        or >= 'A' and <= 'Z'
        or >= 'a' and <= 'z';

    private static bool IsCjkTextChar(char ch) =>
        ch is >= '\u2E80' and <= '\u2EFF'
        or >= '\u3000' and <= '\u303F'
        or >= '\u3040' and <= '\u30FF'
        or >= '\u3100' and <= '\u312F'
        or >= '\u3400' and <= '\u4DBF'
        or >= '\u4E00' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF'
        or >= '\uFE10' and <= '\uFE4F'
        or >= '\uFF00' and <= '\uFFEF';

    private List<Run> SplitWebFontRuns(Run source, FontFamily latinFont, FontFamily cjkFont)
    {
        var text = source.Text;
        var result = new List<Run>();
        var start = 0;
        var currentCjk = ShouldUseCjkFont(text[0]);

        for (var i = 1; i < text.Length; i++)
        {
            var nextCjk = ShouldUseCjkFont(text[i]);
            if (nextCjk == currentCjk) continue;

            result.Add(CloneRun(source, text[start..i], currentCjk ? cjkFont : latinFont));
            start = i;
            currentCjk = nextCjk;
        }

        result.Add(CloneRun(source, text[start..], currentCjk ? cjkFont : latinFont));

        if (result.Count == 1 && Equals(result[0].FontFamily, source.FontFamily))
            return new List<Run> { source };

        return result;
    }

    private static Run CloneRun(Run source, string text, FontFamily fontFamily)
    {
        var run = new Run(text)
        {
            FontFamily = fontFamily,
            FontStretch = source.FontStretch,
            FontStyle = source.FontStyle,
            FontWeight = source.FontWeight,
            Foreground = source.Foreground,
            Background = source.Background,
            BaselineAlignment = source.BaselineAlignment,
            Language = source.Language
        };
        if (source.TextDecorations is not null)
            run.TextDecorations = source.TextDecorations;
        return run;
    }

    private static bool IsMonoRun(Run run)
    {
        var source = run.FontFamily?.Source ?? string.Empty;
        return source.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)
            || source.Contains("SFMono", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Menlo", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Mono", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseCjkFont(char ch) =>
        ch is >= '\u2E80' and <= '\u2EFF'   // CJK radicals
        or >= '\u2018' and <= '\u201F'      // CJK-style smart quotes
        or '\u2026'                         // Chinese ellipsis
        or >= '\u3000' and <= '\u303F'      // CJK punctuation: 、。「」
        or >= '\u3040' and <= '\u30FF'      // kana
        or >= '\u3100' and <= '\u312F'      // bopomofo
        or >= '\u3400' and <= '\u4DBF'      // CJK extension A
        or >= '\u4E00' and <= '\u9FFF'      // CJK unified ideographs
        or >= '\uF900' and <= '\uFAFF'      // CJK compatibility ideographs
        or >= '\uFE10' and <= '\uFE4F'      // vertical / compatibility forms
        or >= '\uFF00' and <= '\uFFEF';     // full-width punctuation/forms

    private void StyleInlineCode(Paragraph paragraph)
    {
        foreach (var run in paragraph.Inlines.OfType<Run>())
        {
            if (run.FontFamily?.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true
                || run.FontFamily?.Source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase) == true)
            {
                run.Background = ResolveBrush("Brush.Bg.Tertiary", new SolidColorBrush(Color.FromRgb(241, 243, 245)));
                run.Foreground = ResolveBrush("Brush.Primary.Hover", Brushes.IndianRed);
            }
        }
    }

    private void AnimateFade()
    {
        try
        {
            var anim = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _viewer.BeginAnimation(OpacityProperty, anim);
        }
        catch
        {
            _viewer.Opacity = 1.0;
        }
    }
}
