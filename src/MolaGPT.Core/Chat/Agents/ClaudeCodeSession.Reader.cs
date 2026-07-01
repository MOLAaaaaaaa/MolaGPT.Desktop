using System.Text.Json;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// stdout read loop + stream-json → <see cref="AgentEvent"/> mapping for
/// <see cref="ClaudeCodeSession"/>. See Claude Code's stream-json schema:
///   - {"type":"system","subtype":"init", ...}        session boot
///   - {"type":"stream_event","event":{...}}          partial Anthropic SSE events (text/thinking deltas)
///   - {"type":"assistant","message":{content:[...]}} a complete assistant message (tool_use blocks)
///   - {"type":"user","message":{content:[...]}}      tool_result echoes
///   - {"type":"result","subtype":"success", ...}     end of a turn (+ usage)
/// Text is taken from stream_event deltas; tool calls from the assistant message
/// (avoids double-emitting the same text that the complete message repeats).
/// </summary>
internal sealed partial class ClaudeCodeSession
{
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            var stdout = _process.StandardOutput.BaseStream;
            await foreach (var line in NdJsonStreamReader.ReadAsync(stdout, ct).ConfigureAwait(false))
            {
                if (!line.IsValid)
                    continue; // tolerate stray non-JSON lines

                foreach (var ev in MapLine(line.Root, line.Raw))
                    await _channel.Writer.WriteAsync(ev, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _channel.Writer.TryWrite(AgentEvent.Failure($"Claude Code stream error: {ex.Message}"));
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private IEnumerable<AgentEvent> MapLine(JsonElement root, string raw)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "stream_event":
                if (root.TryGetProperty("event", out var inner))
                {
                    var ev = MapStreamEvent(inner, raw);
                    if (ev is not null) yield return ev;
                }
                break;

            case "assistant":
                foreach (var ev in MapAssistantMessage(root, raw))
                    yield return ev;
                break;

            case "user":
                foreach (var ev in MapToolResults(root, raw))
                    yield return ev;
                break;

            case "control_request":
                // stream-json permission protocol: a tool needs approval. Record
                // the pending request (keyed by request_id) so ApproveAsync can
                // reply with a matching control_response, and surface it as a
                // normalized PermissionRequest (same shape Codex emits).
                if (MapControlRequest(root, raw) is { } perm)
                    yield return perm;
                break;

            case "control_response":
                // Reply to a control_request WE sent (initialize / set_model).
                // Completes the matching waiter; emits no user-visible event.
                CompleteControlResponse(root);
                break;

            case "result":
                yield return AgentEvent.Complete(ExtractUsage(root), raw);
                break;

            case "system":
                // init carries Claude's own session_id — capture it so the bridge
                // can resume the correct conversation after a respawn.
                if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                {
                    var id = sid.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) _currentSessionId = id;
                }
                break;
        }
    }

    private AgentEvent? MapControlRequest(JsonElement root, string raw)
    {
        if (!root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
            return null;
        var subtype = request.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype != "can_use_tool")
            return null; // other control subtypes (e.g. responses to our interrupt) aren't approvals

        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        if (string.IsNullOrEmpty(requestId))
            return null;

        var toolName = request.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
        var description = request.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        var input = request.TryGetProperty("input", out var inp) ? inp : default;

        // Stash the original input so an "allow" can echo it back as updatedInput.
        _pendingApprovals[requestId] = input.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { })
            : input.Clone();

        return new AgentEvent(
            AgentEventKind.PermissionRequest,
            Permission: new AgentPermissionRequest(
                requestId,
                toolName ?? "tool",
                description ?? toolName,
                input.ValueKind == JsonValueKind.Undefined ? null : input.GetRawText()),
            RawJson: raw);
    }

    private static AgentEvent? MapStreamEvent(JsonElement evt, string raw)
    {
        var etype = evt.TryGetProperty("type", out var et) ? et.GetString() : null;
        if (etype != "content_block_delta") return null;
        if (!evt.TryGetProperty("delta", out var delta)) return null;

        var dtype = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
        return dtype switch
        {
            "text_delta" when delta.TryGetProperty("text", out var txt)
                => AgentEvent.TextDelta(txt.GetString() ?? string.Empty, raw),
            "thinking_delta" when delta.TryGetProperty("thinking", out var th)
                => AgentEvent.ThinkingDelta(th.GetString() ?? string.Empty, raw),
            _ => null
        };
    }

    private static IEnumerable<AgentEvent> MapAssistantMessage(JsonElement root, string raw)
    {
        if (!root.TryGetProperty("message", out var msg)) yield break;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var block in content.EnumerateArray())
        {
            var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (btype != "tool_use") continue;

            var id = block.TryGetProperty("id", out var bid) ? bid.GetString() : null;
            var name = block.TryGetProperty("name", out var bn) ? bn.GetString() : null;
            var argsJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : null;

            yield return new AgentEvent(
                AgentEventKind.ToolCall,
                Tool: new AgentToolEvent(
                    id ?? Guid.NewGuid().ToString("N"),
                    name ?? "tool",
                    AgentToolStatus.Started,
                    Title: name,
                    ArgumentsJson: argsJson),
                RawJson: raw);
        }
    }

    private static IEnumerable<AgentEvent> MapToolResults(JsonElement root, string raw)
    {
        if (!root.TryGetProperty("message", out var msg)) yield break;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var block in content.EnumerateArray())
        {
            var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (btype != "tool_result") continue;

            var id = block.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null;
            var isError = block.TryGetProperty("is_error", out var er) && er.ValueKind == JsonValueKind.True;
            string? preview = block.TryGetProperty("content", out var c)
                ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText())
                : null;
            if (preview is { Length: > 500 }) preview = preview[..500] + "…";

            yield return new AgentEvent(
                AgentEventKind.ToolCall,
                Tool: new AgentToolEvent(
                    id ?? Guid.NewGuid().ToString("N"),
                    "tool",
                    isError ? AgentToolStatus.Failed : AgentToolStatus.Completed,
                    ResultPreview: preview),
                RawJson: raw);
        }
    }

    private static AgentUsage? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        int? input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv) ? iv : null;
        int? output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov) ? ov : null;
        if (input is null && output is null) return null;
        return new AgentUsage(input, output, (input ?? 0) + (output ?? 0));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (!SafeHasExited())
            {
                try { _process.StandardInput.Close(); } catch { /* ignore */ }
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* best-effort teardown */ }

        try { await _readerTask.ConfigureAwait(false); } catch { /* ignore */ }

        _channel.Writer.TryComplete();
        _lifetimeCts.Dispose();
        _turnGate.Dispose();
        _writeGate.Dispose();
        _process.Dispose();
    }
}
