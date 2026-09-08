using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;

namespace MolaGPT.ViewModels;

/// <summary>A one-tap recovery offered on a failed turn's error banner.</summary>
public enum MessageErrorAction
{
    None = 0,
    /// <summary>Balance/model failure (e.g. HTTP 402) — open the model selector.</summary>
    SwitchModel
}

/// <summary>
/// One row in the chat scroll viewer. Content is the raw markdown source;
/// MarkdownPresenter re-renders it on every chunk (throttled).
///
/// Thinking lifecycle (drives the ThinkBlock UI):
/// <list type="number">
///   <item><see cref="AppendThinking"/> first call → record
///         <see cref="_thinkingStartedAt"/>, mark <see cref="IsThinkingActive"/>=true,
///         start a 100ms timer so <see cref="ThinkingElapsedSeconds"/> ticks live.</item>
///   <item>The first visible answer/tool delta after thinking freezes the
///         elapsed counter and closes the current thinking segment.</item>
///   <item><see cref="StopThinking"/> is also called by FinalizeAssistantMessage
///         to handle the edge case where reasoning fired but no normal
///         content arrived (e.g. cancelled mid-thought).</item>
/// </list>
///
/// We use <see cref="System.Threading.Timer"/> + a captured
/// <see cref="SynchronizationContext"/> to keep this VM platform-agnostic
/// (the ViewModels project deliberately carries no UI framework reference, so
/// it cannot reach for a dispatcher timer).
/// </summary>
public sealed partial class MessageViewModel : ObservableObject, IDisposable
{

