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
/// (the ViewModels project deliberately stays net8.0, not net8.0-windows,
/// so it doesn't depend on WPF's DispatcherTimer).
/// </summary>
public sealed partial class MessageViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan StreamFlushInterval = TimeSpan.FromMilliseconds(16);

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

    public bool HasThinking => !string.IsNullOrEmpty(Thinking);
    public bool HasActions => Role == "assistant" && !IsStreaming && !IsPending && !string.IsNullOrWhiteSpace(Content);
    public bool HasResponseStats => Usage is not null || !string.IsNullOrWhiteSpace(ModelLabel);
    public bool HasAttachments => Attachments is { Count: > 0 };
    public bool HasToolCalls => ToolCalls.Count > 0;
    public string VisibleContent => StripSystemHints(Content);
    public bool HasRetryBar => IsLatestAssistant && RetryAttempts is { Count: > 1 };
    public string RetryCounter => HasRetryBar ? $"{RetryCurrentIndex + 1}/{RetryAttempts!.Count}" : string.Empty;
    public string ResponseStatsText
    {
        get
        {
            var rows = new List<string>();
            if (!string.IsNullOrWhiteSpace(ModelLabel)) rows.Add($"使用模型：{ModelLabel}");
            if (Usage?.PromptTokens is { } prompt) rows.Add($"输入 Tokens：{prompt:N0}");
            if (Usage?.CompletionTokens is { } completion) rows.Add($"输出 Tokens：{completion:N0}");
            if (Usage?.TotalTokens is { } total) rows.Add($"总 Tokens：{total:N0}");
            return rows.Count == 0 ? "暂无响应统计" : string.Join("\n", rows);
        }
    }

    private DateTimeOffset? _thinkingStartedAt;
    private DateTimeOffset? _pendingStartedAt;
    private System.Threading.Timer? _elapsedTimer;
    private System.Threading.Timer? _pendingTimer;
    private System.Threading.Timer? _streamFlushTimer;
    private readonly System.Threading.Lock _streamLock = new();
    private readonly System.Text.StringBuilder _pendingDelta = new();
    private readonly SynchronizationContext? _syncContext;
    private ThinkingSegmentViewModel? _activeThinkingSegment;
    private int _nextDisplaySequence;
    private bool _disposed;
    private bool _streamFlushScheduled;

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
        StopPending();

        if (IsStreaming)
        {
            lock (_streamLock)
            {
                _pendingDelta.Append(delta);
                if (!_streamFlushScheduled)
                {
                    _streamFlushScheduled = true;
                    _streamFlushTimer ??= new System.Threading.Timer(_ => PostFlushPendingDeltaFrame(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _streamFlushTimer.Change(StreamFlushInterval, Timeout.InfiniteTimeSpan);
                }
            }
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

    /// <summary>
    /// Commit any queued streaming text immediately. Called before final
    /// markdown render and before persistence so the database never misses
    /// the tail that was waiting for the next UI frame.
    /// </summary>
    public void FlushPendingDelta()
    {
        if (_disposed) return;
        var pending = TakeAllPendingDelta();
        if (pending.Length > 0) Content += pending;
    }

    public void ReplaceContent(string text)
    {
        if (_disposed) return;
        FlushPendingDelta();
        Content = text;
    }

    public void FinishStreaming()
    {
        if (_disposed) return;
        FlushPendingDelta();
        IsStreaming = false;
    }

    private void PostFlushPendingDeltaFrame()
    {
        if (_disposed) return;
        if (_syncContext is not null) _syncContext.Post(_ => FlushPendingDeltaFrame(), null);
        else FlushPendingDeltaFrame();
    }

    private void FlushPendingDeltaFrame()
    {
        if (_disposed) return;
        var pending = TakeFramePendingDelta();
        if (pending.Length > 0) Content += pending;
    }

    private string TakeFramePendingDelta()
    {
        lock (_streamLock)
        {
            _streamFlushScheduled = false;
            if (_pendingDelta.Length == 0) return string.Empty;
            var take = Math.Min(GetAdaptiveStreamBatchSize(_pendingDelta.Length), _pendingDelta.Length);
            var pending = _pendingDelta.ToString(0, take);
            _pendingDelta.Remove(0, take);
            if (_pendingDelta.Length > 0 && !_disposed)
            {
                _streamFlushScheduled = true;
                _streamFlushTimer?.Change(StreamFlushInterval, Timeout.InfiniteTimeSpan);
            }
            return pending;
        }
    }

    private string TakeAllPendingDelta()
    {
        lock (_streamLock)
        {
            _streamFlushScheduled = false;
            _streamFlushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_pendingDelta.Length == 0) return string.Empty;
            var pending = _pendingDelta.ToString();
            _pendingDelta.Clear();
            return pending;
        }
    }

    private static int GetAdaptiveStreamBatchSize(int queuedChars)
    {
        if (queuedChars >= 4000) return 512;
        if (queuedChars >= 1600) return 256;
        if (queuedChars >= 700) return 160;
        if (queuedChars >= 240) return 96;
        if (queuedChars >= 80) return 48;
        return Math.Min(queuedChars, 24);
    }

    public void AppendThinking(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        StopPending();
        Thinking = (Thinking ?? string.Empty) + delta;
        OnPropertyChanged(nameof(HasThinking));

        if (_thinkingStartedAt is null)
        {
            _thinkingStartedAt = DateTimeOffset.UtcNow;
            ThinkingElapsedSeconds = 0;
            IsThinkingActive = true;
            _activeThinkingSegment = CreateThinkingSegment();
            EnsureElapsedTimer();
        }
        else if (_activeThinkingSegment is null)
        {
            _activeThinkingSegment = CreateThinkingSegment();
            _activeThinkingSegment.IsThinking = IsThinkingActive;
            _activeThinkingSegment.ElapsedSeconds = ThinkingElapsedSeconds;
        }

        _activeThinkingSegment.Append(delta);
        RebuildDisplayBlocks();
    }

    /// <summary>Freeze the elapsed counter and clear active state. Called
    /// when normal content starts arriving or when streaming finalizes.</summary>
    public void StopThinking()
    {
        FlushPendingDelta();
        if (_thinkingStartedAt is { } start)
            ThinkingElapsedSeconds = (DateTimeOffset.UtcNow - start).TotalSeconds;
        if (_activeThinkingSegment is { } segment)
        {
            segment.ElapsedSeconds = ThinkingElapsedSeconds;
            segment.IsThinking = false;
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
            attempts.Add(new MessageAttempt(Content, ModelLabel, Usage, Sources));

        Content = string.Empty;
        Thinking = null;
        ThinkingSegments.Clear();
        ToolCalls.Clear();
        DisplayBlocks.Clear();
        _activeThinkingSegment = null;
        _thinkingStartedAt = null;
        ThinkingElapsedSeconds = 0;
        Usage = null;
        Sources = null;
        RetryAttempts = attempts;
        RetryCurrentIndex = attempts.Count - 1;
    }

    public void CommitRetryAttempt()
    {
        var attempts = RetryAttempts?.ToList() ?? new List<MessageAttempt>();
        attempts.Add(new MessageAttempt(Content, ModelLabel, Usage, Sources));
        RetryAttempts = attempts;
        RetryCurrentIndex = attempts.Count - 1;
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

        Content = attempt.Content;
        ModelLabel = attempt.ModelLabel;
        Usage = attempt.Usage;
        Sources = attempt.Sources;
        RetryCurrentIndex = index;
    }

    public void StartPending(bool routes)
    {
        if (_disposed) return;
        IsRoutesPending = routes;
        _pendingStartedAt = DateTimeOffset.UtcNow;
        IsPending = true;
        UpdatePendingCopy();
        _pendingTimer ??= new System.Threading.Timer(_ => PostUpdatePendingCopy(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pendingTimer.Change(TimeSpan.FromMilliseconds(650), TimeSpan.FromMilliseconds(650));
    }

    public void SetPendingStatus(string label, string? detail = null, bool? routes = null)
    {
        if (_disposed) return;
        if (routes is { } value) IsRoutesPending = value;
        PendingLabel = label;
        PendingDetail = detail;
        if (IsPending) return;

        _pendingStartedAt = DateTimeOffset.UtcNow;
        IsPending = true;
        _pendingTimer ??= new System.Threading.Timer(_ => PostUpdatePendingCopy(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pendingTimer.Change(TimeSpan.FromMilliseconds(650), TimeSpan.FromMilliseconds(650));
    }

    public void ApplyToolDelta(ToolCallDelta delta)
    {
        if (_disposed) return;
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
        var elapsed = DateTimeOffset.UtcNow - _pendingStartedAt.Value;

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
    partial void OnAttachmentsChanged(IReadOnlyList<AttachmentChip>? value) => OnPropertyChanged(nameof(HasAttachments));
    partial void OnUsageChanged(Usage? value)
    {
        OnPropertyChanged(nameof(HasResponseStats));
        OnPropertyChanged(nameof(ResponseStatsText));
    }
    partial void OnModelLabelChanged(string? value)
    {
        OnPropertyChanged(nameof(HasResponseStats));
        OnPropertyChanged(nameof(ResponseStatsText));
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
    }

    private void RebuildDisplayBlocks()
    {
        var next = new List<MessageDisplayBlockViewModel>();
        var content = Content ?? string.Empty;
        var cursor = 0;
        foreach (var tool in ToolCalls
                     .Cast<object>()
                     .Concat(ThinkingSegments)
                     .OrderBy(GetBlockContentOffset)
                     .ThenBy(GetBlockTimelineIndex))
        {
            var offset = Math.Clamp(GetBlockContentOffset(tool), cursor, content.Length);
            if (offset > cursor)
                next.Add(MessageDisplayBlockViewModel.ForText(content[cursor..offset]));
            if (tool is ToolCallViewModel toolCall)
                next.Add(MessageDisplayBlockViewModel.ForTool(toolCall));
            else if (tool is ThinkingSegmentViewModel thinking && IsThinkingSegmentVisible(thinking))
                next.Add(MessageDisplayBlockViewModel.ForThinking(thinking));
            cursor = offset;
        }

        if (cursor < content.Length)
            next.Add(MessageDisplayBlockViewModel.ForText(content[cursor..]));

        SyncDisplayBlocks(next);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;
        _pendingTimer?.Dispose();
        _pendingTimer = null;
        _streamFlushTimer?.Dispose();
        _streamFlushTimer = null;
    }
}

public sealed record MessageAttempt(string Content, string? ModelLabel, Usage? Usage, IReadOnlyList<SourceReference>? Sources);
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

    /// <summary>Relative file name in the local AttachmentStore (BYOK images).
    /// Null for MolaGPT-account images (which use <see cref="ThumbnailUrl"/>).</summary>
    public string? LocalName { get; init; }

    /// <summary>MIME type, persisted so reloaded bytes can be re-encoded as a
    /// data URL for the wire without re-sniffing.</summary>
    public string? MimeType { get; init; }

    public bool IsImage =>
        Bytes is { Length: > 0 }
        || !string.IsNullOrEmpty(LocalName)
        || !string.IsNullOrEmpty(ThumbnailUrl)
        || string.Equals(Label, "图片", StringComparison.Ordinal);

    public bool HasInlinePreview =>
        Bytes is { Length: > 0 }
        || !string.IsNullOrEmpty(LocalName)
        || !string.IsNullOrEmpty(ThumbnailUrl);
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
    public ThinkingSegmentViewModel? Thinking { get; }
    public bool IsText => Text is { Length: > 0 };
    public bool IsTool => Tool is not null;
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

        if (Thinking is not null && ReferenceEquals(Thinking, next.Thinking))
            return true;

        return false;
    }

    public static MessageDisplayBlockViewModel ForText(string text) => new(text, null, null);
    public static MessageDisplayBlockViewModel ForTool(ToolCallViewModel tool) => new(null, tool, null);
    public static MessageDisplayBlockViewModel ForThinking(ThinkingSegmentViewModel thinking) => new(null, null, thinking);
}

public sealed partial class ThinkingSegmentViewModel : ObservableObject
{
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private bool _isThinking;
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
    public string Name { get; }
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
        Name = name;
        _label = ToolLabelFor(name);
    }

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasProvider => !string.IsNullOrWhiteSpace(Provider);
    public bool HasArguments => !string.IsNullOrWhiteSpace(ArgumentsJson);
    public bool HasResultPreview => !string.IsNullOrWhiteSpace(ResultPreviewJson);
    public string? DisplayArgumentsJson => FormatDisplayJson(ArgumentsJson);
    public string? DisplayResultPreviewJson => FormatDisplayJson(ResultPreviewJson);
    public bool IsCompleted => Status.Equals("completed", StringComparison.OrdinalIgnoreCase);
    public bool IsError => Status.Equals("error", StringComparison.OrdinalIgnoreCase);
    public bool IsSearch => Name.Equals("search_web", StringComparison.OrdinalIgnoreCase)
                            || Name.Equals("web_search", StringComparison.OrdinalIgnoreCase);
    public bool IsGenericTool => !IsSearch;
    public string IconGlyph => Name switch
    {
        "search_web" or "web_search" => "\uE721",
        "web_fetch" or "steel_browser" => "\uE774",
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
        Label = string.IsNullOrWhiteSpace(delta.Label) ? ToolLabelFor(delta.Name) : delta.Label!;
        Summary = delta.Summary;
        Detail = delta.Detail;
        ArgumentsJson = string.IsNullOrWhiteSpace(delta.ArgumentsJson) && IsSearch
            ? BuildSearchArgumentsFromSummary(delta.Summary)
            : delta.ArgumentsJson;
        ResultPreviewJson = delta.ResultPreviewJson;
        Provider = delta.Provider;
        RefreshComputed();
    }

    partial void OnStatusChanged(string value) => RefreshComputed();
    partial void OnSummaryChanged(string? value) => OnPropertyChanged(nameof(HasSummary));
    partial void OnDetailChanged(string? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnArgumentsJsonChanged(string? value)
    {
        OnPropertyChanged(nameof(HasArguments));
        OnPropertyChanged(nameof(DisplayArgumentsJson));
        ArgsView = ToolArgsExtractor.Extract(Name, value);
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

    private static string ToolLabelFor(string name) => name switch
    {
        "search_web" or "web_search" => "联网搜索",
        "web_fetch" or "steel_browser" => "网页阅读",
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
