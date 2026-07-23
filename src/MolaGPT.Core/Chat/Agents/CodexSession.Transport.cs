using System.Text.Json;
using System.Threading.Channels;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// JSON-RPC transport + notification → <see cref="AgentEvent"/> mapping for
/// <see cref="CodexSession"/>. Streaming notifications of interest:
///   - item/agentMessage/delta  → assistant text delta
///   - item/reasoning/delta     → thinking delta
///   - item/started / item/completed (command/fileChange) → tool lifecycle
///   - turn/completed           → end of turn (+ usage)
/// </summary>
internal sealed partial class CodexSession
{
    private async Task<JsonElement> RequestAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate) _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new { id, method, @params });
        await WriteLineAsync(payload, ct).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task NotifyAsync(string method, object @params, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { method, @params });
        await WriteLineAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task RespondAsync(JsonElement id, object result, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { id, result });
        await WriteLineAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string payload, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            var stdout = _process.StandardOutput.BaseStream;
            await foreach (var line in NdJsonStreamReader.ReadAsync(stdout, ct).ConfigureAwait(false))
            {
                if (!line.IsValid) continue;
                Dispatch(line.Root, line.Raw);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _turnChannel?.Writer.TryWrite(AgentEvent.Failure($"Codex stream error: {ex.Message}"));
        }
        finally
        {
            // Fail any in-flight request and close the active turn.
            lock (_pendingGate)
            {
                foreach (var tcs in _pending.Values)
                    tcs.TrySetException(new IOException("Codex app-server connection closed."));
                _pending.Clear();
            }
            _turnChannel?.Writer.TryComplete();
        }
    }

    private void Dispatch(JsonElement root, string raw)
    {
        // Responses carry "id" without "method".
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)
            && !root.TryGetProperty("method", out _))
        {
            TaskCompletionSource<JsonElement>? tcs;
            lock (_pendingGate)
            {
                _pending.Remove(id, out tcs);
            }
            if (tcs is null) return;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "JSON-RPC error";
                tcs.TrySetException(new InvalidOperationException(msg ?? "JSON-RPC error"));
            }
            else
            {
                tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
            }
            return;
        }

        // Server-initiated requests carry both "id" and "method". Approval
        // requests must be answered later with the same id.
        var method = root.TryGetProperty("method", out var me) ? me.GetString() : null;
        if (method is null) return;

        if (root.TryGetProperty("id", out var serverId))
        {
            var serverParams = root.TryGetProperty("params", out var serverParamsEl) ? serverParamsEl : default;
            HandleServerRequest(serverId.Clone(), method, serverParams, raw);
            return;
        }

        if (method == "serverRequest/resolved")
            RemoveResolvedServerRequest(root);

        var channel = _turnChannel;
        if (channel is null) return;

        var prms = root.TryGetProperty("params", out var p) ? p : default;
        var ev = MapNotification(method, prms, raw);
        if (ev is not null)
            channel.Writer.TryWrite(ev);
    }

    private void HandleServerRequest(JsonElement id, string method, JsonElement prms, string raw)
    {
        if (!IsApprovalMethod(method))
        {
            _ = RespondAsync(id, new { error = "unsupported" }, _lifetimeCts.Token);
            return;
        }

        var permissionId = RequestIdKey(id);
        _pendingApprovals[permissionId] = new PendingApprovalRequest(id, method, prms.Clone());

        var channel = _turnChannel;
        if (channel is null)
            return;

        channel.Writer.TryWrite(new AgentEvent(
            AgentEventKind.PermissionRequest,
            Permission: new AgentPermissionRequest(
                permissionId,
                ApprovalToolName(method),
                ApprovalDescription(method, prms),
                prms.ValueKind == JsonValueKind.Undefined ? null : prms.GetRawText()),
            RawJson: raw));
    }

    private void RemoveResolvedServerRequest(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var prms) || prms.ValueKind != JsonValueKind.Object)
            return;
        if (!prms.TryGetProperty("requestId", out var rid))
            return;
        _pendingApprovals.TryRemove(RequestIdKey(rid), out _);
    }

    private static bool IsApprovalMethod(string method)
        => method is "item/commandExecution/requestApproval"
            or "item/fileChange/requestApproval"
            or "item/permissions/requestApproval"
            or "execCommandApproval"
            or "applyPatchApproval";

    private static string RequestIdKey(JsonElement id)
        => id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : id.GetRawText();

    private static string ApprovalToolName(string method) => method switch
    {
        "item/commandExecution/requestApproval" or "execCommandApproval" => "commandExecution",
        "item/fileChange/requestApproval" or "applyPatchApproval" => "fileChange",
        "item/permissions/requestApproval" => "permissions",
        _ => "approval"
    };

    private static string? ApprovalDescription(string method, JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object)
            return method;

        var reason = ReadString(prms, "reason");
        var command = ReadString(prms, "command");
        if (command is null && prms.TryGetProperty("command", out var commandArray) && commandArray.ValueKind == JsonValueKind.Array)
            command = string.Join(" ", commandArray.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        var cwd = ReadString(prms, "cwd");
        var grantRoot = ReadString(prms, "grantRoot");

        var parts = new[] { reason, command, grantRoot is null ? null : $"grant root: {grantRoot}", cwd is null ? null : $"cwd: {cwd}" }
            .Where(x => !string.IsNullOrWhiteSpace(x));
        var detail = string.Join(Environment.NewLine, parts);
        return detail.Length == 0 ? method : detail;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object BuildApprovalResult(string method, JsonElement prms, AgentPermissionChoice choice)
    {
        if (method is "execCommandApproval" or "applyPatchApproval")
        {
            var decision = choice switch
            {
                AgentPermissionChoice.Always => "approved_for_session",
                AgentPermissionChoice.Deny => "denied",
                _ => "approved"
            };
            return new { decision };
        }

        if (method == "item/permissions/requestApproval")
        {
            var permissions = choice == AgentPermissionChoice.Deny
                ? new Dictionary<string, object?>()
                : GrantedPermissions(prms);
            var scope = choice == AgentPermissionChoice.Always ? "session" : "turn";
            return new { permissions, scope };
        }

        var v2Decision = choice switch
        {
            AgentPermissionChoice.Always => "acceptForSession",
            AgentPermissionChoice.Deny => "decline",
            _ => "accept"
        };
        return new { decision = v2Decision };
    }

    private static Dictionary<string, object?> GrantedPermissions(JsonElement prms)
    {
        var granted = new Dictionary<string, object?>();
        if (prms.ValueKind != JsonValueKind.Object
            || !prms.TryGetProperty("permissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Object)
            return granted;

        if (permissions.TryGetProperty("network", out var network) && network.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            granted["network"] = network.Clone();
        if (permissions.TryGetProperty("fileSystem", out var fileSystem) && fileSystem.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            granted["fileSystem"] = fileSystem.Clone();
        return granted;
    }

    private static AgentEvent? MapNotification(string method, JsonElement prms, string raw)
    {
        switch (method)
        {
            case "item/agentMessage/delta":
                return AgentEvent.TextDelta(ExtractDeltaText(prms), raw);

            case "item/reasoning/delta":
                return AgentEvent.ThinkingDelta(ExtractDeltaText(prms), raw);

            case "item/started":
                return MapItemLifecycle(prms, raw, AgentToolStatus.Started);

            case "item/completed":
                return MapItemLifecycle(prms, raw, AgentToolStatus.Completed);

            case "turn/completed":
                return AgentEvent.Complete(ExtractUsage(prms), raw);

            case "turn/failed":
                return AgentEvent.Failure(ExtractError(prms) ?? "Codex turn failed.", raw);

            default:
                return null;
        }
    }

    private static string ExtractDeltaText(JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object) return string.Empty;
        if (prms.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
            return d.GetString() ?? string.Empty;
        if (prms.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() ?? string.Empty;
        return string.Empty;
    }

    internal static AgentEvent? MapItemLifecycle(JsonElement prms, string raw, AgentToolStatus status)
    {
        if (prms.ValueKind != JsonValueKind.Object || !prms.TryGetProperty("item", out var item))
            return null;

        var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : visibleType(item);
        if (string.IsNullOrEmpty(itemType)) return null;

        // Deny-list, not allow-list: plain conversation items already stream as
        // deltas, and EVERYTHING else app-server reports as an item is a tool the
        // user should see. An allow-list silently dropped tool types Codex added
        // later — `webSearch` was invisible on the phone for exactly this reason,
        // so a turn that searched the web showed the answer with no tool card.
        if (itemType is "userMessage" or "agentMessage" or "reasoning" or "todoList" or "error")
            return null;

        var id = item.TryGetProperty("id", out var iid) ? iid.GetString() : null;
        var argsJson = ExtractItemArguments(item);
        var resultPreview = status == AgentToolStatus.Completed ? ExtractItemResult(item) : null;
        var failed = item.TryGetProperty("status", out var st)
            && st.ValueKind == JsonValueKind.String
            && string.Equals(st.GetString(), "failed", StringComparison.OrdinalIgnoreCase);

        return new AgentEvent(
            AgentEventKind.ToolCall,
            Tool: new AgentToolEvent(
                id ?? Guid.NewGuid().ToString("N"),
                itemType,
                failed ? AgentToolStatus.Failed : status,
                Title: itemType,
                ArgumentsJson: argsJson,
                ResultPreview: resultPreview),
            RawJson: raw);

        static string? visibleType(JsonElement item)
            => item.TryGetProperty("itemType", out var x) ? x.GetString() : null;
    }

    /// <summary>Best-effort "what is this tool doing" payload. Shapes differ per
    /// item type (commandExecution.command, webSearch.query/action, mcpToolCall.input,
    /// fileChange.changes…), so probe the known carriers and fall back to the whole
    /// item minus the noisy/streaming fields.</summary>
    private static string? ExtractItemArguments(JsonElement item)
    {
        foreach (var name in new[] { "input", "command", "arguments", "query", "action", "changes", "args" })
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                // Wrap bare strings so the phone's arg renderer sees an object.
                return JsonSerializer.Serialize(new Dictionary<string, string?> { [name] = text });
            }
            return value.GetRawText();
        }
        return null;
    }

    /// <summary>Completed-tool output preview, again shape-dependent.</summary>
    private static string? ExtractItemResult(JsonElement item)
    {
        foreach (var name in new[] { "aggregatedOutput", "output", "result", "resultPreview" })
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return Truncate(text!);
            }
            else if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return Truncate(value.GetRawText());
            }
        }
        return null;

        static string Truncate(string s) => s.Length <= 2000 ? s : s[..2000] + "…";
    }

    private static AgentUsage? ExtractUsage(JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object) return null;
        if (!prms.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        int? input = usage.TryGetProperty("inputTokens", out var i) && i.TryGetInt32(out var iv) ? iv : null;
        int? output = usage.TryGetProperty("outputTokens", out var o) && o.TryGetInt32(out var ov) ? ov : null;
        if (input is null && output is null) return null;
        return new AgentUsage(input, output, (input ?? 0) + (output ?? 0));
    }

    private static string? ExtractError(JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object) return null;
        if (prms.TryGetProperty("error", out var e))
        {
            if (e.ValueKind == JsonValueKind.String) return e.GetString();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
                return m.GetString();
        }
        return null;
    }

    private static string? ExtractThreadId(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        if (result.TryGetProperty("thread", out var thread) && thread.ValueKind == JsonValueKind.Object
            && thread.TryGetProperty("id", out var tid))
            return tid.GetString();
        if (result.TryGetProperty("threadId", out var direct))
            return direct.GetString();
        return null;
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
        catch { /* best-effort */ }

        try { await _readerTask.ConfigureAwait(false); } catch { /* ignore */ }

        _turnChannel?.Writer.TryComplete();
        _lifetimeCts.Dispose();
        _turnGate.Dispose();
        _writeGate.Dispose();
        _process.Dispose();
    }
}
