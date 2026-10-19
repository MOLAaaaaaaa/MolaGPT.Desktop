using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;

namespace MolaGPT.ViewModels;

public sealed partial class ChatViewModel
{
    private readonly Lock _messageWriteGate = new();
    private Task _messageWriteTask = Task.CompletedTask;

    private Task QueueMessageWrite(string messageId, MessagePersistenceSnapshot snapshot)
    {
        var repository = _messageRepo!;
        return QueueMessageWrite(() => repository.Update(
            messageId, snapshot.CurrentAttempt.Content, BuildMessageMeta(snapshot)));
    }

    private Task QueueMessageWrite(Action write)
    {
        lock (_messageWriteGate)
        {
            // Continue on the thread pool even when the preceding write has already
            // completed. The queued work reads only the captured state, never the VM.
            _messageWriteTask = _messageWriteTask.ContinueWith(
                _ => write(),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _messageWriteTask;
        }
    }

    private static MessagePersistenceSnapshot CapturePersistenceSnapshot(MessageViewModel vm)
    {
        var current = vm.CaptureRetryAttempt(includePendingThinking: true);
        current = current with { Sources = current.Sources?.ToArray() };
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
            vm.CompactionReason);
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
        {
            meta["tool_calls"] = new JsonArray(snapshot.CurrentAttempt.ToolCalls
                .Select(t => (JsonNode)new JsonObject
                {
                    ["id"] = t.Id,
                    ["name"] = t.Name,
                    ["status"] = t.Status,
                    ["label"] = t.Label,
                    ["summary"] = t.Summary,
                    ["detail"] = t.Detail,
                    ["arguments_json"] = t.ArgumentsJson,
                    ["result_preview_json"] = t.ResultPreviewJson,
                    ["provider"] = t.Provider,
                    ["content_offset"] = t.ContentOffset,
                    ["timeline_index"] = t.TimelineIndex,
                    ["background_task_id"] = t.BackgroundTaskId,
                    ["task_state"] = t.TaskState
                })
                .ToArray());
        }
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

        return meta.ToJsonString();
    }

    private static MessageAttempt CloneAttempt(MessageAttempt attempt) => attempt with
    {
        Sources = attempt.Sources?.ToArray(),
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
        string? CompactionReason);
}
