using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MolaGPT.Presentation;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

/// <summary>
/// 思考与工具调用的统一过程时间线视图：
///
/// <code>
///   💡 正在思考… 5s  ▾
///   │  (思考正文，未展开不解析 Markdown)
///   │  🔍 联网搜索  Pixel 10              ✓
///   │  </> Python 执行                    ✓
/// </code>
///
/// 无底卡、无左侧粗色条，与 Mobile ProcessTimeline 及桌面工具行视觉统一。
/// </summary>
public sealed class ProcessTimelineView : Decorator
{
    private readonly TextBlock _icon;
    private readonly TextBlock _title;
    private readonly ToolChevron _chevron;
    private readonly Button _header;
    private readonly Border _rail;
    private readonly StackPanel _stepsPanel;
    private readonly RevealPresenter _reveal;

    private readonly Dictionary<ProcessTimelineStep, Control> _stepControls = new();
    private readonly Dictionary<ThinkingSegmentViewModel, ThinkingSegmentBody> _thinkingBodies = new();
    private ProcessTimelineRow? _row;
    private bool _subscribed;
    private bool _queued;
    private bool _bodyStale = true;

    public ProcessTimelineView()
    {
        _icon = new TextBlock
        {
            FontSize = 13,
            Width = 16,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon.Classes.Add("icon");

        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        _chevron = new ToolChevron
        {
            ClosedAngle = -90,
            OpenAngle = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.65
        };

        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerRow.Children.Add(_icon);
        headerRow.Children.Add(_title);
        headerRow.Children.Add(_chevron);

        _header = new Button
        {
            Content = headerRow,
            Padding = new Thickness(6, 4),
            Margin = new Thickness(-6, 0, 0, 0),
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        _header.Classes.Add("processheader");
        _header.Click += (_, _) =>
        {
            if (_row is not null) _row.IsExpanded = !_row.IsExpanded;
        };

        _rail = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 4)
        };

        _stepsPanel = new StackPanel { Spacing = 4 };

        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("16,8,*"),
            Margin = new Thickness(0, 2, 0, 6)
        };
        bodyGrid.Children.Add(_rail);
        Grid.SetColumn(_stepsPanel, 2);
        bodyGrid.Children.Add(_stepsPanel);

        _reveal = new RevealPresenter
        {
            Child = bodyGrid,
            IsOpen = false,
            PerformanceLabel = "thinking"
        };

        var root = new StackPanel();
        root.Children.Add(_header);
        root.Children.Add(_reveal);

