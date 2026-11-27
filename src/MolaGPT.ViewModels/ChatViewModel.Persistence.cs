using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels;

public sealed partial class ChatViewModel
{
    private static readonly JsonSerializerOptions MessageMetaJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Lock _messageWriteGate = new();
    private readonly Dictionary<string, Task> _messageWrites = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MessagePersistenceSnapshot> _unsavedMessages = new();

    internal bool HasUnsavedMessages(string conversationId) =>
        _unsavedMessages.Values.Any(message => message.ConversationId == conversationId);

    private void WriteMessage(string messageId, MessagePersistenceSnapshot snapshot)
    {
        var initial = snapshot.InitialRow ?? _unsavedMessages.GetValueOrDefault(messageId)?.InitialRow;
        try
        {
            var content = snapshot.CurrentAttempt.Content;
            var meta = BuildMessageMeta(snapshot);
            if (initial is not null)
                _messageRepo!.Insert(initial with { Content = content, Meta = meta });
            else if (!_messageRepo!.Update(messageId, content, meta))
                throw new InvalidOperationException("消息已不存在，无法保存回答。");
            _unsavedMessages.TryRemove(messageId, out _);
        }
        catch
        {
            _unsavedMessages[messageId] = snapshot with { InitialRow = initial };
            throw;
        }
    }

    private Task QueueMessageWrite(string messageId, MessagePersistenceSnapshot snapshot) =>
        QueueMessageWrite(snapshot.ConversationId!, () => WriteMessage(messageId, snapshot));

