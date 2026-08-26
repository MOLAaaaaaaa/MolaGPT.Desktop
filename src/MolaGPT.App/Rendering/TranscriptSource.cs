using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using MolaGPT.Presentation;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Flattens <see cref="ChatViewModel.Messages"/> into the row list the
/// transcript actually virtualizes, and keeps it in sync incrementally.
///
/// Three properties this has to hold, in order of how badly they hurt when lost:
///
///  1. A delta must not rebuild the world. Each message owns a contiguous run of
///     rows; a change re-flattens that message only, and the splice below keeps
///     the unchanged prefix, so a streaming answer touches one or two rows per
///     tick instead of the whole transcript.
///  2. Keys must be stable. A row whose key is unchanged keeps its realized
///     container, which is what stops the viewport flickering while text streams
///     in underneath the user's cursor.
///  3. Rebuilds must coalesce. Deltas arrive faster than frames; without the
///     dirty set below, a fast stream schedules more re-flattens than the UI
///     thread can retire and the window stops responding — which is exactly the
///     failure mode this migration exists to remove.
/// </summary>
public sealed class TranscriptSource : ObservableCollection<TranscriptRow>, IDisposable
{
    private sealed class Segment
    {
        public int Start;
        public List<TranscriptRow> Rows = new();
        /// <summary>Last parse per text block index, so streaming can re-parse
        /// only the tail rather than the whole body.</summary>
        public readonly Dictionary<int, RenderDocument> Documents = new();
    }

    private readonly ChatViewModel _chat;
    private readonly Dictionary<MessageViewModel, Segment> _segments = new();
    private readonly List<MessageViewModel> _order = new();
    private readonly HashSet<MessageViewModel> _dirty = new();
    private bool _flushQueued;
    private bool _disposed;

    public TranscriptSource(ChatViewModel chat)
    {
        _chat = chat;
        _chat.Messages.CollectionChanged += OnMessagesChanged;
        Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.Messages.CollectionChanged -= OnMessagesChanged;
        foreach (var message in _order) Unsubscribe(message);
        _order.Clear();
        _segments.Clear();
        Clear();
    }

    // ---- collection wiring -------------------------------------------------

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Loading a conversation replaces the whole list; anything finer-grained
        // is an append during streaming or a removal on retry.
        if (e.Action is NotifyCollectionChangedAction.Reset
            || e.OldItems is { Count: > 0 } && e.NewItems is { Count: > 0 })
        {
            Reset();
            return;
        }

        if (e.OldItems is { Count: > 0 })
        {
            foreach (MessageViewModel message in e.OldItems) RemoveMessage(message);
        }