        Child = root;
        BuildContextMenu();
        DataContextChanged += (_, _) => AttachRow();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        AttachRow();
        ApplyBrushes();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        ClearSteps();
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void BuildContextMenu()
    {
        var copy = new MenuItem { Header = "复制思考内容" };
        copy.Click += async (_, _) =>
        {
            var text = _row?.CopyableThinkingText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Opening += (_, _) => copy.IsEnabled = !string.IsNullOrWhiteSpace(_row?.CopyableThinkingText());
        ContextMenu = menu;
    }

    private void AttachRow()
    {
        var next = DataContext as ProcessTimelineRow;
        if (!ReferenceEquals(_row, next))
        {
            Unsubscribe();
            ClearSteps();
            _row = next;
        }
        if (_row is not null && !_subscribed && this.IsAttachedToVisualTree())
        {
            _row.PropertyChanged += OnRowChanged;
            _row.Steps.CollectionChanged += OnStepsChanged;
            _subscribed = true;
        }
        Refresh(immediate: true);
    }

    private void Unsubscribe()
    {
        if (_row is null || !_subscribed) return;
        _row.PropertyChanged -= OnRowChanged;
        _row.Steps.CollectionChanged -= OnStepsChanged;
        _subscribed = false;
    }

    private void ClearSteps()
    {
        foreach (var body in _thinkingBodies.Values) body.Dispose();
        _thinkingBodies.Clear();
        _stepControls.Clear();
        _stepsPanel.Children.Clear();
        _bodyStale = true;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProcessTimelineRow.IsExpanded))
        {
            if (_row?.IsExpanded == true) _bodyStale = true;
        }
        QueueRefresh();
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _bodyStale = true;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (this.IsAttachedToVisualTree()) Refresh(immediate: false);
        }, DispatcherPriority.Render);
    }

    private void Refresh(bool immediate)
    {
        if (_row is null) return;

        _icon.Text = _row.IconGlyph;
        _title.Text = _row.Title;
        _chevron.IsExpanded = _row.IsExpanded;
        Shimmer.SetIsActive(_title, _row.IsActive);
        ApplyHeaderBrushes();

        if (_row.IsExpanded)
        {
            if (_bodyStale || immediate)
            {
                _bodyStale = false;
                SyncSteps();
            }
        }
        _reveal.IsOpen = _row.IsExpanded;
    }

    private void SyncSteps()
    {
        if (_row is null || !this.IsAttachedToVisualTree()) return;

        var current = new HashSet<ProcessTimelineStep>(_row.Steps);
        foreach (var gone in _stepControls.Keys.Where(step => !current.Contains(step)).ToArray())
        {
            _stepsPanel.Children.Remove(_stepControls[gone]);
            _stepControls.Remove(gone);
            if (gone is ProcessThinkingStep thinking && _thinkingBodies.Remove(thinking.Segment, out var body))
                body.Dispose();
        }

        for (var i = 0; i < _row.Steps.Count; i++)
        {
            var step = _row.Steps[i];
            if (!_stepControls.TryGetValue(step, out var control))
            {
                control = BuildStepControl(step);
                _stepControls[step] = control;
                _stepsPanel.Children.Insert(i, control);
            }
            else
            {
                var idx = _stepsPanel.Children.IndexOf(control);
                if (idx != i) _stepsPanel.Children.Move(idx, i);
                if (step is ProcessThinkingStep thinking && _thinkingBodies.TryGetValue(thinking.Segment, out var body))
                    body.Sync();
            }
        }

        UpdateEmbeddedSummary();
    }

    private Control BuildStepControl(ProcessTimelineStep step) => step switch
    {
        ProcessThinkingStep thinking => CreateThinkingBody(thinking.Segment),
        ProcessToolStep tool => new ContentControl
        {
            Content = tool.Row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        },
        _ => new Border()
    };

    private Control CreateThinkingBody(ThinkingSegmentViewModel segment)
    {
        if (_thinkingBodies.TryGetValue(segment, out var existing))
        {
            existing.Sync();
            return existing.Panel;
        }
        var body = new ThinkingSegmentBody(this, _row!, segment, UpdateEmbeddedSummary);
        _thinkingBodies[segment] = body;
        body.Sync();
        return body.Panel;
    }

    private void UpdateEmbeddedSummary()
    {
        if (_row is null) return;
        var hasEmbedded = false;
        ToolRow? activeTool = null;
        foreach (var body in _thinkingBodies.Values)
        {
            if (body.HasEmbeddedTools) hasEmbedded = true;
            if (body.ActiveEmbeddedTool is { IsShimmering: true } running) activeTool = running;
        }
        _row.UpdateEmbeddedTools(hasEmbedded, activeTool);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyBrushes();

    private void ApplyBrushes()
    {
        if (this.TryFindResource("Color.Border", ActualThemeVariant, out var borderVal) && borderVal is Color borderColor)
        {
            _rail.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(borderColor.A * 0.85), borderColor.R, borderColor.G, borderColor.B), 0),
                    new GradientStop(Color.FromArgb((byte)(borderColor.A * 0.7), borderColor.R, borderColor.G, borderColor.B), 0.85),
                    new GradientStop(Color.FromArgb(0, borderColor.R, borderColor.G, borderColor.B), 1)
                }
            };
        }
        else if (TryBrush("Brush.Border") is { } fallbackRail)
        {
            _rail.Background = fallbackRail;
        }
        ApplyHeaderBrushes();
    }

    private void ApplyHeaderBrushes()
    {
        var secondary = TryBrush("Brush.Text.Secondary");
        var muted = TryBrush("Brush.Text.Muted");
        var primary = TryBrush("Brush.Primary");

        _title.Foreground = _row?.IsStopped == true ? muted ?? secondary : secondary;
        _chevron.Foreground = secondary;
        _icon.Foreground = _row switch
        {
            { IsActive: true } => primary ?? secondary,
            { IsStopped: true } => muted ?? secondary,
            _ => secondary
        };
    }

    private IBrush? TryBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    /// <summary>
    /// 单段思考的增量 Markdown 渲染器。若官方链路在思考段里内嵌了工具标签（data-tool-call），
    /// 直接在此扁平化为同一时间线里的 ToolRow，不再嵌套双层折叠组。
    /// </summary>
    private sealed class ThinkingSegmentBody : IDisposable
    {
        private static readonly ConditionalWeakTable<ThinkingSegmentViewModel, HashSet<string>> s_openEmbeddedTools = new();

        private sealed record EmbeddedToolBlock : RenderBlock
        {
            public required ToolRow Row { get; init; }
        }

        private readonly ProcessTimelineView _owner;
        private readonly ProcessTimelineRow _timeline;
        private readonly ThinkingSegmentViewModel _segment;
        private readonly Action _onEmbeddedChanged;
        private readonly List<RenderBlock> _rendered = new();
        private readonly Dictionary<string, ToolCallViewModel> _webTools = new();
        private readonly Dictionary<string, ToolRow> _embeddedToolRows = new();
        private RenderDocument? _document;
        private bool _hasEmbeddedCalls;
        private bool _syncQueued;
        private bool _disposed;

        public StackPanel Panel { get; } = new() { Spacing = 2, Classes = { "thinkingbody" } };
        public bool HasEmbeddedTools => _embeddedToolRows.Count > 0;
        public ToolRow? ActiveEmbeddedTool =>
            _embeddedToolRows.Values.LastOrDefault(row => row.IsShimmering);

        public ThinkingSegmentBody(
            ProcessTimelineView owner,
            ProcessTimelineRow timeline,
            ThinkingSegmentViewModel segment,
            Action onEmbeddedChanged)
        {
            _owner = owner;
            _timeline = timeline;
            _segment = segment;
            _onEmbeddedChanged = onEmbeddedChanged;
            _segment.PropertyChanged += OnSegmentChanged;
            _timeline.Message.PropertyChanged += OnMessageChanged;
        }

        private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed || e.PropertyName != nameof(ThinkingSegmentViewModel.Source)) return;
            QueueSync();
        }

        private void OnMessageChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed || !_hasEmbeddedCalls) return;
            if (e.PropertyName is nameof(MessageViewModel.HasToolCalls)
                or nameof(MessageViewModel.ThinkingSegments)
                or nameof(MessageViewModel.Content)
                or nameof(MessageViewModel.IsStreaming)
                or nameof(MessageViewModel.IsRevealing))
            {
                QueueSync();
            }
        }

        private void QueueSync()
        {
            if (_syncQueued || _disposed) return;
            _syncQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _syncQueued = false;
                if (!_disposed && _owner.IsAttachedToVisualTree() && _timeline.IsExpanded)
                    Sync();
            }, DispatcherPriority.Render);
        }

        public void Sync()
        {
            if (_disposed || !_owner.IsAttachedToVisualTree()) return;
            var source = _segment.Source;
            if (string.IsNullOrWhiteSpace(source))
            {
                ClearBlocks();
                _onEmbeddedChanged();
                return;
            }

            _document = MessageDocumentParser.ParseIncremental(_document, source);
            _hasEmbeddedCalls = _document.Blocks.OfType<MarkupUnitBlock>()
                .Any(block => block.Unit.ToolCallId is not null);
            var next = ResolveEmbeddedTools(_document.Blocks);
            SyncChildren(next);
            _onEmbeddedChanged();
        }

        private IReadOnlyList<RenderBlock> ResolveEmbeddedTools(IReadOnlyList<RenderBlock> blocks)
        {
            var message = _timeline.Message;
            SaveExpandedChoices();
            var openIds = s_openEmbeddedTools.GetOrCreateValue(_segment);

            var result = new List<RenderBlock>(blocks.Count);
            var pending = new List<IReadOnlyList<ToolCallViewModel>>();
            var markups = new Dictionary<string, MarkupUnitBlock>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenCalls = new HashSet<string>(StringComparer.Ordinal);
            var bodyCalls = message.Content.Contains("data-tool-call", StringComparison.OrdinalIgnoreCase)
                ? MolaGptMarkupSplitter.Split(message.Content)
                    .Where(unit => unit.ToolCallId is not null)
                    .Select(unit => unit.ToolCallId!)
                    .ToHashSet(StringComparer.Ordinal)
                : null;
            var thinkingCalls = message.ThinkingSegments.Count > 1 ? WebToolPresentation.ThinkingTools(message) : null;
            var done = !message.IsStreaming && !message.IsRevealing;

            void FlushPending()
            {
                foreach (var group in pending)
                {
                    if (group.Count == 0) continue;
                    var firstId = group[0].Id;
                    var initial = !_embeddedToolRows.TryGetValue(firstId, out var row);
                    if (initial)
                    {
                        row = new ToolRow(message, group[0], result.Count)
                        {
                            AnimateEntrance = !done,
                            OnUserExpanded = _timeline.KeepOpen
                        };
                        row.PropertyChanged += OnEmbeddedToolChanged;
                        _embeddedToolRows[firstId] = row;
                    }
                    row!.SyncTools(group);
                    row.SetMarkups(markups);
                    if (initial && openIds.Contains(firstId))
                        row.IsExpanded = true;
                    seenIds.Add(firstId);
                    result.Add(new EmbeddedToolBlock { Key = firstId, SourceStart = 0, SourceLength = 0, Row = row });
                }
                pending.Clear();
            }

            foreach (var block in blocks)
            {
                if (WebToolPresentation.HandleSpecial(block, pending, markups)) continue;
                if (block is MarkupUnitBlock markup && WebToolPresentation.IsTool(markup))
                {
                    var unit = markup.Unit;
                    if (unit.ToolCallId is { } callId && (!seenCalls.Add(callId)
                        || bodyCalls?.Contains(callId) == true
                        || message.ToolCalls.Any(tool => tool.Id == callId)))
                        continue;
                    if (unit.ToolCallId is { } acrossId && thinkingCalls?.TryGetValue(acrossId, out var whole) == true)
                    {
                        if (!ReferenceEquals(_segment, whole.Owner)) continue;
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
                FlushPending();
                result.Add(block);
            }
            FlushPending();

            foreach (var gone in _embeddedToolRows.Keys.Where(id => !seenIds.Contains(id)).ToArray())
            {
                _embeddedToolRows[gone].PropertyChanged -= OnEmbeddedToolChanged;
                _embeddedToolRows[gone].Dispose();
                _embeddedToolRows.Remove(gone);
            }
            foreach (var gone in _webTools.Keys.Where(id => !markups.ContainsKey(id)).ToArray())
                _webTools.Remove(gone);

            return result;
        }

        private void OnEmbeddedToolChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            if (e.PropertyName is nameof(ToolRow.IsShimmering) or nameof(ToolRow.DisplayLabel) or nameof(ToolRow.IconGlyph))
                _onEmbeddedChanged();
        }

        private void SyncChildren(IReadOnlyList<RenderBlock> next)
        {
            for (var i = 0; i < next.Count; i++)
            {
                var block = next[i];
                if (i >= _rendered.Count)
                {
                    Panel.Children.Add(BuildChild(block));
                    _rendered.Add(block);
                    continue;
                }
                if (block is EmbeddedToolBlock toolBlock)
                {
                    if (Panel.Children[i] is not ContentControl cc || !ReferenceEquals(cc.Content, toolBlock.Row))
                        Panel.Children[i] = BuildChild(block);
                    _rendered[i] = block;
                    continue;
                }
                if (string.Equals(_rendered[i].Key, block.Key, StringComparison.Ordinal)) continue;

                if (_rendered[i].GetType() == block.GetType())
                    Panel.Children[i].DataContext = block;
                else
                    Panel.Children[i] = BuildChild(block);

                _rendered[i] = block;
            }

            while (_rendered.Count > next.Count)
            {
                _rendered.RemoveAt(_rendered.Count - 1);
                Panel.Children.RemoveAt(Panel.Children.Count - 1);
            }
        }

        private Control BuildChild(RenderBlock block)
        {
            if (block is EmbeddedToolBlock toolBlock)
            {
                return new ContentControl
                {
                    Content = toolBlock.Row,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
            }
            if (_owner.FindDataTemplate(block)?.Build(block) is { } templated)
            {
                templated.DataContext = block;
                return templated;
            }
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
            _segment.Source is { } source
            && block.SourceStart >= 0
            && block.SourceStart + block.SourceLength <= source.Length
                ? source.Substring(block.SourceStart, block.SourceLength)
                : string.Empty;

        private void SaveExpandedChoices()
        {
            if (_embeddedToolRows.Count == 0) return;
            var openIds = s_openEmbeddedTools.GetOrCreateValue(_segment);
            openIds.Clear();
            foreach (var (id, row) in _embeddedToolRows)
                if (row.IsExpanded) openIds.Add(id);
        }

        private void ClearBlocks()
        {
            SaveExpandedChoices();
            foreach (var row in _embeddedToolRows.Values)
            {
                row.PropertyChanged -= OnEmbeddedToolChanged;
                row.Dispose();
            }
            _embeddedToolRows.Clear();
            _webTools.Clear();
            _document = null;
            _hasEmbeddedCalls = false;
            _rendered.Clear();
            Panel.Children.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _segment.PropertyChanged -= OnSegmentChanged;
            _timeline.Message.PropertyChanged -= OnMessageChanged;
            ClearBlocks();
        }
    }
}
