using System.Text;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Source-agnostic transcript state machine — pure, no WPF. Folds a stream of
/// normalized <see cref="AgentEvent"/>s into a list of <see cref="AgentBlockDto"/>,
/// regardless of whether the events come from a local CLI process or (later) a
/// remote relay replay. This is the canonical reducer both desktop and phone
/// mirror; the phone re-implements the same algorithm in Kotlin against its own
/// fragment model.
///
/// Design (carried over from the prior WPF reducer, converged across OpenCovibe
/// / Happy):
///   • item-id addressed, never stream-order addressed — out-of-order events
///     still route to the right block (tools by tool-id; the active text /
///     thinking block tracked by list index and reset at turn boundaries).
///   • idempotent — replayed tool-results are ignored, guarded by
///     <see cref="_seenToolResultIds"/>.
///   • batched — text/thinking deltas accumulate in StringBuilders and commit to
///     their bound block on <see cref="Flush"/> (caller drives cadence), so a
///     serializer/observer sees one update per tick instead of one per delta.
///
/// <b>Thread safety.</b> The bridge folds events on a turn thread while a timer
/// thread snapshots <see cref="Blocks"/> for publication — so every public
/// mutator and reader is serialized on <see cref="_lock"/> (a Monitor, which is
/// reentrant, so public methods that call each other are safe). <see cref="Blocks"/>
/// returns an immutable copy, never the live list, so a snapshotter can never
/// hit "Collection was modified" nor read a half-applied state. Blocks are
/// immutable records, so "mutating" a streaming block means replacing it in the
/// list at its tracked index. Indices stay valid because we only ever append
/// (the sole removal is the transient pending placeholder, which is always the
/// tail when cleared — see <see cref="ClearPending"/>).
/// </summary>
public sealed class AgentTranscriptReducer
{
    private readonly List<AgentBlockDto> _blocks = new();
    private readonly object _lock = new();

    // Active streaming block indices for the current turn (-1 = none). Reset on
    // turn boundaries.
    private int _activeTextIndex = -1;
    private int _activeThinkingIndex = -1;
    private readonly StringBuilder _textBuffer = new();
    private readonly StringBuilder _thinkingBuffer = new();

    // Tool blocks routed by tool-id → list index (order-independent).
    private readonly Dictionary<string, int> _toolIndex = new(StringComparer.Ordinal);

    // Idempotency: a tool-result already folded once is ignored on replay.
    private readonly HashSet<string> _seenToolResultIds = new(StringComparer.Ordinal);

    /// <summary>Monotonic counter of folded events — diagnostic; the authoritative
    /// session seq lives in the bridge's <c>AgentEventLog</c>.</summary>
    public long Seq { get; private set; }

    /// <summary>An immutable snapshot of the transcript. Safe to enumerate off the
    /// lock; reflects the state at the moment of the call.</summary>
    public IReadOnlyList<AgentBlockDto> Blocks
    {
        get { lock (_lock) return _blocks.ToList(); }
    }

    /// <summary>Append a user prompt (optimistic local echo). Flushes any pending
    /// stream first so ordering is preserved.</summary>
    public void AddUser(string text)
    {
        lock (_lock)
        {
            Flush();
            ResetActiveBlocks();
            _blocks.Add(AgentBlockDto.User(text));
        }
    }

    /// <summary>Show an immediate placeholder right after the user prompt, before
    /// the backend has produced anything. Always added as the tail; cleared by the
    /// next <see cref="Apply"/> or an explicit <see cref="ClearPending"/>.</summary>
    public void BeginPending(string label)
    {
        lock (_lock)
        {
            ClearPending();
            _blocks.Add(AgentBlockDto.Pending(label));
        }
    }

    /// <summary>Remove the pending placeholder if it is the current tail.</summary>
    public void ClearPending()
    {
        lock (_lock)
        {
            if (_blocks.Count > 0 && _blocks[^1].Kind == AgentBlockKind.Pending)
                _blocks.RemoveAt(_blocks.Count - 1);
        }
    }

    /// <summary>Fold one normalized event into the transcript.</summary>
    public void Apply(AgentEvent ev)
    {
        lock (_lock)
        {
            Seq++;
            ClearPending(); // the first real event replaces the "connecting" placeholder
            switch (ev.Kind)
            {
                case AgentEventKind.ThinkingDelta:
                    EndText();
                    EnsureThinking();
                    _thinkingBuffer.Append(ev.Text ?? string.Empty);
                    break;

                case AgentEventKind.TextDelta:
                    EndThinking();
                    EnsureText();
                    _textBuffer.Append(ev.Text ?? string.Empty);
                    break;

                case AgentEventKind.ToolCall when ev.Tool is not null:
                    ApplyTool(ev.Tool);
                    break;

                case AgentEventKind.PermissionRequest when ev.Permission is not null:
                    Flush();
                    ResetActiveBlocks();
                    _blocks.Add(AgentBlockDto.Permission(
                        ev.Permission.Id, ev.Permission.ToolName,
                        ev.Permission.ArgumentsJson ?? ev.Permission.Description));
                    break;

                case AgentEventKind.TurnComplete:
                    EndTurn();
                    break;

                case AgentEventKind.Error:
                    EndTurn();
                    _blocks.Add(AgentBlockDto.AssistantText("⚠ " + (ev.ErrorMessage ?? "出错了"), streaming: false));
                    break;
            }
        }
    }

