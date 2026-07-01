using System.Collections.Concurrent;
using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Control-protocol client half for <see cref="ClaudeCodeSession"/>: sends
/// <c>control_request</c>s WE originate (initialize, set_model) and correlates
/// their <c>control_response</c>s by request id. The incoming <c>can_use_tool</c>
/// approval flow lives in the reader partial; this is the outbound side.
///
/// Wire (confirmed against Claude Code 2.1.196):
///   → {"type":"control_request","request_id":"cc_N","request":{"subtype":"initialize","hooks":null}}
///   ← {"type":"control_response","response":{"subtype":"success","request_id":"cc_N","response":{...,"models":[...]}}}
///   → {"type":"control_request","request_id":"cc_M","request":{"subtype":"set_model","model":"haiku"}}
///   ← {"type":"control_response","response":{"subtype":"success","request_id":"cc_M","response":{}}}
/// </summary>
internal sealed partial class ClaudeCodeSession
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingControlResponses
        = new(StringComparer.Ordinal);
    private int _controlSeq;
    private volatile IReadOnlyList<AgentModelInfo> _availableModels = Array.Empty<AgentModelInfo>();
    private Task? _initTask;

    public IReadOnlyList<AgentModelInfo> AvailableModels => _availableModels;

    /// <summary>Kick off the initialize handshake once, in the background, to
    /// populate <see cref="AvailableModels"/>. The control channel is live from
    /// process start in stream-json input mode, so no user turn is required.</summary>
    internal void BeginInitialize()
        => _initTask ??= Task.Run(() => InitializeAsync(_lifetimeCts.Token));

    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var response = await SendControlRequestAsync(
                new { subtype = "initialize", hooks = (object?)null }, TimeSpan.FromSeconds(20), ct)
                .ConfigureAwait(false);
            _availableModels = ParseModels(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: the phone just falls back to observed model ids.
            _channel.Writer.TryWrite(AgentEvent.Failure($"Claude model discovery failed: {ex.Message}"));
        }
    }

    /// <summary>Switch the live session's model via the set_model control request.
    /// No process restart; conversation context is preserved.</summary>
    public async Task<bool> SetModelAsync(string? model, CancellationToken ct)
    {
        if (_disposed != 0 || SafeHasExited()) return false;
        // model may be null to reset to default; the CLI accepts an explicit null.
        await SendControlRequestAsync(
            new { subtype = "set_model", model }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Send a control_request and await its correlated control_response.
    /// Returns the inner <c>response.response</c> payload (empty object when none).</summary>
    private async Task<JsonElement> SendControlRequestAsync(object request, TimeSpan timeout, CancellationToken ct)
    {
        var requestId = $"cc_{Interlocked.Increment(ref _controlSeq)}_{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControlResponses[requestId] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        await using var reg = timeoutCts.Token.Register(() =>
            tcs.TrySetException(new TimeoutException($"Claude control request timed out: {DescribeSubtype(request)}")));

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "control_request",
                request_id = requestId,
                request
            });
            await WriteLineAsync(payload, ct).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingControlResponses.TryRemove(requestId, out _);
        }
    }

    /// <summary>Route an inbound control_response to the waiter registered by
    /// <see cref="SendControlRequestAsync"/>. Called from the reader loop.</summary>
    private void CompleteControlResponse(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return;
        var requestId = response.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        if (string.IsNullOrEmpty(requestId) || !_pendingControlResponses.TryRemove(requestId, out var tcs))
            return;

        var subtype = response.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype == "error")
        {
            var error = response.TryGetProperty("error", out var e) ? e.GetString() : null;
            tcs.TrySetException(new InvalidOperationException(error ?? "Claude control request failed"));
            return;
        }

        // success: the payload we care about is response.response (may be absent).
        var inner = response.TryGetProperty("response", out var innerEl) && innerEl.ValueKind == JsonValueKind.Object
            ? innerEl.Clone()
            : JsonSerializer.SerializeToElement(new { });
        tcs.TrySetResult(inner);
    }

    /// <summary>Parse the initialize response's <c>models</c> array into
    /// <see cref="AgentModelInfo"/>. Each entry: value / displayName / description /
    /// supportedEffortLevels (+ a "default" entry the CLI marks as recommended).</summary>
    private static IReadOnlyList<AgentModelInfo> ParseModels(JsonElement initResponse)
    {
        if (!initResponse.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return Array.Empty<AgentModelInfo>();

        var list = new List<AgentModelInfo>();
        foreach (var m in models.EnumerateArray())
        {
            var value = m.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(value)) continue;

            var display = m.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                ? dn.GetString()! : value;
            var description = m.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            var isDefault = value == "default";

            IReadOnlyList<string>? efforts = null;
            if (m.TryGetProperty("supportedEffortLevels", out var el) && el.ValueKind == JsonValueKind.Array)
            {
                var e = new List<string>();
                foreach (var lvl in el.EnumerateArray())
                    if (lvl.ValueKind == JsonValueKind.String && lvl.GetString() is { } s) e.Add(s);
                if (e.Count > 0) efforts = e;
            }

            list.Add(new AgentModelInfo(value!, display, description, isDefault, efforts));
        }
        return list;
    }

    private static string DescribeSubtype(object request)
    {
        var prop = request.GetType().GetProperty("subtype");
        return prop?.GetValue(request)?.ToString() ?? "control";
    }
}