        if (e.NewItems is { Count: > 0 })
        {
            var index = e.NewStartingIndex;
            foreach (MessageViewModel message in e.NewItems)
                InsertMessage(message, index++);
        }
    }

    /// <summary>
    /// Rebuild every row for a wholesale change — opening a conversation, or a
    /// retry that replaces the tail.
    ///
    /// The rows are built into <see cref="Collection{T}.Items"/> and published
    /// with a single Reset. Going through <see cref="InsertMessage"/> per
    /// message (and therefore <c>Insert</c> per row) raised one
    /// CollectionChanged for every row in the transcript — around 300 for an
    /// ordinary conversation — and the list re-ran container bookkeeping and
    /// invalidated layout on each one. Incremental paths (<see cref="InsertMessage"/>,
    /// <see cref="Splice"/>) are untouched: their whole point is to move as few
    /// rows as possible while text is streaming in.
    /// </summary>
    private void Reset()
    {
        foreach (var message in _order) Unsubscribe(message);
        _order.Clear();
        _segments.Clear();
        _dirty.Clear();

        CheckReentrancy();
        Items.Clear();

        foreach (var message in _chat.Messages)
        {
            var segment = new Segment { Start = Items.Count };
            _order.Add(message);
            _segments[message] = segment;
            Subscribe(message);

            segment.Rows = Build(message, segment);
            foreach (var row in segment.Rows) Items.Add(row);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void InsertMessage(MessageViewModel message, int index)
    {
        if (_segments.ContainsKey(message)) return;

        index = Math.Clamp(index, 0, _order.Count);
        var segment = new Segment { Start = index == 0 ? 0 : SegmentEnd(_order[index - 1]) };

        _order.Insert(index, message);
        _segments[message] = segment;
        Subscribe(message);

        var rows = Build(message, segment);
        for (var i = 0; i < rows.Count; i++)
            Insert(segment.Start + i, rows[i]);
        segment.Rows = rows;
        ShiftFrom(index + 1, rows.Count);
    }

    private void RemoveMessage(MessageViewModel message)
    {
        if (!_segments.TryGetValue(message, out var segment)) return;

        var index = _order.IndexOf(message);
        for (var i = segment.Rows.Count - 1; i >= 0; i--)
            RemoveAt(segment.Start + i);

        _order.RemoveAt(index);
        _segments.Remove(message);
        _dirty.Remove(message);
        Unsubscribe(message);
        ShiftFrom(index, -segment.Rows.Count);
    }

    private int SegmentEnd(MessageViewModel message) =>
        _segments.TryGetValue(message, out var s) ? s.Start + s.Rows.Count : 0;

    private void ShiftFrom(int orderIndex, int delta)
    {
        if (delta == 0) return;
        for (var i = orderIndex; i < _order.Count; i++)
            _segments[_order[i]].Start += delta;
    }

    // ---- change subscriptions ---------------------------------------------

    private void Subscribe(MessageViewModel message)
    {
        message.PropertyChanged += OnMessagePropertyChanged;
        message.DisplayBlocks.CollectionChanged += OnDisplayBlocksChanged;
    }

    private void Unsubscribe(MessageViewModel message)
    {
        message.PropertyChanged -= OnMessagePropertyChanged;
        message.DisplayBlocks.CollectionChanged -= OnDisplayBlocksChanged;
    }

    private static readonly string[] RebuildTriggers =
    {
        nameof(MessageViewModel.Content),
        nameof(MessageViewModel.IsStreaming),
        nameof(MessageViewModel.IsPending),
        nameof(MessageViewModel.IsLatestAssistant),
        nameof(MessageViewModel.ModelLabel),
        nameof(MessageViewModel.WasStopped)
    };

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MessageViewModel message) return;
        if (e.PropertyName is not { } name || Array.IndexOf(RebuildTriggers, name) < 0) return;
        MarkDirty(message);
    }

    private void OnDisplayBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ObservableCollection does not name its owner, so find the message whose
        // block list this is. The transcript is short enough that a scan is
        // cheaper than keeping a second dictionary in sync.
        foreach (var message in _order)
        {
            if (ReferenceEquals(message.DisplayBlocks, sender))
            {
                MarkDirty(message);
                return;
            }
        }
    }

    private void MarkDirty(MessageViewModel message)
    {
        if (_disposed || !_segments.ContainsKey(message)) return;
        _dirty.Add(message);
        if (_flushQueued) return;

        _flushQueued = true;
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    private void Flush()
    {
        _flushQueued = false;
        if (_disposed || _dirty.Count == 0) return;

        // Snapshot: rebuilding raises collection events, and a handler could in
        // principle mark something else dirty.
        var pending = _dirty.ToArray();
        _dirty.Clear();

        foreach (var message in pending)
        {
            if (_segments.TryGetValue(message, out var segment))
                Splice(message, segment);
        }
    }

    // ---- flatten + splice --------------------------------------------------

    private List<TranscriptRow> Build(MessageViewModel message, Segment segment)
    {
        // A user turn is one row: bubble plus the avatar beside it, exactly as
        // MessageItemView lays it out. Splitting it would put the avatar on its
        // own line.
        if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            segment.Documents.TryGetValue(0, out var userPrevious);
            var userDocument = MessageDocumentParser.ParseIncremental(userPrevious, message.VisibleContent);
            segment.Documents[0] = userDocument;
            return new List<TranscriptRow> { new UserMessageRow(message, userDocument.Blocks) };
        }

        var rows = new List<TranscriptRow> { new HeaderRow(message) };

        if (message.IsPending)
            rows.Add(new PendingRow(message));

        for (var i = 0; i < message.DisplayBlocks.Count; i++)
        {
            var block = message.DisplayBlocks[i];

            if (block.IsText)
            {
                segment.Documents.TryGetValue(i, out var previous);
                var document = MessageDocumentParser.ParseIncremental(previous, block.Text);
                segment.Documents[i] = document;

                foreach (var renderBlock in document.Blocks)
                    rows.Add(new ProseRow(message, renderBlock, i));
            }
            else if (block.Tool is { } tool)
            {
                rows.Add(new ToolRow(message, tool, i));
            }
            else if (block.ToolGroup is { } group)
            {
                rows.Add(new ToolGroupRow(message, group, i));
            }
            else if (block.Thinking is { } thinking)
            {
                rows.Add(new ThinkingRow(message, thinking, i));
            }
        }

        // Drop parses for text blocks that no longer exist, so a retry that
        // shortens the answer cannot resurrect stale rows through the cache.
        if (segment.Documents.Count > message.DisplayBlocks.Count)
        {
            foreach (var key in segment.Documents.Keys.Where(k => k >= message.DisplayBlocks.Count).ToList())
                segment.Documents.Remove(key);
        }

        // Both flags are the view model's own rules, the same ones the WPF
        // template bound its Visibility to — the shells must not each invent
        // their own reading of "was this stopped" or "is this finished".
        if (message.ShowStoppedNotice)
            rows.Add(new StoppedRow(message));

        if (message.HasActions)
            rows.Add(new ActionRow(message));

        return rows;
    }

    /// <summary>
    /// Replaces a message's rows with the minimum number of collection events.
    ///
    /// The shared prefix is kept by reference — those containers are already
    /// realized and on screen. Only the tail, which is where a streaming delta
    /// lands, is torn down and rebuilt.
    /// </summary>
    private void Splice(MessageViewModel message, Segment segment)
    {
        var next = Build(message, segment);
        var previous = segment.Rows;

        var shared = 0;
        var max = Math.Min(previous.Count, next.Count);
        while (shared < max
               && string.Equals(previous[shared].Key, next[shared].Key, StringComparison.Ordinal))
        {
            shared++;
        }

        // Carry the already-realized rows forward so their containers survive.
        for (var i = 0; i < shared; i++)
            next[i] = previous[i];

        for (var i = previous.Count - 1; i >= shared; i--)
            RemoveAt(segment.Start + i);

        for (var i = shared; i < next.Count; i++)
            Insert(segment.Start + i, next[i]);

        var delta = next.Count - previous.Count;
        segment.Rows = next;
        ShiftFrom(_order.IndexOf(message) + 1, delta);
    }
}