    private Task QueueMessageWrite(string conversationId, Action write)
    {
        lock (_messageWriteGate)
        {
            // Continue on the thread pool even when the preceding write has already
            // completed. The queued work reads only the captured state, never the VM.
            var prior = _messageWrites.GetValueOrDefault(conversationId) ?? Task.CompletedTask;
            var result = prior.ContinueWith(
                _ => write(),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _messageWrites[conversationId] = result;
            _ = result.ContinueWith(finished =>
            {
                _ = finished.Exception;
                lock (_messageWriteGate)
                    if (_messageWrites.GetValueOrDefault(conversationId) == finished)
                        _messageWrites.Remove(conversationId);
            },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return result;
        }
    }

    internal async Task<bool> TrySaveAsync(Func<Task> save)
    {
        try { await save().ConfigureAwait(false); return true; }
        catch (Exception ex) { HistorySaveFailed?.Invoke(ex.Message); return false; }
    }

    internal async Task RetryUnsavedMessagesAsync()
    {
        Task[] pending;
        lock (_messageWriteGate) pending = _messageWrites.Values.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var (id, message) in _unsavedMessages.OrderBy(pair => pair.Value.InitialRow?.CreatedAt))
            await TrySaveAsync(() => QueueMessageWrite(message.ConversationId!, () =>
            {
                if (_unsavedMessages.TryGetValue(id, out var snapshot)) WriteMessage(id, snapshot);
            })).ConfigureAwait(false);
    }

    private static MessagePersistenceSnapshot CapturePersistenceSnapshot(MessageViewModel vm)
    {
        var current = vm.CaptureRetryAttempt(includePendingThinking: true);
        current = current with { Sources = current.Sources?.ToArray(), Subagents = (JsonObject?)current.Subagents?.DeepClone() };
        return new MessagePersistenceSnapshot(
            current,
            vm.ProviderLabel,
            vm.IsStreaming,
            vm.ArtifactRefs?.ToArray(),
            vm.Attachments?.Select(attachment => attachment with { Bytes = null }).ToArray(),
            vm.ContentPartsJson,
            vm.RetryAttempts?.Select(CloneAttempt).ToArray(),
            vm.RetryCurrentIndex,
            vm.ContextTokens,
            vm.ContextWindow,
            vm.CompactionTokensBefore,
            vm.CompactionTokensAfter,
            vm.CompactionReason,
            vm.RequestError);
    }

    private static string BuildMessageMeta(MessagePersistenceSnapshot snapshot)
    {
        var current = snapshot.CurrentAttempt;
        var meta = new JsonObject();
        if (!string.IsNullOrWhiteSpace(current.ModelLabel)) meta["model"] = current.ModelLabel;
        if (!string.IsNullOrWhiteSpace(snapshot.ProviderLabel)) meta["provider"] = snapshot.ProviderLabel;
        if (!string.IsNullOrWhiteSpace(current.Thinking)) meta["thinking"] = current.Thinking;
        if (!string.IsNullOrEmpty(current.PersonaId)) meta["persona_id"] = current.PersonaId;
        if (!string.IsNullOrEmpty(current.PersonaName)) meta["persona_name"] = current.PersonaName;
        if (current.WasStopped) meta["stopped"] = true;
        if (snapshot.IsStreaming) meta["streaming"] = true;
        if (snapshot.Error is not null) meta["error"] = JsonSerializer.SerializeToNode(snapshot.Error, MessageMetaJson);
        if (current.FinishReason is { Length: > 0 } finish && finish != "stop")
            meta["finish_reason"] = finish;
        if (current.Usage is not null) meta["response_stats"] = BuildUsageJson(current.Usage);
        if (BuildTurnTimingJson(snapshot.CurrentAttempt.FirstTokenSeconds, snapshot.CurrentAttempt.GenerationSeconds)
            is { } timing)
            meta["turn_timing"] = timing;
        if (snapshot.ContextTokens > 0) meta["context_tokens"] = snapshot.ContextTokens;
        if (snapshot.ContextWindow > 0) meta["context_window"] = snapshot.ContextWindow;
        if (snapshot.CompactionTokensBefore > 0)
            meta["compaction_tokens_before"] = snapshot.CompactionTokensBefore;
        if (snapshot.CompactionTokensAfter > 0)
            meta["compaction_tokens_after"] = snapshot.CompactionTokensAfter;
        if (!string.IsNullOrWhiteSpace(snapshot.CompactionReason))
            meta["compaction_reason"] = snapshot.CompactionReason;

        if (current.Subagents is not null) meta["subagents"] = current.Subagents.DeepClone();
        if (current.Sources is { Count: > 0 })
            meta["sources"] = BuildSourcesJson(current.Sources);
        if (snapshot.ArtifactRefs is { Count: > 0 })
        {
            meta["artifact_refs"] = new JsonArray(snapshot.ArtifactRefs
                .Select(r => (JsonNode)new JsonObject
                {
                    ["title"] = r.Title,
                    ["kind"] = r.Badge,
                    ["version"] = r.Version,
                    ["versions"] = r.VersionCount
                })
                .ToArray());
        }
        if (snapshot.Attachments is { Count: > 0 })
        {
            meta["attachments"] = new JsonArray(snapshot.Attachments
                .Select(a =>
                {
                    var obj = new JsonObject
                    {
                        ["filename"] = a.FileName,
                        ["label"] = a.Label
                    };
                    if (!string.IsNullOrWhiteSpace(a.ThumbnailUrl)) obj["thumbnailUrl"] = a.ThumbnailUrl;
                    if (!string.IsNullOrWhiteSpace(a.LocalName)) obj["localName"] = a.LocalName;
                    if (!string.IsNullOrWhiteSpace(a.MimeType)) obj["mime"] = a.MimeType;
                    if (a.Kind is { } kind)
                        obj["kind"] = kind == AttachmentKind.Image ? "image" : "file";
                    if (!string.IsNullOrWhiteSpace(a.WorkspacePath)) obj["workspacePath"] = a.WorkspacePath;
                    if (!string.IsNullOrWhiteSpace(a.ExtractedTextPath)) obj["textPath"] = a.ExtractedTextPath;
                    return (JsonNode)obj;
                })
                .ToArray());
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ContentPartsJson))
        {
            try
            {
                if (JsonNode.Parse(snapshot.ContentPartsJson) is { } parts)
                    meta["content_parts"] = parts;
            }
            catch (JsonException) { }
        }

        var retryAttempts = snapshot.RetryAttempts;
        var retryCurrent = snapshot.RetryCurrentIndex;
        if (snapshot.IsStreaming && retryAttempts is { Count: > 0 })
        {
            var interrupted = snapshot.CurrentAttempt with
            {
                WasStopped = true,
                ToolCalls = snapshot.CurrentAttempt.ToolCalls?.Select(tool =>
                    tool.Status is "preparing" or "running"
                        ? tool with { Status = "cancelled" }
                        : tool).ToArray()
            };
            retryAttempts = [.. retryAttempts, interrupted];
            retryCurrent = retryAttempts.Count - 1;
        }
        if (retryAttempts is { Count: > 1 })
        {
            meta["retry"] = new JsonObject
            {
                ["current"] = retryCurrent,
                ["attempts"] = new JsonArray(retryAttempts.Select(BuildRetryAttemptJson).ToArray())
            };
        }

        if (snapshot.CurrentAttempt.ToolCalls is { Count: > 0 })
            meta["tool_calls"] = BuildToolCallsJson(snapshot.CurrentAttempt.ToolCalls);
        if (snapshot.CurrentAttempt.ThinkingSegments is { Count: > 0 })
        {
            meta["thinking_segments"] = new JsonArray(snapshot.CurrentAttempt.ThinkingSegments
                .Where(t => !string.IsNullOrWhiteSpace(t.Source))
                .Select(t => (JsonNode)new JsonObject
                {
                    ["source"] = t.Source,
                    ["content_offset"] = t.ContentOffset,
                    ["timeline_index"] = t.TimelineIndex,
                    ["elapsed_seconds"] = t.ElapsedSeconds
                })
                .ToArray());
        }

        return meta.ToJsonString(MessageMetaJson);
    }

    private static MessageAttempt CloneAttempt(MessageAttempt attempt) => attempt with
    {
        Sources = attempt.Sources?.ToArray(),
        Subagents = (JsonObject?)attempt.Subagents?.DeepClone(),
        ThinkingSegments = attempt.ThinkingSegments?.ToArray(),
        ToolCalls = attempt.ToolCalls?.ToArray()
    };

    private sealed record MessagePersistenceSnapshot(
        MessageAttempt CurrentAttempt,
        string? ProviderLabel,
        bool IsStreaming,
        IReadOnlyList<ArtifactRefChip>? ArtifactRefs,
        IReadOnlyList<AttachmentChip>? Attachments,
        string? ContentPartsJson,
        IReadOnlyList<MessageAttempt>? RetryAttempts,
        int RetryCurrentIndex,
        int ContextTokens,
        int ContextWindow,
        int CompactionTokensBefore,
        int CompactionTokensAfter,
        string? CompactionReason,
        ChatRequestError? Error,
        MessageRow? InitialRow = null,
        string? ConversationId = null);
}
