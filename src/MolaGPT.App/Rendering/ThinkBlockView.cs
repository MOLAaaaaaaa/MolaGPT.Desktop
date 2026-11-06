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
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MolaGPT.Presentation;
using MolaGPT.ViewModels;
using MolaGPT.App.Views;

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

    public static readonly StyledProperty<MessageViewModel?> MessageProperty =
        AvaloniaProperty.Register<ThinkBlockView, MessageViewModel?>(nameof(Message));

    public MessageViewModel? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

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
    private readonly Dictionary<string, ToolCallViewModel> _webTools = new();
    private readonly Dictionary<string, ToolRunRow> _toolRuns = new();
    private readonly DispatcherTimer _toolFoldTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly ConditionalWeakTable<ThinkingSegmentViewModel, ToolChoices> s_toolChoices = new();
    private ThinkingSegmentViewModel? _choiceSegment;

    private sealed class ToolChoices
    {
        public HashSet<string> OpenRuns { get; } = new();
        public HashSet<string> OpenEntries { get; } = new();
    }

    private sealed record ThinkingToolBlock : RenderBlock
    {
        public required ToolRunRow Row { get; init; }
    }

    /// <summary>Last parse, so a delta re-parses only the trailing blocks.</summary>
    private RenderDocument? _document;
    private bool _bodyStale = true;
    private bool _attached;
    private bool _hasEmbeddedCalls;

    static ThinkBlockView()
    {
        IsThinkingProperty.Changed.AddClassHandler<ThinkBlockView>((x, e) =>
        {
            x.UpdateStatus();
            x.Invalidate();
        });
        ElapsedSecondsProperty.Changed.AddClassHandler<ThinkBlockView>((x, _) => x.UpdateStatus());
        SourceProperty.Changed.AddClassHandler<ThinkBlockView>((x, _) => x.Invalidate());
        IsExpandedProperty.Changed.AddClassHandler<ThinkBlockView>((x, _) => x.ApplyExpanded());
        MessageProperty.Changed.AddClassHandler<ThinkBlockView>((x, change) =>
        {
            if (x._attached && change.OldValue is MessageViewModel old) old.PropertyChanged -= x.OnMessageChanged;
            x.ClearBody();
            if (x._attached && change.NewValue is MessageViewModel next) next.PropertyChanged += x.OnMessageChanged;
            if (x.IsExpanded) x.BuildBody();
        });
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
            HorizontalAlignment = HorizontalAlignment.Left,
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

        // Square: the shell's clip rounds its outer corners at whatever radius
        // the shell has.
        _stripe = new Border { Width = 4 };

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("4,*") };
        layout.Children.Add(_stripe);
        Grid.SetColumn(column, 1);
        layout.Children.Add(column);

        _shell = new Border
        {
            ClipToBounds = true,
            Child = layout
        }.WithRadius("Radius.Sm");

        LogicalChildren.Add(_shell);
        VisualChildren.Add(_shell);

        BuildContextMenu();
        _toolFoldTimer.Tick += OnToolFoldTick;
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
        _attached = true;
        ApplyBrushes();
        ActualThemeVariantChanged += OnVariantChanged;
        if (Message is { } message) message.PropertyChanged += OnMessageChanged;

        // The block templates are shared with the transcript's own rows and are
        // therefore found by walking up from here. A body built before the card
        // was attached would have found none of them, so the first real build
        // has to wait for this.
        if (IsExpanded) BuildBody();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        ActualThemeVariantChanged -= OnVariantChanged;
        if (Message is { } message) message.PropertyChanged -= OnMessageChanged;
        ClearBody();
    }

    private void OnMessageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hasEmbeddedCalls
            && (e.PropertyName is nameof(MessageViewModel.HasToolCalls) or nameof(MessageViewModel.ThinkingSegments)
                or nameof(MessageViewModel.Content) or nameof(MessageViewModel.IsStreaming)
                or nameof(MessageViewModel.IsRevealing)))
            Invalidate();
    }

    private void ClearBody()
    {
        _toolFoldTimer.Stop();
        SaveToolChoices();
        foreach (var run in _toolRuns.Values) run.Dispose();
        _toolRuns.Clear();
        _webTools.Clear();
        _document = null;
        _hasEmbeddedCalls = false;
        _rendered.Clear();
        _body.Children.Clear();
        _bodyStale = true;
        _choiceSegment = null;
    }

    private void OnToolFoldTick(object? sender, EventArgs e)
    {
        var pending = _toolRuns.Values.Where(run => run.NeedsAutoCollapse).ToArray();
        if (pending.Length == 0) { _toolFoldTimer.Stop(); return; }
        var allowed = this.FindAncestorOfType<TranscriptView>()?.CanAutoCollapseTools() ?? true;
        foreach (var run in pending) run.TryAutoCollapse(allowed);
    }

    private void SaveToolChoices()
    {
        if (_choiceSegment is null || _toolRuns.Count == 0) return;
        var choices = s_toolChoices.GetOrCreateValue(_choiceSegment);
        choices.OpenRuns.Clear();
        choices.OpenEntries.Clear();
        void SaveEntry(ToolRow entry)
        {
            if (entry.IsExpanded) choices.OpenEntries.Add(entry.Tool.Id);
            foreach (var child in entry.GroupItems) SaveEntry(child);
        }
        foreach (var run in _toolRuns.Values)
        {
            if (run.IsExpanded)
                foreach (var entry in run.Entries)
                    foreach (var id in entry.ToolIds) choices.OpenRuns.Add(id);
            foreach (var entry in run.Entries) SaveEntry(entry);
        }
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

        Shimmer.SetIsActive(_status, IsThinking);
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
            ClearBody();
            AnimationPerformanceTrace.ThinkingBodyBuilt(traceStarted, 0, 0);
            return;
        }

        // Incremental, not Parse: a full re-read of the whole segment on every
        // delta is quadratic over a segment that streams for a minute.
        _document = MessageDocumentParser.ParseIncremental(_document, source);
        _hasEmbeddedCalls = _document.Blocks.OfType<MarkupUnitBlock>().Any(block => block.Unit.ToolCallId is not null);
        SyncBody(BuildToolRuns(_document.Blocks));
        AnimationPerformanceTrace.ThinkingBodyBuilt(
            traceStarted,
            source.Length,
            _document.Blocks.Count);
    }

    private IReadOnlyList<RenderBlock> BuildToolRuns(IReadOnlyList<RenderBlock> blocks)
    {
        if (Message is not { } message) return blocks;
        SaveToolChoices();
        _choiceSegment = (DataContext as ThinkingRow)?.Segment;
        var choices = _choiceSegment is null ? null : s_toolChoices.GetOrCreateValue(_choiceSegment);
        var result = new List<RenderBlock>();
        var pending = new List<IReadOnlyList<ToolCallViewModel>>();
        var markups = new Dictionary<string, MarkupUnitBlock>();
        var seenRuns = new HashSet<string>();
        var seenCalls = new HashSet<string>();
        var bodyCalls = message.Content.Contains("data-tool-call", StringComparison.OrdinalIgnoreCase)
            ? MolaGptMarkupSplitter.Split(message.Content).Where(unit => unit.ToolCallId is not null)
                .Select(unit => unit.ToolCallId!).ToHashSet(StringComparer.Ordinal)
            : null;
        var thinkingCalls = message.ThinkingSegments.Count > 1 ? WebToolPresentation.ThinkingTools(message) : null;
        var following = new Dictionary<ToolRunRow, int>();
        ToolRunRow? preceding = null;
        var done = !message.IsStreaming && !message.IsRevealing;

        void Flush()
        {
            while (pending.Count > 0)
            {
                var first = pending[0][0].Id;
                var initial = !_toolRuns.TryGetValue(first, out var run);
                if (initial) _toolRuns[first] = run = new ToolRunRow(message, first);
                var owned = new List<IReadOnlyList<ToolCallViewModel>>();
                var later = new List<IReadOnlyList<ToolCallViewModel>>();
                var pastBoundary = false;
                foreach (var group in pending)
                {
                    var split = pastBoundary ? 0 : run!.RetiredToolIds is { } retired
                        ? group.TakeWhile(tool => retired.Contains(tool.Id)).Count() : group.Count;
                    if (split > 0) owned.Add(split == group.Count ? group : group.Take(split).ToArray());
                    if (split == group.Count) continue;
                    pastBoundary = true;
                    later.Add(group.Skip(split).ToArray());
                }
                run!.Sync(owned, animateNewEntries: !done, markups);
                run.UpdateContext(done, 0);
                if (done) run.TryAutoCollapse(allowed: true, initial: initial);
                if (initial && choices is not null)
                {
                    if (owned.SelectMany(group => group).Any(tool => choices.OpenRuns.Contains(tool.Id)))
                        run.IsExpanded = true;
                    void RestoreEntry(ToolRow entry)
                    {
                        if (choices.OpenEntries.Contains(entry.Tool.Id)) entry.IsExpanded = true;
                        foreach (var child in entry.GroupItems) RestoreEntry(child);
                    }
                    foreach (var entry in run.Entries) RestoreEntry(entry);
                }
                result.Add(new ThinkingToolBlock { Key = first, SourceStart = 0, SourceLength = 0, Row = run });
                seenRuns.Add(first);
                following[run] = 0;
                preceding = run;
                pending.Clear();
                pending.AddRange(later);
            }
        }

        foreach (var block in blocks)
        {
            if (WebToolPresentation.HandleSpecial(block, pending, markups)) continue;
            if (block is MarkupUnitBlock markup && WebToolPresentation.IsTool(markup))
            {
                var unit = markup.Unit;
                if (unit.ToolCallId is { } callId && (!seenCalls.Add(callId)
                    || bodyCalls?.Contains(callId) == true || message.ToolCalls.Any(tool => tool.Id == callId)))
                    continue;
                if (unit.ToolCallId is { } idAcrossSegments && thinkingCalls?.TryGetValue(idAcrossSegments, out var whole) == true)
                {
                    if (_choiceSegment is not null && !ReferenceEquals(_choiceSegment, whole.Owner)) continue;
                    markup = markup with { Unit = unit = whole.Markup.Unit };
                }
                if (message.ServerToolResults.TryGetValue(unit.ToolCallId ?? markup.Key, out var retry))
                    markup = markup with { Unit = unit = retry };
                var id = unit.ToolCallId is { } identity ? $"think:{identity}" : $"think:{markup.SourceStart}";
                var tool = WebToolPresentation.Update(message, markup, _webTools, id, done,
                    !done && unit.PresentationPending);
                markups[id] = markup;
                WebToolPresentation.Add(pending, [tool]);
                continue;
            }
            Flush();
            if (preceding is { NeedsProseSignal: true } && following[preceding] < 240)
                following[preceding] += TranscriptSource.CountProseCharacters([block]);
            result.Add(block);
        }
        Flush();
        foreach (var (run, characters) in following)
        {
            run.UpdateContext(done, characters);
            if (run.NeedsAutoCollapse)
            {
                run.TryAutoCollapse(this.FindAncestorOfType<TranscriptView>()?.CanAutoCollapseTools() ?? true);
                if (run.NeedsAutoCollapse) _toolFoldTimer.Start();
            }
        }
        foreach (var key in _toolRuns.Keys.Where(key => !seenRuns.Contains(key)).ToArray())
        {
            _toolRuns[key].Dispose();
            _toolRuns.Remove(key);
        }
        foreach (var key in _webTools.Keys.Where(key => !markups.ContainsKey(key)).ToArray())
            _webTools.Remove(key);
        return result;
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

            if (block is ThinkingToolBlock toolBlock)
            {
                if (_body.Children[i] is not ToolRunView toolView || !ReferenceEquals(toolView.DataContext, toolBlock.Row))
                    _body.Children[i] = BuildChild(block);
                _rendered[i] = block;
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
        if (block is ThinkingToolBlock toolBlock) return new ToolRunView { DataContext = toolBlock.Row };
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