    [GeneratedRegex("<DSanalysis\\b(?=[^>]*\\bdata-tool-type\\s*=\\s*['\"]image-gen['\"])[^>]*>[\\s\\S]*?</DSanalysis>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageGenDsAnalysisRegex();

    [GeneratedRegex("<DSanalysis\\b(?=[^>]*\\bdata-tool-type\\s*=\\s*['\"](?!(?:python|mcp|image-action)['\"])[^'\"]+['\"])[^>]*>[\\s\\S]*?</DSanalysis>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiddenDsAnalysisRegex();

    [GeneratedRegex("<DSanalysis\\b[^>]*>\\s*</DSanalysis>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmptyDsAnalysisRegex();

    [GeneratedRegex("<!--[\\s\\S]*?-->", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex("✝[^✝]*✝")]
    private static partial Regex DaggerWrappedTokenRegex();

    [GeneratedRegex("<ref\\b(?<attrs>[^>]*)>(?<inner>[\\s\\S]*?)</ref>|<ref\\b(?<attrs2>[^>]*)/?>", RegexOptions.IgnoreCase)]
    private static partial Regex RefTagRegex();

    [GeneratedRegex("\\bsource\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s/>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex RefSourceRegex();

    [GeneratedRegex("""<blockquote\b[^>]*\bclass\s*=\s*(?:"[^"]*\btool-status\b[^"]*"|'[^']*\btool-status\b[^']*')[^>]*>|<DSanalysis\b[^>]*>|<steel-step\b[^>]*>""", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StreamedMarkupOpenRegex();

    [GeneratedRegex("""<div\b[^>]*\bclass\s*=\s*(?:"[^"]*\bai-image-(?:pending-skeleton|error-card)\b[^"]*"|'[^']*\bai-image-(?:pending-skeleton|error-card)\b[^']*')[^>]*>""", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StreamedImageMarkupRegex();

    [ObservableProperty] private string _role;
    [ObservableProperty] private string _content;
    [ObservableProperty] private string? _messageId;
    [ObservableProperty] private string? _thinking;
    [ObservableProperty] private DateTimeOffset _timestamp;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _modelLabel;
    [ObservableProperty] private string? _providerLabel;
    [ObservableProperty] private Usage? _usage;
    [ObservableProperty] private IReadOnlyList<SourceReference>? _sources;
    [ObservableProperty] private IReadOnlyList<AttachmentChip>? _attachments;
    [ObservableProperty] private string? _contentPartsJson;
    [ObservableProperty] private IReadOnlyList<MessageAttempt>? _retryAttempts;
    [ObservableProperty] private int _retryCurrentIndex;
    [ObservableProperty] private bool _isLatestAssistant;
    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private bool _isRoutesPending;
    [ObservableProperty] private bool _autoCollapseThinkingOnComplete = true;

    /// <summary>
    /// The user stopped this turn. Persisted so the bubble still explains itself
    /// after a reload, and so a turn that was stopped before producing anything
    /// keeps its action bar instead of collapsing to a blank gap.
    /// </summary>
    [ObservableProperty] private bool _wasStopped;
    [ObservableProperty] private string _pendingLabel = "回复处理中";
    [ObservableProperty] private string? _pendingDetail;
    public ObservableCollection<ToolCallViewModel> ToolCalls { get; } = new();
    public ObservableCollection<ThinkingSegmentViewModel> ThinkingSegments { get; } = new();
    public ObservableCollection<MessageDisplayBlockViewModel> DisplayBlocks { get; } = new();

    /// <summary>True while the model is still emitting reasoning chunks.
    /// Drives the pulsing dot + "思考中…" header in ThinkBlock.</summary>
    [ObservableProperty] private bool _isThinkingActive;

    /// <summary>Wall-clock seconds since reasoning started.</summary>
    [ObservableProperty] private double _thinkingElapsedSeconds;

    /// <summary>When a turn fails with a recoverable cause, the view shows a small
    /// banner with a one-tap fix (e.g. switch model) instead of leaving the user
    /// to hunt for the control. <see cref="MessageErrorAction.None"/> hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    [NotifyPropertyChangedFor(nameof(ActionErrorButtonLabel))]
    private MessageErrorAction _errorAction = MessageErrorAction.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    private string? _actionErrorText;

    public bool HasActionError => ErrorAction != MessageErrorAction.None
                                  && !string.IsNullOrWhiteSpace(ActionErrorText);

    public string ActionErrorButtonLabel => ErrorAction switch
    {
        MessageErrorAction.SwitchModel => "换个模型",
        _ => string.Empty
    };

    /// <summary>Attach a recoverable-error banner to this (assistant) message.</summary>
    public void SetActionableError(MessageErrorAction action, string text)
    {
        ActionErrorText = text;
        ErrorAction = action;
    }

    /// <summary>Context size when the agent summarized its own history during this
    /// turn; 0 when it did not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompaction))]
    [NotifyPropertyChangedFor(nameof(CompactionText))]
    private int _compactionTokensBefore;

    /// <summary>
    /// Why the cut happened — <c>manual</c> when the user pressed the button in the
    /// gauge, otherwise whatever the agent called its own trigger.
    ///
    /// Persisted beside the size because it cannot be re-derived afterwards, and
    /// "did I do this or did it happen to me" is the first question the marker has
    /// to answer. Null on rows written before this was recorded, which is why the
    /// text below still has an unqualified form.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactionText))]
    private string? _compactionReason;

    /// <summary>
    /// What the history weighs now that it is a summary — the agent's own estimate,
    /// from counting characters rather than asking the model. 0 when unreported.
    ///
    /// Rendered with a 约 for exactly that reason: the next turn's real reading will
    /// not match it, and a number presented as measured would make that difference
    /// look like something going wrong.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactionText))]
    private int _compactionTokensAfter;

    /// <summary>
    /// How full the context was when this turn ended, and against which window.
    ///
    /// Stored per message rather than only on the gauge because the gauge is
    /// rebuilt from scratch on every conversation load — without this, reopening a
    /// conversation showed no reading at all until the user sent another turn.
    /// Deliberately not derived from <see cref="Usage"/>: that is the turn's total
    /// across every model call in the agent loop, which for a multi-call turn is
    /// several times the context size.
    /// </summary>
    [ObservableProperty] private int _contextTokens;

    [ObservableProperty] private int _contextWindow;

    public bool HasCompaction => CompactionTokensBefore > 0;

    /// <summary>The agent's own word for a user-requested compaction.</summary>
    public const string ManualCompactionReason = "manual";

    public string CompactionText => HasCompaction
        ? $"{CompactionKindText} · {CompactionSizeText}"
        : string.Empty;

    private string CompactionKindText => CompactionReason switch
    {
        null or "" => "上下文已压缩",
        ManualCompactionReason => "上下文已手动压缩",
        _ => "上下文已自动压缩"
    };

    /// <summary>
    /// The before, the after and the difference — "how much did that actually buy
    /// me" is the only question this marker gets asked twice.
    ///
    /// Falls back to the before-size alone when the agent reported no estimate,
    /// rather than filling the gap with a computed one. A savings figure derived
    /// from a number nobody gave us would be the least trustworthy thing on screen.
    /// </summary>
    private string CompactionSizeText
    {
        get
        {
            if (CompactionTokensAfter <= 0 || CompactionTokensAfter >= CompactionTokensBefore)
                return $"压缩前 {FormatTokens(CompactionTokensBefore)}";

            var saved = CompactionTokensBefore - CompactionTokensAfter;
            return $"{FormatTokens(CompactionTokensBefore)} → 约 {FormatTokens(CompactionTokensAfter)}"
                   + $"，省下 {FormatTokens(saved)}";
        }
    }

    /// <summary>
    /// Record that history was summarized away at this point in the conversation.
    ///
    /// Kept on the message on purpose: the fact is <em>positional</em> — it marks
    /// which point in the conversation the model can no longer see past — and a
    /// banner, which is by definition transient and placeless, cannot carry that.
    /// </summary>
    public void NoteCompaction(int tokensBefore, int tokensAfter = 0, string? reason = null)
    {
        if (tokensBefore <= 0) return;
        CompactionTokensBefore = tokensBefore;
        if (tokensAfter > 0) CompactionTokensAfter = tokensAfter;
        if (!string.IsNullOrWhiteSpace(reason)) CompactionReason = reason;
    }

    private static string FormatTokens(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
        >= 1_000 => $"{tokens / 1_000d:0}K",
        _ => tokens.ToString()
    };

    public bool HasThinking => !string.IsNullOrEmpty(Thinking);

    /// <summary>
    /// Whether the action bar (version switcher / retry / copy / stats) shows.
    /// Deliberately independent of whether the turn produced any text: an empty
    /// finished turn is exactly the one that needs a retry button, and because the
    /// version switcher lives inside this bar, hiding it on an empty attempt also
    /// strands the user with no way back to a non-empty one.
    /// </summary>
    public bool HasActions => Role == "assistant" && !IsStreaming && !IsPending;
    /// <summary>Shows the "已停止" marker. Only when the turn produced nothing —
    /// with partial content the text itself already shows where it stopped, and a
    /// banner would just be noise.</summary>
    public bool ShowStoppedNotice =>
        WasStopped && !IsStreaming && string.IsNullOrWhiteSpace(Content) && !HasToolCalls;

    public bool HasResponseStats => Usage is not null || !string.IsNullOrWhiteSpace(ModelLabel);
    public bool HasAttachments => Attachments is { Count: > 0 };
    public bool HasToolCalls => ToolCalls.Count > 0;
    public string VisibleContent => ProcessCitationRefs(StripSystemHints(Content));
    public bool HasRetryBar => IsLatestAssistant && RetryAttempts is { Count: > 1 };
    public string RetryCounter => HasRetryBar ? $"{RetryCurrentIndex + 1}/{RetryAttempts!.Count}" : string.Empty;

    /// <summary>请求发出到首个输出的秒数；未打点时为空。</summary>
    public double? FirstTokenSeconds =>
        _requestStartedAt is { } start && _firstOutputAt is { } first
            ? Math.Max(0, (first - start).TotalSeconds)
            : null;

    /// <summary>首个输出到上游结束的生成秒数，平均速度的分母；未完成时为空。</summary>
    public double? GenerationSeconds =>
        _firstOutputAt is { } first && _streamCompletedAt is { } end
            ? Math.Max(0, (end - first).TotalSeconds)
            : null;

    /// <summary>输出 tokens 除以生成耗时。Usage、时序或耗时任一缺失时为空。</summary>
    public double? OutputTokensPerSecond =>
        Usage?.CompletionTokens is { } completion && GenerationSeconds is { } seconds && seconds > 0
            ? completion / seconds
            : null;

    /// <summary>TTFT 数值文本，如 <c>1234ms</c>。</summary>
    public string? FirstTokenText =>
        FirstTokenSeconds is { } seconds ? $"{(long)Math.Round(seconds * 1000)}ms" : null;

    /// <summary>输入/输出 tokens 文本，如 <c>↑3710 ↓6322</c>；只有一侧时只显示那一侧。</summary>
    public string? TokensText =>
        (Usage?.PromptTokens, Usage?.CompletionTokens) switch
        {
            ({ } input, { } output) => $"↑{input} ↓{output}",
            ({ } input, null) => $"↑{input}",
            (null, { } output) => $"↓{output}",
            _ => null
        };

    /// <summary>平均速度的展示文本，如 <c>188.7 tok/s</c>。</summary>
    public string? SpeedText =>
        OutputTokensPerSecond is { } speed ? $"{speed:0.#} tok/s" : null;

    public bool HasInlineStats => InlineStatsText.Length > 0;

    /// <summary>
    /// 动作条里跟在响应统计按钮后面的一行摘要，例如
    /// <c>TTFT: 1234ms · Tokens: ↑3710 ↓6322 · 188.7 tok/s</c>。
    /// 没有数据的段落自动省略；全都缺失时返回空串，由
    /// <see cref="HasInlineStats"/> 隐藏整段。
    /// </summary>
    public string InlineStatsText
    {
        get
        {
            var parts = new List<string>(3);
            if (FirstTokenText is { } ttft) parts.Add($"TTFT: {ttft}");
            if (TokensText is { } tokens) parts.Add($"Tokens: {tokens}");
            if (SpeedText is { } speed) parts.Add(speed);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 从历史版本恢复时序。持久化的是秒数而不是时刻，这里以当前时刻为锚点反推，
    /// 使 <see cref="FirstTokenSeconds"/> / <see cref="GenerationSeconds"/> 精确复现。
    /// </summary>
    public void RestoreTurnTiming(double? firstTokenSeconds, double? generationSeconds)
    {
        if (firstTokenSeconds is not { } latency)
        {
            _requestStartedAt = null;
            _firstOutputAt = null;
            _streamCompletedAt = null;
        }
        else
        {
            var start = DateTimeOffset.UtcNow;
            _requestStartedAt = start;
            _firstOutputAt = start + TimeSpan.FromSeconds(latency);
            _streamCompletedAt = generationSeconds is { } generation
                ? _firstOutputAt.Value + TimeSpan.FromSeconds(generation)
                : null;
        }
        NotifyTurnStatsChanged();
    }

    /// <summary>
    /// 标记本轮请求已真正发出（在 <c>provider.StreamChatAsync</c> 之前）。
    /// 幂等：续流重连不覆盖最初起点；重试由 <see cref="BeginRetryAttempt"/> 先清零。
    /// </summary>
    public void MarkRequestStarted()
    {
        if (_disposed || _requestStartedAt is not null) return;
        _requestStartedAt = DateTimeOffset.UtcNow;
        NotifyTurnStatsChanged();
    }

    /// <summary>记录首个输出。正文、思考、工具卡任一先到都算，只在第一次生效。</summary>
    private void MarkFirstOutput()
    {
        if (_disposed || _firstOutputAt is not null) return;
        _firstOutputAt = DateTimeOffset.UtcNow;
        NotifyTurnStatsChanged();
    }

    private void NotifyTurnStatsChanged()
    {
        OnPropertyChanged(nameof(FirstTokenSeconds));
        OnPropertyChanged(nameof(GenerationSeconds));
        OnPropertyChanged(nameof(OutputTokensPerSecond));
        OnPropertyChanged(nameof(FirstTokenText));
        OnPropertyChanged(nameof(TokensText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(InlineStatsText));
        OnPropertyChanged(nameof(HasInlineStats));
    }

    private DateTimeOffset? _thinkingStartedAt;
    private DateTimeOffset? _pendingStartedAt;

    /// <summary>
    /// 本轮的时序打点：请求真正发出、首个输出到达、上游结束。三者都是 UTC 时刻，
    /// 只在一次回答版本内有效——切换重试版本时随版本存取，重新生成时清零。
    /// </summary>
    private DateTimeOffset? _requestStartedAt;
    private DateTimeOffset? _firstOutputAt;
    private DateTimeOffset? _streamCompletedAt;

    private System.Threading.Timer? _elapsedTimer;
    private System.Threading.Timer? _pendingTimer;

    /// <summary>Holds streamed text back and releases it at a readable rate.
    /// Both the answer and the reasoning go through this one instance, so they
    /// share a single rate budget: two independent throttles would have let a
    /// turn that reasons and answers at once emit at twice the intended
    /// speed.</summary>
    private readonly StreamPacer _pacer = new();
    private System.Threading.Timer? _paceTimer;
    private readonly System.Threading.Lock _paceLock = new();
    private readonly System.Diagnostics.Stopwatch _paceClock = new();
    private bool _paceScheduled;
    private string? _streamedMarkupCloseTag;

    /// <summary>Whether the active segment currently shows anything. Tracked so a
    /// delta only rebuilds the display blocks when that answer changes.</summary>
    private bool _activeThinkingVisible;
    private readonly SynchronizationContext? _syncContext;
    private ThinkingSegmentViewModel? _activeThinkingSegment;
    private int _nextDisplaySequence;
    private bool _disposed;

    public MessageViewModel(string role, string content, DateTimeOffset timestamp)
    {
        _role = role;
        _content = content;
        _timestamp = timestamp;
        // Capture the UI sync context if we were constructed on the UI thread
        // (which is the normal case — ChatViewModel.AppendUserMessage and
        // BeginAssistantMessage both run on the dispatcher). Falls back to
        // null on background threads, in which case the timer ticks fire on
        // a thread pool thread and WPF's binding system marshals back.
        _syncContext = SynchronizationContext.Current;
        ToolCalls.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasToolCalls));
            RebuildDisplayBlocks();
        };
        ThinkingSegments.CollectionChanged += (_, _) => RebuildDisplayBlocks();
        RebuildDisplayBlocks();
    }

    public void AppendDelta(string delta)
    {
        if (_disposed || string.IsNullOrEmpty(delta)) return;
        MarkFirstOutput();

        if (IsStreaming)
        {
            AppendStreamingDelta(delta);
        }
        else
        {
            Content += delta;
        }

        // First content delta after thinking → freeze the elapsed counter
        // and stop the pulsing UI. This is the "思考已完成" transition.
        if (IsThinkingActive && HasThinking)
        {
            StopThinking();
        }
    }

    private void AppendStreamingDelta(string delta)
    {
        if (_streamedMarkupCloseTag is null
            && !StreamedMarkupOpenRegex().IsMatch(delta)
            && !StreamedImageMarkupRegex().IsMatch(delta))
        {
            _pacer.Enqueue(PacedStream.Answer, delta);
            SchedulePaceFrame();
            return;
        }

        // Tool markup is structural SSE content. Revealing it one character at
        // a time briefly turns incomplete tags into visible Markdown and makes
        // the text inside the card inherit the answer's tail effect.
        FlushPendingDelta();
        Content += delta;
        TrackStreamedMarkup(delta);
    }

    private void TrackStreamedMarkup(string text)
    {
        var cursor = 0;
        while (cursor < text.Length)
        {
            if (_streamedMarkupCloseTag is { } closeTag)
            {
                var close = text.IndexOf(closeTag, cursor, StringComparison.OrdinalIgnoreCase);
                if (close < 0) return;

                cursor = close + closeTag.Length;
                _streamedMarkupCloseTag = null;
            }

            var open = StreamedMarkupOpenRegex().Match(text, cursor);
            if (!open.Success) return;

            _streamedMarkupCloseTag = open.Value.StartsWith("<DSanalysis", StringComparison.OrdinalIgnoreCase)
                ? "</DSanalysis>"
                : open.Value.StartsWith("<steel-step", StringComparison.OrdinalIgnoreCase)
                    ? "</steel-step>"
                    : "</blockquote>";
            cursor = open.Index + open.Length;
        }
    }

    /// <summary>
    /// The whole answer, including any tail the pacer is still revealing.
    ///
    /// <see cref="Content"/> is what the transcript shows, and after the stream
    /// ends it deliberately lags for up to a couple of seconds while the last
    /// words play out. Everything that captures the finished text — persistence,
    /// the history sent with the next request, title generation — has to read
    /// this instead, or it saves a truncated answer that nothing would report.
    /// </summary>
    public string FullContent
    {
        get
        {
            var pending = _pacer.PendingAnswer;
            return pending.Length == 0 ? Content : Content + pending;
        }
    }

    /// <summary>Whether text is still being revealed. Stays true through the
    /// post-stream drain, after <see cref="IsStreaming"/> has gone false, and is
    /// what the trailing fade keys off.</summary>
    [ObservableProperty] private bool _isRevealing;

    /// <summary>
    /// Reveal every queued character immediately.
    ///
    /// This is the escape hatch for the places where the display has to be in
    /// step with the model right now rather than a fraction of a second behind:
    /// a tool card about to record its offset in the text, a cancelled turn, a
    /// wholesale content replacement. Ordinary completion does not go through
    /// here — see <see cref="CompleteStreaming"/>.
    /// </summary>
    public void FlushPendingDelta()
    {
        if (_disposed) return;
        _pacer.DumpAll(EmitPaced);
        StopPaceFrames();
    }

    /// <summary>
    /// Upstream has finished producing text.
    ///
    /// Reasoning is released whole, and the answer switches to a bounded drain
    /// so the last words are not slammed onto the screen in one frame — which is
    /// exactly what a dump-on-finish does, and what the tapering batch size
    /// during streaming exists to avoid in the first place.
    /// </summary>
    public void CompleteStreaming()
    {
        if (_disposed) return;
        _streamCompletedAt ??= DateTimeOffset.UtcNow;
        NotifyTurnStatsChanged();
        _pacer.Complete(EmitPaced);
        _streamedMarkupCloseTag = null;
        if (_pacer.HasPending) SchedulePaceFrame();
        else StopPaceFrames();
    }

    /// <summary>Swaps the whole answer for a new one. The queue is discarded
    /// rather than drained: the caller is supplying the complete text, so
    /// anything still in flight is already part of it.</summary>
    public void ReplaceContent(string text)
    {
        if (_disposed) return;
        _pacer.Reset();
        StopPaceFrames();
        _streamedMarkupCloseTag = null;
        Content = text;
    }

    public void FinishStreaming()
    {
        if (_disposed) return;
        CompleteStreaming();
        IsStreaming = false;
    }

    // ---- pacing frame loop -------------------------------------------------

    private void SchedulePaceFrame()
    {
        lock (_paceLock)
        {
            if (_disposed || _paceScheduled) return;
            _paceScheduled = true;
            _paceTimer ??= new System.Threading.Timer(
                _ => PostPaceFrame(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _paceTimer.Change(StreamPacer.FrameInterval, Timeout.InfiniteTimeSpan);
        }

        if (!IsRevealing) IsRevealing = true;
    }

    private void StopPaceFrames()
    {
        lock (_paceLock)
        {
            _paceScheduled = false;
            _paceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _paceClock.Reset();
        }

        if (IsRevealing) IsRevealing = false;
    }

    private void PostPaceFrame()
    {
        if (_disposed) return;
        if (_syncContext is not null) _syncContext.Post(_ => PaceFrame(), null);
        else PaceFrame();
    }

    /// <summary>
    /// One frame of playout.
    ///
    /// The elapsed time is measured rather than assumed to be the timer period.
    /// A <see cref="System.Threading.Timer"/> on Windows resolves to about
    /// 15.6ms and coalesces under load, so a fixed batch per tick makes output
    /// <em>slower</em> exactly when the UI thread is already struggling —
    /// punishing the user twice. Feeding the real delta to the pacer keeps the
    /// perceived rate flat across a stutter instead.
    /// </summary>
    private void PaceFrame()
    {
        if (_disposed) return;

        double elapsedMs;
        lock (_paceLock)
        {
            _paceScheduled = false;
            elapsedMs = _paceClock.IsRunning ? _paceClock.Elapsed.TotalMilliseconds : 0;
            _paceClock.Restart();
        }

        _pacer.Drain(elapsedMs, EmitPaced);

        if (_pacer.HasPending) SchedulePaceFrame();
        else StopPaceFrames();
    }

    private void EmitPaced(PacedStream kind, string text)
    {
        if (_disposed || text.Length == 0) return;
        if (kind == PacedStream.Answer) Content += text;
        else CommitThinking(text);
    }

    public void AppendThinking(string delta)
    {
        if (_disposed || string.IsNullOrEmpty(delta)) return;
        MarkFirstOutput();

        // Opening a segment is structural — the card has to appear on the first
        // delta — so it is never deferred. Only the text is.
        if (_thinkingStartedAt is null)
        {
            _thinkingStartedAt = DateTimeOffset.UtcNow;
            ThinkingElapsedSeconds = 0;
            IsThinkingActive = true;
            _activeThinkingSegment = CreateThinkingSegment();
            _activeThinkingVisible = false;
            EnsureElapsedTimer();
        }
        else if (_activeThinkingSegment is null)
        {
            _activeThinkingSegment = CreateThinkingSegment();
            _activeThinkingSegment.IsThinking = IsThinkingActive;
            _activeThinkingSegment.ElapsedSeconds = ThinkingElapsedSeconds;
            _activeThinkingVisible = false;
        }

        // Reasoning arrives token by token and is the longest text in the turn,
        // so it gets the same coalescing the answer has always had. Without it
        // every token re-rendered the whole segment: a minute of reasoning at a
        // few hundred characters a second means thousands of full re-renders of
        // a body that ends up tens of thousands of characters long.
        if (!IsStreaming)
        {
            CommitThinking(delta);
            return;
        }

        _pacer.Enqueue(PacedStream.Thinking, delta);
        SchedulePaceFrame();
    }

    /// <summary>
    /// Applies queued reasoning text to the aggregate and to the segment it
    /// belongs to.
    ///
    /// The display-block list is deliberately not rebuilt here. Growing a
    /// segment's text changes no block's identity, offset or count, so the list
    /// is already correct — and rebuilding it re-sorted every tool call and
    /// segment and re-scanned all of their text, which over a long reasoning
    /// turn cost more than the rendering did. The one thing a delta can change
    /// is whether a segment holding nothing but hidden markup has become
    /// visible, or a half-streamed marker has just closed and hidden it.
    /// </summary>
    private void CommitThinking(string text)
    {
        if (_disposed || text.Length == 0) return;

        Thinking = (Thinking ?? string.Empty) + text;
        OnPropertyChanged(nameof(HasThinking));

        if (_activeThinkingSegment is not { } segment) return;
        segment.Append(text);

        // Visibility can only move when a marker character is in play: a
        // construct that hides a segment has to open with '<' and close with
        // '>'. Ordinary reasoning arriving at a segment that is already showing
        // cannot change the answer, and skipping the check there keeps four
        // regex passes over the longest text in the turn off the streaming path
        // — reasoning that happens to discuss markup would otherwise land on
        // the slow path for every delta.
        if (_activeThinkingVisible && !text.AsSpan().ContainsAny('<', '>')) return;

        var visible = IsThinkingSegmentVisible(segment);
        if (visible == _activeThinkingVisible) return;

        _activeThinkingVisible = visible;
        RebuildDisplayBlocks();
    }

    /// <summary>Commit any queued reasoning immediately, so persistence and the
    /// finished card never miss the tail waiting for the next frame.</summary>
    public void FlushPendingThinking()
    {
        if (_disposed) return;
        _pacer.DumpThinking(EmitPaced);
    }

    /// <summary>Freeze the elapsed counter and clear active state. Called
    /// when normal content starts arriving or when streaming finalizes.
    ///
    /// Only the reasoning queue is drained here. Draining the answer too — as
    /// this used to — would dump the answer's backlog at the exact moment the
    /// first answer token arrives, which is the one transition the pacing is
    /// most visible on.</summary>
    public void StopThinking()
    {
        FlushPendingThinking();
        if (_thinkingStartedAt is { } start)
            ThinkingElapsedSeconds = (DateTimeOffset.UtcNow - start).TotalSeconds;
        if (_activeThinkingSegment is { } segment)
        {
            segment.ElapsedSeconds = ThinkingElapsedSeconds;
            segment.IsThinking = false;
            if (AutoCollapseThinkingOnComplete)
                segment.IsExpanded = false;
        }
        _activeThinkingSegment = null;
        _thinkingStartedAt = null;
        IsThinkingActive = false;
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;
    }

    public void BeginRetryAttempt()
    {
        var attempts = RetryAttempts?.ToList() ?? new List<MessageAttempt>();
        if (attempts.Count == 0)
            attempts.Add(CaptureRetryAttempt());

        Content = string.Empty;
        Thinking = null;
        WasStopped = false;
        ThinkingSegments.Clear();
        ToolCalls.Clear();
        DisplayBlocks.Clear();
        // The attempt above already captured everything the pacer was still
        // holding, so the queue is discarded rather than drained into the empty
        // content the retry starts from.
        _pacer.Reset();
        StopPaceFrames();
        _streamedMarkupCloseTag = null;
        _activeThinkingSegment = null;
        _activeThinkingVisible = false;
        _thinkingStartedAt = null;
        _requestStartedAt = null;
        _firstOutputAt = null;
        _streamCompletedAt = null;
        NotifyTurnStatsChanged();
        _nextDisplaySequence = 0;
        ThinkingElapsedSeconds = 0;
        Usage = null;
        Sources = null;
        RetryAttempts = attempts;
        RetryCurrentIndex = attempts.Count - 1;
    }

    public void CommitRetryAttempt()
    {
        var attempts = RetryAttempts?.ToList() ?? new List<MessageAttempt>();
        attempts.Add(CaptureRetryAttempt());
        RetryAttempts = attempts;
        RetryCurrentIndex = attempts.Count - 1;
    }

    private MessageAttempt CaptureRetryAttempt()
    {
        var thinkingSegments = ThinkingSegments.Count == 0
            ? null
            : ThinkingSegments
                .Where(segment => !string.IsNullOrWhiteSpace(segment.Source))
                .Select(segment => new ThinkingSegmentDelta(
                    segment.Source,
                    segment.ContentOffset,
                    segment.ElapsedSeconds,
                    segment.TimelineIndex))
                .ToArray();
        var toolCalls = ToolCalls.Count == 0
            ? null
            : ToolCalls
                .Select(tool => new ToolCallDelta(
                    tool.Id,
                    tool.Name,
                    tool.Status,
                    tool.Label,
                    tool.Summary,
                    tool.Detail,
                    tool.ArgumentsJson,
                    tool.ResultPreviewJson,
                    tool.Provider,
                    tool.ContentOffset,
                    tool.TimelineIndex))
                .ToArray();

        return new MessageAttempt(
            FullContent,
            ModelLabel,
            Usage,
            Sources,
            WasStopped,
            Thinking,
            thinkingSegments,
            toolCalls,
            FirstTokenSeconds,
            GenerationSeconds);
    }

    [RelayCommand(CanExecute = nameof(CanPreviousAttempt))]
    private void PreviousAttempt() => SelectAttempt(RetryCurrentIndex - 1);

    [RelayCommand(CanExecute = nameof(CanNextAttempt))]
    private void NextAttempt() => SelectAttempt(RetryCurrentIndex + 1);

    private bool CanPreviousAttempt() => RetryAttempts is { Count: > 1 } && RetryCurrentIndex > 0;
    private bool CanNextAttempt() => RetryAttempts is { Count: > 1 } && RetryCurrentIndex < RetryAttempts.Count - 1;

    private void SelectAttempt(int index)
    {
        if (RetryAttempts is not { Count: > 0 } attempts) return;
        index = Math.Max(0, Math.Min(index, attempts.Count - 1));
        var attempt = attempts[index];

        // Retry version switching should replace the whole rendered answer,
        // not keep tool/thinking UI fragments from another attempt.
        StopPending();
        IsStreaming = false;
        StopThinking();
        ToolCalls.Clear();
        ThinkingSegments.Clear();
        _nextDisplaySequence = 0;

        Content = attempt.Content;
        Thinking = attempt.Thinking;
        ModelLabel = attempt.ModelLabel;
        Usage = attempt.Usage;
        Sources = attempt.Sources;
        WasStopped = attempt.WasStopped;
        if (attempt.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in attempt.ToolCalls)
                ApplyToolDelta(toolCall);
        }
        if (!string.IsNullOrWhiteSpace(attempt.Thinking))
        {
            RestoreThinkingSegments(attempt.ThinkingSegments is { Count: > 0 }
                ? attempt.ThinkingSegments
                : [new ThinkingSegmentDelta(attempt.Thinking, 0)]);
        }
        // 放在工具卡/思考段恢复之后：ApplyToolDelta 会记首个输出，这里必须覆盖它。
        RestoreTurnTiming(attempt.FirstTokenSeconds, attempt.GenerationSeconds);
        RetryCurrentIndex = index;
    }

    public void StartPending(bool routes)
    {
        if (_disposed) return;
        IsRoutesPending = routes;
        _pendingStartedAt = DateTimeOffset.UtcNow;
        _explicitStatusUntil = null;
        IsPending = true;
        UpdatePendingCopy();
        _pendingTimer ??= new System.Threading.Timer(_ => PostUpdatePendingCopy(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pendingTimer.Change(TimeSpan.FromMilliseconds(650), TimeSpan.FromMilliseconds(650));
    }

    /// <summary>
    /// How long an explicitly set status holds before the generic rotation takes
    /// over again. Without this the rotation timer overwrites it within 650ms, so
    /// "上传附件" / "启动 Agent 运行时" would flash by unread; with it the phase
    /// stays legible, and a phase that outlives the window still falls back to
    /// reassuring copy instead of a stale label.
    /// </summary>
    private static readonly TimeSpan ExplicitStatusHold = TimeSpan.FromSeconds(4);
    private DateTimeOffset? _explicitStatusUntil;

    public void SetPendingStatus(string label, string? detail = null, bool? routes = null)
    {
        if (_disposed) return;
        if (routes is { } value) IsRoutesPending = value;
        PendingLabel = label;
        PendingDetail = detail;
        _explicitStatusUntil = DateTimeOffset.UtcNow + ExplicitStatusHold;
        if (IsPending) return;

        _pendingStartedAt = DateTimeOffset.UtcNow;
        IsPending = true;
        _pendingTimer ??= new System.Threading.Timer(_ => PostUpdatePendingCopy(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pendingTimer.Change(TimeSpan.FromMilliseconds(650), TimeSpan.FromMilliseconds(650));
    }

    public void ApplyToolDelta(ToolCallDelta delta)
    {
        if (_disposed) return;
        MarkFirstOutput();
        StopPending();
        var existing = ToolCalls.FirstOrDefault(t => t.Id == delta.Id);
        if (existing is null)
        {
            StopThinking();
            FlushPendingDelta();
            existing = new ToolCallViewModel(delta.Id, delta.Name);
            existing.ContentOffset = delta.ContentOffset ?? Content.Length;
            existing.TimelineIndex = delta.TimelineIndex ?? _nextDisplaySequence++;
            AdvanceNextDisplaySequence(existing.TimelineIndex);
            ToolCalls.Add(existing);
        }

        existing.Apply(delta);
        RebuildDisplayBlocks();
    }

    public void StopPending()
    {
        if (!IsPending && _pendingTimer is null) return;
        IsPending = false;
        _pendingStartedAt = null;
        _explicitStatusUntil = null;
        _pendingTimer?.Dispose();
        _pendingTimer = null;
    }

    private void PostUpdatePendingCopy()
    {
        if (_disposed) return;
        if (_syncContext is not null) _syncContext.Post(_ => UpdatePendingCopy(), null);
        else UpdatePendingCopy();
    }

    private void UpdatePendingCopy()
    {
        if (_disposed || !IsPending || _pendingStartedAt is null) return;
        var now = DateTimeOffset.UtcNow;
        if (_explicitStatusUntil is { } until && now < until) return;
        _explicitStatusUntil = null;
        var elapsed = now - _pendingStartedAt.Value;

        if (IsRoutesPending)
        {
            if (elapsed >= TimeSpan.FromSeconds(10))
            {
                PendingLabel = "MolaGPT Routes 工作中";
                PendingDetail = "正在选择模型";
            }
            else if (elapsed >= TimeSpan.FromMilliseconds(900))
            {
                PendingLabel = "初始化模型";
                PendingDetail = "分类用户问题";
            }
            else
            {
                PendingLabel = "MolaGPT Routes 工作中";
                PendingDetail = "分类用户问题";
            }
            return;
        }

        if (elapsed >= TimeSpan.FromSeconds(10))
        {
            PendingLabel = "继续等待回答";
            PendingDetail = null;
        }
        else if (elapsed >= TimeSpan.FromMilliseconds(900))
        {
            PendingLabel = "等待模型回答";
            PendingDetail = null;
        }
        else
        {
            PendingLabel = "回复处理中";
            PendingDetail = null;
        }
    }

    private void EnsureElapsedTimer()
    {
        if (_elapsedTimer is not null) return;
        _elapsedTimer = new System.Threading.Timer(_ =>
        {
            if (_disposed || _thinkingStartedAt is null) return;
            void Update()
            {
                if (_disposed || _thinkingStartedAt is null) return;
                ThinkingElapsedSeconds = (DateTimeOffset.UtcNow - _thinkingStartedAt.Value).TotalSeconds;
                if (_activeThinkingSegment is { } segment)
                    segment.ElapsedSeconds = ThinkingElapsedSeconds;
            }
            if (_syncContext is not null) _syncContext.Post(_ => Update(), null);
            else Update();
        }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    partial void OnThinkingChanged(string? value) => OnPropertyChanged(nameof(HasThinking));
    partial void OnRoleChanged(string value) => OnActionStateChanged();
    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleContent));
        RebuildDisplayBlocks();
        OnActionStateChanged();
    }
    partial void OnIsStreamingChanged(bool value) => OnActionStateChanged();
    partial void OnIsPendingChanged(bool value) => OnActionStateChanged();
    partial void OnWasStoppedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStoppedNotice));
        OnActionStateChanged();
    }
    partial void OnAttachmentsChanged(IReadOnlyList<AttachmentChip>? value) => OnPropertyChanged(nameof(HasAttachments));
    partial void OnSourcesChanged(IReadOnlyList<SourceReference>? value)
    {
        OnPropertyChanged(nameof(VisibleContent));
        RebuildDisplayBlocks();
    }
    partial void OnUsageChanged(Usage? value)
    {
        OnPropertyChanged(nameof(HasResponseStats));
        NotifyTurnStatsChanged();
    }
    partial void OnModelLabelChanged(string? value)
    {
        OnPropertyChanged(nameof(HasResponseStats));
    }
    partial void OnRetryAttemptsChanged(IReadOnlyList<MessageAttempt>? value)
    {
        OnPropertyChanged(nameof(HasRetryBar));
        OnPropertyChanged(nameof(RetryCounter));
        PreviousAttemptCommand.NotifyCanExecuteChanged();
        NextAttemptCommand.NotifyCanExecuteChanged();
    }
    partial void OnRetryCurrentIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RetryCounter));
        PreviousAttemptCommand.NotifyCanExecuteChanged();
        NextAttemptCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsLatestAssistantChanged(bool value) => OnPropertyChanged(nameof(HasRetryBar));

    private void OnActionStateChanged()
    {
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(ShowStoppedNotice));
    }

    private void RebuildDisplayBlocks()
    {
        var next = new List<MessageDisplayBlockViewModel>();
        var content = Content ?? string.Empty;
        var cursor = 0;

        // Accumulator for a run of consecutive same-kind groupable tools. runName
        // holds the canonical group key (aliases collapsed), not the raw tool name.
        var run = new List<ToolCallViewModel>();
        string? runName = null;
        void FlushRun()
        {
            if (run.Count == 0) return;
            // A lone groupable tool renders as a normal single card; ≥2 merge.
            next.Add(run.Count == 1
                ? MessageDisplayBlockViewModel.ForTool(run[0])
                : MessageDisplayBlockViewModel.ForToolGroup(new ToolGroupViewModel(runName!, run.ToList())));
            run.Clear();
            runName = null;
        }

        foreach (var tool in ToolCalls
                     .Cast<object>()
                     .Concat(ThinkingSegments)
                     .OrderBy(GetBlockContentOffset)
                     .ThenBy(GetBlockTimelineIndex))
        {
            var offset = Math.Clamp(GetBlockContentOffset(tool), cursor, content.Length);
            if (offset > cursor)
            {
                FlushRun(); // visible text between calls breaks a run
                next.Add(MessageDisplayBlockViewModel.ForText(ProcessCitationRefs(content[cursor..offset])));
            }
            if (tool is ToolCallViewModel toolCall)
            {
                if (toolCall.GroupKey is { } groupKey)
                {
                    if (runName is not null && !string.Equals(runName, groupKey, StringComparison.Ordinal))
                        FlushRun(); // a different groupable kind breaks the run
                    runName = groupKey;
                    run.Add(toolCall);
                }
                else
                {
                    FlushRun();
                    next.Add(MessageDisplayBlockViewModel.ForTool(toolCall));
                }
            }
            else if (tool is ThinkingSegmentViewModel thinking && IsThinkingSegmentVisible(thinking))
            {
                FlushRun();
                next.Add(MessageDisplayBlockViewModel.ForThinking(thinking));
            }
            cursor = offset;
        }

        FlushRun();
        if (cursor < content.Length)
            next.Add(MessageDisplayBlockViewModel.ForText(ProcessCitationRefs(content[cursor..])));

        SyncDisplayBlocks(next);

        // The placeholder goes when there is something to replace it with, not
        // when the first byte lands. Those are not the same moment: a turn whose
        // first delta is a newline (common) or a not-yet-closed hidden marker
        // draws nothing, and tearing the pill down on arrival left the answer a
        // blank gap under the model name until real text showed up — which for a
        // reasoning model is the whole thinking phase.
        if (IsPending && HasVisibleOutput) StopPending();
    }

    /// <summary>
    /// Whether the transcript would draw anything for this message yet.
    ///
    /// Deliberately conservative about text: whitespace produces a display block
    /// but no markdown block, so counting blocks alone would call an empty
    /// message visible. Tool cards and reasoning cards are structural — they are
    /// on screen the moment they exist.
    /// </summary>
    private bool HasVisibleOutput
    {
        get
        {
            foreach (var block in DisplayBlocks)
            {
                if (block.Tool is not null || block.ToolGroup is not null || block.Thinking is not null)
                    return true;
                if (block.IsText && !string.IsNullOrWhiteSpace(block.Text)) return true;
            }
            return false;
        }
    }

    private void SyncDisplayBlocks(IReadOnlyList<MessageDisplayBlockViewModel> next)
    {
        var common = Math.Min(DisplayBlocks.Count, next.Count);
        for (var i = 0; i < common; i++)
        {
            if (DisplayBlocks[i].TryUpdateFrom(next[i])) continue;
            DisplayBlocks[i] = next[i];
        }

        while (DisplayBlocks.Count > next.Count)
            DisplayBlocks.RemoveAt(DisplayBlocks.Count - 1);

        for (var i = DisplayBlocks.Count; i < next.Count; i++)
            DisplayBlocks.Add(next[i]);
    }

    private static bool IsThinkingSegmentVisible(ThinkingSegmentViewModel thinking)
    {
        var source = thinking.Source;
        if (string.IsNullOrWhiteSpace(source)) return false;

        // Every marker that can hide a segment opens with '<', and ordinary
        // reasoning contains none. Reasoning is also the longest text in a turn,
        // so the fast path is what stops four regex passes over tens of
        // thousands of characters from running on the streaming path.
        if (!source.Contains('<')) return true;

        var visible = ImageGenDsAnalysisRegex().Replace(source, string.Empty);
        visible = HiddenDsAnalysisRegex().Replace(visible, string.Empty);
        visible = EmptyDsAnalysisRegex().Replace(visible, string.Empty);
        visible = HtmlCommentRegex().Replace(visible, string.Empty);
        return visible.Any(ch => !char.IsWhiteSpace(ch) && ch != '\u200B' && ch != '\uFEFF');
    }

    private ThinkingSegmentViewModel CreateThinkingSegment()
        => CreateThinkingSegmentAt(Content.Length);

    private ThinkingSegmentViewModel CreateThinkingSegmentAt(int contentOffset)
    {
        var segment = new ThinkingSegmentViewModel
        {
            ContentOffset = Math.Max(0, contentOffset),
            TimelineIndex = _nextDisplaySequence++,
            IsThinking = true,
            ElapsedSeconds = ThinkingElapsedSeconds
        };
        ThinkingSegments.Add(segment);
        return segment;
    }

    public void RestoreThinkingSegments(IReadOnlyList<ThinkingSegmentDelta> segments)
    {
        ThinkingSegments.Clear();
        _activeThinkingSegment = null;
        foreach (var item in segments)
        {
            ThinkingSegments.Add(new ThinkingSegmentViewModel
            {
                Source = item.Source,
                ContentOffset = item.ContentOffset,
                TimelineIndex = item.TimelineIndex ?? _nextDisplaySequence++,
                IsThinking = false,
                IsExpanded = !AutoCollapseThinkingOnComplete,
                ElapsedSeconds = item.ElapsedSeconds
            });
            AdvanceNextDisplaySequence(ThinkingSegments[^1].TimelineIndex);
        }
        RebuildDisplayBlocks();
    }

    private void AdvanceNextDisplaySequence(int usedIndex)
    {
        if (_nextDisplaySequence <= usedIndex)
            _nextDisplaySequence = usedIndex + 1;
    }

    private static int GetBlockContentOffset(object block) => block switch
    {
        ToolCallViewModel tool => Math.Max(0, tool.ContentOffset),
        ThinkingSegmentViewModel thinking => Math.Max(0, thinking.ContentOffset),
        _ => 0
    };

    private static int GetBlockTimelineIndex(object block) => block switch
    {
        ToolCallViewModel tool => tool.TimelineIndex,
        ThinkingSegmentViewModel thinking => thinking.TimelineIndex,
        _ => 0
    };

    public static string StripSystemHints(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        return DaggerWrappedTokenRegex().Replace(content, string.Empty).Trim();
    }

    private string ProcessCitationRefs(string source)
    {
        if (source.Length == 0 || !source.Contains("<ref", StringComparison.OrdinalIgnoreCase))
            return source;

        var sources = Sources ?? Array.Empty<SourceReference>();
        return RefTagRegex().Replace(source, match =>
        {
            var inner = match.Groups["inner"].Success ? match.Groups["inner"].Value : string.Empty;
            if (sources.Count == 0) return inner;

            var attrs = match.Groups["attrs"].Success
                ? match.Groups["attrs"].Value
                : match.Groups["attrs2"].Value;
            var sourceMatch = RefSourceRegex().Match(attrs);
            if (!sourceMatch.Success) return inner;

            var links = new List<string>();
            foreach (var id in ParseSourceIds(sourceMatch.Groups["value"].Value))
            {
                var sourceRef = sources.FirstOrDefault(item => item.Id == id);
                if (sourceRef is null) continue;

                var url = string.IsNullOrWhiteSpace(sourceRef.Url)
                    ? "#"
                    : sourceRef.Url.Replace(")", "%29", StringComparison.Ordinal);
                var title = sourceRef.Title.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal);
                links.Add(title.Length == 0
                    ? $"[[来源 {id}]]({url})"
                    : $"[[来源 {id}]]({url} \"{title}\")");
            }

            return links.Count == 0 ? inner : string.Join(" ", links) + inner;
        });
    }

    private static IReadOnlyList<int> ParseSourceIds(string value)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var part in value.Split([',', '，', '|', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = Regex.Match(part, @"^(\d+)\s*[-~]\s*(\d+)$");
            if (range.Success
                && int.TryParse(range.Groups[1].Value, out var start)
                && int.TryParse(range.Groups[2].Value, out var end))
            {
                for (var id = Math.Min(start, end); id <= Math.Max(start, end); id++)
                    if (seen.Add(id)) result.Add(id);
                continue;
            }

            if (int.TryParse(part, out var single) && seen.Add(single))
                result.Add(single);
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;
        _pendingTimer?.Dispose();
        _pendingTimer = null;
        _paceTimer?.Dispose();
        _paceTimer = null;
        _paceClock.Reset();
    }
}

/// <summary>One saved version of an assistant turn, cycled through by the
/// version switcher.</summary>
/// <param name="WasStopped">Whether the user cut this version short. Travels with
/// the attempt so switching back to it still explains why it is empty, instead of
/// showing a blank bubble.</param>
public sealed record MessageAttempt(
    string Content,
    string? ModelLabel,
    Usage? Usage,
    IReadOnlyList<SourceReference>? Sources,
    bool WasStopped = false,
    string? Thinking = null,
    IReadOnlyList<ThinkingSegmentDelta>? ThinkingSegments = null,
    IReadOnlyList<ToolCallDelta>? ToolCalls = null,
    double? FirstTokenSeconds = null,
    double? GenerationSeconds = null);
/// <summary>
/// Lightweight representation of a sent attachment, kept on the message
/// view-model after the original <see cref="MolaGPT.Core.Models.Attachment"/>
/// (with full <see cref="byte"/> array) has been released.
///
/// Persisted in message meta as <c>{ filename, label, thumbnailUrl, localName,
/// mime }</c>. Three preview/reload paths:
///   - <see cref="Bytes"/> — in-memory only, set right after sending so the
///     just-sent image previews without a disk round-trip;
///   - <see cref="LocalName"/> — BYOK images are content-addressed into the
///     local <c>AttachmentStore</c>; survives reload (bytes re-read from disk);
///   - <see cref="ThumbnailUrl"/> — MolaGPT-account images carry a server URL.
/// </summary>
public sealed record AttachmentChip(string FileName, string Label, string? ThumbnailUrl = null)
{
    public byte[]? Bytes { get; init; }

    /// <summary>Relative file name in the local AttachmentStore (BYOK attachments).
    /// Null for MolaGPT-account images (which use <see cref="ThumbnailUrl"/>).</summary>
    public string? LocalName { get; init; }

    /// <summary>MIME type, persisted so reloaded bytes can be re-encoded as a
    /// data URL for the wire without re-sniffing.</summary>
    public string? MimeType { get; init; }

    /// <summary>Explicit kind. Null on chips persisted before this field existed,
    /// where <see cref="IsImage"/> falls back to inference. Files carry a
    /// LocalName too now, so the old "has a local copy ⇒ image" rule cannot be
    /// used any more.</summary>
    public AttachmentKind? Kind { get; init; }

    /// <summary>Workspace-relative path of the BYOK file copy, so later turns
    /// keep pointing the model at the same file instead of copying it again.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>Workspace-relative path of the extracted-text sidecar written for
    /// documents too large to inline whole.</summary>
    public string? ExtractedTextPath { get; init; }

    /// <summary>Runtime-only: the bytes could not be loaded when building the last
    /// request. Surfaced in the chip so the user sees the same gap the model was
    /// told about. Not persisted — it is re-derived on every send.</summary>
    public bool IsUnavailable { get; init; }

    /// <summary>Secondary chip line. Carries the unavailable state so a lost
    /// attachment is visible in the bubble, not only in the request.</summary>
    public string StatusLabel => IsUnavailable ? $"{Label} · 不可用" : Label;

    public bool IsImage => Kind switch
    {
        AttachmentKind.Image => true,
        AttachmentKind.File => false,
        _ => Bytes is { Length: > 0 }
             || !string.IsNullOrEmpty(ThumbnailUrl)
             || string.Equals(Label, "图片", StringComparison.Ordinal)
    };

    public bool HasInlinePreview =>
        IsImage
        && (Bytes is { Length: > 0 }
            || !string.IsNullOrEmpty(LocalName)
            || !string.IsNullOrEmpty(ThumbnailUrl));
}
public sealed record ThinkingSegmentDelta(string Source, int ContentOffset, double ElapsedSeconds = 0, int? TimelineIndex = null);

public sealed partial class MessageDisplayBlockViewModel : ObservableObject
{
    private MessageDisplayBlockViewModel(string? text, ToolCallViewModel? tool, ThinkingSegmentViewModel? thinking)
    {
        _text = text;
        Tool = tool;
        Thinking = thinking;
    }

    [ObservableProperty] private string? _text;
    public ToolCallViewModel? Tool { get; }
    public ToolGroupViewModel? ToolGroup { get; }
    public ThinkingSegmentViewModel? Thinking { get; }
    public bool IsText => Text is { Length: > 0 };
    public bool IsTool => Tool is not null;
    public bool IsToolGroup => ToolGroup is not null;
    public bool IsThinking => Thinking is not null;

    partial void OnTextChanged(string? value) => OnPropertyChanged(nameof(IsText));

    public bool TryUpdateFrom(MessageDisplayBlockViewModel next)
    {
        if (IsText && next.IsText)
        {
            Text = next.Text;
            return true;
        }

        if (Tool is not null && ReferenceEquals(Tool, next.Tool))
            return true;

        // Same-identity tool group: reconcile its rows in place (keep the group
        // VM instance, only refresh items/header) so the streaming group card
        // doesn't flicker on every delta.
        if (ToolGroup is not null && next.ToolGroup is not null
            && string.Equals(ToolGroup.Name, next.ToolGroup.Name, StringComparison.Ordinal))
        {
            ToolGroup.SyncFrom(next.ToolGroup);
            return true;
        }

        if (Thinking is not null && ReferenceEquals(Thinking, next.Thinking))
            return true;

        return false;
    }

    public static MessageDisplayBlockViewModel ForText(string text) => new(text, null, null);
    public static MessageDisplayBlockViewModel ForTool(ToolCallViewModel tool) => new(null, tool, null);
    public static MessageDisplayBlockViewModel ForThinking(ThinkingSegmentViewModel thinking) => new(null, null, thinking);
    public static MessageDisplayBlockViewModel ForToolGroup(ToolGroupViewModel group) => new(group);

    private MessageDisplayBlockViewModel(ToolGroupViewModel group)
    {
        ToolGroup = group;
    }
}

/// <summary>
/// A run of consecutive same-type file-operation tool calls (read_file /
/// glob_files / grep_files) rendered as one collapsible group card: header with
/// op label + count + aggregate status, one row per call. Header props recompute
/// via <see cref="Refresh"/> on every rebuild (RebuildDisplayBlocks runs on each
/// tool delta), so no per-item subscription is needed — rows bind directly to
/// the reference-stable ToolCallViewModels.
/// </summary>
public sealed partial class ToolGroupViewModel : ObservableObject
{
    public ToolGroupViewModel(string name, IReadOnlyList<ToolCallViewModel> items)
    {
        Name = name;
        foreach (var item in items)
            Items.Add(item);
    }

    public string Name { get; }
    public ObservableCollection<ToolCallViewModel> Items { get; } = new();

    public string IconGlyph => ToolCallViewModel.IconGlyphFor(Name);
    public string Label => ToolCallViewModel.LabelFor(Name);
    public int Count => Items.Count;

    public string CountText => Name switch
    {
        "read_file" => $"{Count} 个文件",
        "glob_files" => $"{Count} 次查找",
        "grep_files" => $"{Count} 次搜索",
        "web_search" => $"{Count} 次搜索",
        "web_fetch" => $"{Count} 个网页",
        _ => $"{Count} 次"
    };

    private int DoneCount => Items.Count(i => i.IsCompleted);
    private int ErrorCount => Items.Count(i => i.IsError);
    private int PendingCount => Items.Count(i => !i.IsCompleted && !i.IsError);

    public bool IsRunning => PendingCount > 0;
    public bool IsError => PendingCount == 0 && ErrorCount > 0;
    public bool IsCompleted => PendingCount == 0 && ErrorCount == 0;

    public string StatusText =>
        IsRunning ? $"{DoneCount}/{Count}"
        : ErrorCount > 0 ? $"{ErrorCount} 失败"
        : $"{Count} 步完成";

    /// <summary>Reconcile this (kept) group's rows to match a freshly built one,
    /// touching only what changed, then refresh the header.</summary>
    public void SyncFrom(ToolGroupViewModel next)
    {
        for (var i = 0; i < next.Items.Count; i++)
        {
            if (i < Items.Count)
            {
                if (!ReferenceEquals(Items[i], next.Items[i]))
                    Items[i] = next.Items[i];
            }
            else
            {
                Items.Add(next.Items[i]);
            }
        }
        while (Items.Count > next.Items.Count)
            Items.RemoveAt(Items.Count - 1);

        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(StatusText));
    }
}

public sealed partial class ThinkingSegmentViewModel : ObservableObject
{
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private double _elapsedSeconds;

    public int ContentOffset { get; set; }
    public int TimelineIndex { get; set; }

    public void Append(string delta)
    {
        if (!string.IsNullOrEmpty(delta))
            Source += delta;
    }
}

public sealed partial class ToolCallViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Id { get; }
    [ObservableProperty] private string _name;
    public int ContentOffset { get; set; }
    public int TimelineIndex { get; set; }

    [ObservableProperty] private string _status = "preparing";
    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private string? _argumentsJson;
    [ObservableProperty] private string? _resultPreviewJson;
    [ObservableProperty] private string? _provider;
    [ObservableProperty] private ToolArgsView _argsView = ToolArgsView.Empty;

    public ToolCallViewModel(string id, string name)
    {
        Id = id;
        _name = name;
        _label = ToolLabelFor(name);
    }

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasProvider => !string.IsNullOrWhiteSpace(Provider);
    public bool HasArguments => !string.IsNullOrWhiteSpace(ArgumentsJson);
    public bool HasResultPreview => !string.IsNullOrWhiteSpace(ResultPreviewJson);

    /// <summary>
    /// Whether to show the raw-JSON "输入参数" fold. Built-in tools already render
    /// their arguments as a tailored body (Python code, search chips, url/path/kv),
    /// so the JSON fold would be redundant — only generic / third-party (MCP) tools
    /// fall back to it.
    /// </summary>
    public bool ShowArgumentsFold => HasArguments && !IsKnownBuiltInTool;
    public string? DisplayArgumentsJson => FormatDisplayJson(ArgumentsJson);
    public string? DisplayResultPreviewJson => FormatDisplayJson(ResultPreviewJson);
    public bool IsCompleted => Status.Equals("completed", StringComparison.OrdinalIgnoreCase);
    public bool IsError => Status.Equals("error", StringComparison.OrdinalIgnoreCase);
    public bool IsSearch => Name.Equals("search_web", StringComparison.OrdinalIgnoreCase)
                            || Name.Equals("web_search", StringComparison.OrdinalIgnoreCase);
    public bool IsGenericTool => !IsSearch;

    /// <summary>
    /// True for tools we render with a tailored compact body (Python code,
    /// search chips, url/path/text/kv chips). Generic / third-party (MCP) tools
    /// fall back to the JSON folds. Drives which body the card shows.
    /// </summary>
    public bool IsKnownBuiltInTool => Name switch
    {
        "search_web" or "web_search" => true,
        "web_fetch" or "steel_browser" => true,
        "execute_python_code" => true,
        "view_image" or "analyze_image" => true,
        "generate_image" => true,
        "read_file" => true,
        "glob_files" => true,
        "grep_files" => true,
        _ => false
    };

    /// <summary>The read-only file tools, which we group when called back to back.</summary>
    public static bool IsFileOperationTool(string name) =>
        name is "read_file" or "glob_files" or "grep_files";

    public bool IsFileOperation => IsFileOperationTool(Name);

    /// <summary>
    /// Canonical grouping key for tools we merge into one card when called back to
    /// back, or null for tools that always stand alone. Aliases collapse to a
    /// single key so a run holds together even if the backend alternates names
    /// within a turn (search_web/web_search → "web_search",
    /// web_fetch/steel_browser → "web_fetch"). The read-only file tools group
    /// under their own names. Only same-key tools merge, so a search run and a
    /// fetch run stay two separate cards.
    /// </summary>
    public static string? GroupKeyFor(string name) => name switch
    {
        "read_file" => "read_file",
        "glob_files" => "glob_files",
        "grep_files" => "grep_files",
        "search_web" or "web_search" => "web_search",
        "web_fetch" or "steel_browser" => "web_fetch",
        _ => null
    };

    public string? GroupKey => GroupKeyFor(Name);
    public bool IsGroupable => GroupKey is not null;

    /// <summary>
    /// One-line argument preview shown in the collapsed header, right after the
    /// label (e.g. <c>"勒古恩 生平" +2</c> for search, a url/path/code head, or
    /// the first key-value). Empty when there is nothing terse to show.
    /// Derived from <see cref="ArgsView"/> (built from the FULL arguments JSON),
    /// so it is reliable even though the result preview is truncated upstream.
    /// </summary>
    public string HeaderArgPreview => BuildHeaderArgPreview();
    public bool HasHeaderArgPreview => !string.IsNullOrEmpty(HeaderArgPreview);

    private const int HeaderArgMaxLength = 48;

    private string BuildHeaderArgPreview()
    {
        var view = ArgsView;

        if (view.HasSearchQueries)
        {
            var queries = view.SearchQueries!;
            var first = queries[0].Text;
            var preview = Quote(Clip(first, HeaderArgMaxLength));
            return queries.Count > 1 ? $"{preview} +{queries.Count - 1}" : preview;
        }

        if (view.HasCodeArg)
        {
            var firstLine = (view.CodeArg!.Code ?? string.Empty)
                .Replace("\r", string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return firstLine is null ? string.Empty : Clip(firstLine.Trim(), HeaderArgMaxLength);
        }

        if (view.HasPrimaryArg)
            return Clip(view.PrimaryArg!.Value, HeaderArgMaxLength);

        if (view.HasKeyValueArgs)
        {
            var kv = view.KeyValueArgs![0];
            return $"{kv.Key}: {Clip(kv.Value, HeaderArgMaxLength - kv.Key.Length - 2)}";
        }

        return string.Empty;
    }

    private static string Clip(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var single = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (max < 1) max = 1;
        return single.Length <= max ? single : single[..max].TrimEnd() + "…";
    }

    private static string Quote(string value) => $"\"{value}\"";

    public string IconGlyph => IconGlyphFor(Name);
    public static string IconGlyphFor(string name) => name switch
    {
        "search_web" or "web_search" => "\uE721",
        "web_fetch" or "steel_browser" => "\uE774",
        "execute_python_code" => "\uE943",
        "read_file" => "\uE8A5",
        "glob_files" => "\uE8B7",
        "grep_files" => "\uE773",
        _ => "\uE90F"
    };
    public string StatusText => Status switch
    {
        "preparing" => "准备调用",
        "running" => "运行中",
        "completed" => "已完成",
        "error" => "出错",
        _ => Status
    };

    public void Apply(ToolCallDelta delta)
    {
        Status = delta.Status;

        if (IsPlaceholderToolName(Name) && !IsPlaceholderToolName(delta.Name))
            Name = delta.Name;

        // Deltas are partial. A tool surfaces across multiple phases: the call
        // phase carries the name + arguments, while a later result/echo phase may
        // omit them (Claude Code's tool_result echo has no tool name and sends
        // "tool" as a placeholder, with no input). Never let a blank or
        // placeholder value clobber the good value an earlier phase established.
        if (!string.IsNullOrWhiteSpace(delta.Label))
            Label = delta.Label!;
        else if (!string.IsNullOrWhiteSpace(delta.Name) && !IsPlaceholderToolName(delta.Name))
            Label = ToolLabelFor(delta.Name);

        if (!string.IsNullOrWhiteSpace(delta.Summary)) Summary = delta.Summary;
        if (!string.IsNullOrWhiteSpace(delta.Detail)) Detail = delta.Detail;

        var incomingArgs = string.IsNullOrWhiteSpace(delta.ArgumentsJson) && IsSearch
            ? BuildSearchArgumentsFromSummary(delta.Summary)
            : delta.ArgumentsJson;
        if (!string.IsNullOrWhiteSpace(incomingArgs))
            ArgumentsJson = incomingArgs;

        if (!string.IsNullOrWhiteSpace(delta.ResultPreviewJson))
            ResultPreviewJson = delta.ResultPreviewJson;

        if (!string.IsNullOrWhiteSpace(delta.Provider)) Provider = delta.Provider;
        RefreshComputed();
    }

    /// <summary>Placeholder tool names carried by phases that don't actually know
    /// the tool (Claude Code's tool_result echo sends "tool"). We must not let
    /// these overwrite the real label captured from the call phase.</summary>
    private static bool IsPlaceholderToolName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || string.Equals(name, "tool", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "unknown", StringComparison.OrdinalIgnoreCase);

    partial void OnStatusChanged(string value) => RefreshComputed();
    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(IsSearch));
        OnPropertyChanged(nameof(IsGenericTool));
        OnPropertyChanged(nameof(IsKnownBuiltInTool));
        OnPropertyChanged(nameof(ShowArgumentsFold));
        OnPropertyChanged(nameof(IsFileOperation));
        ArgsView = ToolArgsExtractor.Extract(value, ArgumentsJson);
        OnPropertyChanged(nameof(HeaderArgPreview));
        OnPropertyChanged(nameof(HasHeaderArgPreview));
    }
    partial void OnSummaryChanged(string? value) => OnPropertyChanged(nameof(HasSummary));
    partial void OnDetailChanged(string? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnArgumentsJsonChanged(string? value)
    {
        OnPropertyChanged(nameof(HasArguments));
        OnPropertyChanged(nameof(DisplayArgumentsJson));
        ArgsView = ToolArgsExtractor.Extract(Name, value);
        OnPropertyChanged(nameof(HeaderArgPreview));
        OnPropertyChanged(nameof(HasHeaderArgPreview));
    }
    partial void OnResultPreviewJsonChanged(string? value)
    {
        OnPropertyChanged(nameof(HasResultPreview));
        OnPropertyChanged(nameof(DisplayResultPreviewJson));
    }
    partial void OnProviderChanged(string? value) => OnPropertyChanged(nameof(HasProvider));

    private void RefreshComputed()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsError));
    }

    private static string ToolLabelFor(string name) => LabelFor(name);
    public static string LabelFor(string name) => name switch
    {
        "search_web" or "web_search" => "联网搜索",
        "web_fetch" or "steel_browser" => "网页阅读",
        "execute_python_code" => "Python",
        "read_file" => "读取文件",
        "glob_files" => "查找文件",
        "grep_files" => "搜索内容",
        "view_image" => "查看图片",
        "analyze_image" => "图片分析",
        "generate_image" => "生成图片",
        // Claude Code / Codex agent tools (PascalCase). Friendly labels so the
        // console cards read naturally instead of bare English tool names.
        "Read" => "读取文件",
        "Write" => "写入文件",
        "Edit" or "MultiEdit" => "编辑文件",
        "Bash" or "BashOutput" => "命令执行",
        "Glob" => "查找文件",
        "Grep" => "搜索内容",
        "Task" => "子任务",
        "WebFetch" => "网页阅读",
        "WebSearch" => "联网搜索",
        "TodoWrite" => "任务清单",
        "NotebookEdit" => "编辑笔记本",
        "apply_patch" => "应用补丁",
        _ => string.IsNullOrWhiteSpace(name) ? "工具调用" : name
    };

    private static string? BuildSearchArgumentsFromSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var queries = summary
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(query => new Dictionary<string, string> { ["query"] = query })
            .ToArray();
        return queries.Length == 0 ? null : JsonSerializer.Serialize(new Dictionary<string, object> { ["queries"] = queries });
    }

    private static string? FormatDisplayJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var displayRoot = DecodeJsonStringValues(doc.RootElement);
            return JsonSerializer.Serialize(displayRoot, DisplayJsonOptions);
        }
        catch (JsonException)
        {
            return DecodeUnicodeEscapes(json);
        }
    }

    private static JsonNode? DecodeJsonStringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    obj[property.Name] = DecodeJsonStringValues(property.Value);
                return obj;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    array.Add(DecodeJsonStringValues(item));
                return array;

            case JsonValueKind.String:
                return JsonValue.Create(DecodeUnicodeEscapes(element.GetString() ?? string.Empty));

            case JsonValueKind.Number:
                return JsonNode.Parse(element.GetRawText());

            case JsonValueKind.True:
                return JsonValue.Create(true);

            case JsonValueKind.False:
                return JsonValue.Create(false);

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    private static string DecodeUnicodeEscapes(string text)
    {
        if (string.IsNullOrEmpty(text)
            || (!text.Contains(@"\u", StringComparison.Ordinal)
                && !text.Contains(@"\U", StringComparison.Ordinal)))
        {
            return text;
        }

        return UnicodeEscapeRegex().Replace(text, match =>
        {
            var isLong = match.Groups["long"].Success;
            var hex = isLong ? match.Groups["long"].Value : match.Groups["short"].Value;
            try
            {
                var value = Convert.ToInt32(hex, 16);
                return isLong ? char.ConvertFromUtf32(value) : ((char)value).ToString();
            }
            catch (ArgumentException)
            {
                return match.Value;
            }
            catch (OverflowException)
            {
                return match.Value;
            }
            catch (FormatException)
            {
                return match.Value;
            }
        });
    }

    [GeneratedRegex(@"\\(?:u(?<short>[0-9a-fA-F]{4})|U(?<long>[0-9a-fA-F]{8}))", RegexOptions.CultureInvariant)]
    private static partial Regex UnicodeEscapeRegex();
}