    private void ApplyTool(AgentToolEvent tool)
    {
        // A tool-result for a tool we've already completed via the same id is a
        // replay — fold once.
        if (tool.Status is AgentToolStatus.Completed or AgentToolStatus.Failed
            && tool.ResultPreview is not null
            && !_seenToolResultIds.Add(tool.Id + "|result"))
            return;

        Flush();
        EndText();
        EndThinking();

        if (!_toolIndex.TryGetValue(tool.Id, out var idx))
        {
            _blocks.Add(AgentBlockDto.Tool(tool.Id, tool.Name, tool.Status,
                arguments: tool.ArgumentsJson, resultPreview: tool.ResultPreview));
            _toolIndex[tool.Id] = _blocks.Count - 1;
        }
        else
        {
            var existing = _blocks[idx];
            _blocks[idx] = AgentBlockDto.Tool(
                tool.Id,
                !string.IsNullOrEmpty(tool.Name) ? tool.Name : existing.ToolName,
                tool.Status,
                arguments: tool.ArgumentsJson ?? existing.ToolArguments,
                resultPreview: tool.ResultPreview ?? existing.ToolResultPreview);
        }
    }

    /// <summary>Commit buffered text/thinking deltas to their bound blocks. Driven
    /// by the caller on a cadence and once more at turn end.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            CommitText();
            CommitThinking();
        }
    }

    /// <summary>Finalize the current turn: clear the placeholder, flush buffers,
    /// and stop the streaming flag on active blocks. Idempotent.</summary>
    public void EndTurn()
    {
        lock (_lock)
        {
            ClearPending();
            Flush();
            EndText();
            EndThinking();
        }
    }

    private void EnsureText()
    {
        if (_activeTextIndex >= 0) return;
        _blocks.Add(AgentBlockDto.AssistantText(string.Empty, streaming: true));
        _activeTextIndex = _blocks.Count - 1;
    }

    private void EnsureThinking()
    {
        if (_activeThinkingIndex >= 0) return;
        _blocks.Add(AgentBlockDto.Thinking(string.Empty, streaming: true));
        _activeThinkingIndex = _blocks.Count - 1;
    }

    private void EndText()
    {
        CommitText();
        if (_activeTextIndex >= 0)
        {
            _blocks[_activeTextIndex] = _blocks[_activeTextIndex] with { IsStreaming = false };
            _activeTextIndex = -1;
        }
    }

    private void EndThinking()
    {
        CommitThinking();
        if (_activeThinkingIndex >= 0)
        {
            _blocks[_activeThinkingIndex] = _blocks[_activeThinkingIndex] with { IsStreaming = false };
            _activeThinkingIndex = -1;
        }
    }

    private void CommitText()
    {
        if (_textBuffer.Length == 0 || _activeTextIndex < 0) return;
        var b = _blocks[_activeTextIndex];
        _blocks[_activeTextIndex] = b with { Text = (b.Text ?? string.Empty) + _textBuffer.ToString() };
        _textBuffer.Clear();
    }

    private void CommitThinking()
    {
        if (_thinkingBuffer.Length == 0 || _activeThinkingIndex < 0) return;
        var b = _blocks[_activeThinkingIndex];
        _blocks[_activeThinkingIndex] = b with { Text = (b.Text ?? string.Empty) + _thinkingBuffer.ToString() };
        _thinkingBuffer.Clear();
    }

    private void ResetActiveBlocks()
    {
        // Close any active streaming block before abandoning it — e.g. a permission
        // prompt interrupts the assistant mid-stream, so the partial text is final,
        // not still streaming. (Both call sites Flush() first, so no buffered text
        // is lost here.)
        if (_activeTextIndex >= 0)
            _blocks[_activeTextIndex] = _blocks[_activeTextIndex] with { IsStreaming = false };
        if (_activeThinkingIndex >= 0)
            _blocks[_activeThinkingIndex] = _blocks[_activeThinkingIndex] with { IsStreaming = false };
        _activeTextIndex = -1;
        _activeThinkingIndex = -1;
        _textBuffer.Clear();
        _thinkingBuffer.Clear();
    }
}