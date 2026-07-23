using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat.Agents.Relay;
using MolaGPT.Core.Sse;

namespace MolaGPT.Desktop.Services;

/// <summary>
/// HTTP+SSE implementation of <see cref="IRelayProducer"/> — the desktop bridge's
/// connection to the cloud relay at chatgpt.wljay.cn/v2/api/auth/agent_*.php.
/// It reuses the app's <c>MolaGptAuthService</c> long login JWT (UA-bound) — the
/// relay shares credentials with chat, so no separate auth/ALTCHA flow is needed.
///
///   • PostEventAsync/PostMetaAsync/command lease+result → POST agent_events.php
///   • SubscribeCommandsAsync → long-connect SSE to agent_command_stream.php and
///     deserialize each <c>data:</c> line back into a <see cref="RelayCommand"/>.
///
/// Reconnect-on-disconnect lives in the SSE loop itself (the producer reconnects
/// with a short backoff whenever the stream ends). Events POST as opaque JSON —
/// only the transcript event is serialized through the <c>kind</c>-discriminated
/// polymorphic converter; the PHP relay stores/forwards it without reinterpreting.
/// </summary>
public sealed class HttpRelayProducer : IRelayProducer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly MolaGptAuthService _auth;
    private readonly string _base; // e.g. https://chatgpt.wljay.cn/v2
    private readonly string _machineId;
    private readonly string _machineName;

    public HttpRelayProducer(
        HttpClient http,
        MolaGptAuthService auth,
        string? baseUrl = null,
        string? machineId = null,
        string? machineName = null)
    {
        _http = http;
        _auth = auth;
        _base = baseUrl ?? "https://chatgpt.wljay.cn/v2";
        _machineId = (machineId ?? string.Empty).Trim();
        _machineName = string.IsNullOrWhiteSpace(machineName)
            ? Environment.MachineName
            : machineName.Trim();
    }

    public async Task<IReadOnlyDictionary<string, RelaySessionCursor>> ListSessionCursorsAsync(CancellationToken ct)
    {
        using var doc = await GetAgentSessionsRootAsync(ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("sessions", out var sessions) ||
            sessions.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, RelaySessionCursor>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, RelaySessionCursor>(StringComparer.Ordinal);
        foreach (var session in sessions.EnumerateArray())
        {
            var id = ReadString(session, "conversationId")
                ?? ReadString(session, "conversation_id")
                ?? ReadString(session, "sessionId")
                ?? ReadString(session, "session_id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var seq = ReadLong(session, "seq");
            var activityAtMs = ReadLong(session, "activityAtMs", "activity_at_ms", "ActivityAtMs");
            if (result.TryGetValue(id, out var existing))
            {
                result[id] = new RelaySessionCursor(
                    id,
                    Math.Max(existing.Seq, seq),
                    Math.Max(existing.ActivityAtMs, activityAtMs));
            }
            else
            {
                result[id] = new RelaySessionCursor(id, seq, activityAtMs);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<RelayMobileDevice>> ListMobileDevicesAsync(CancellationToken ct)
    {
        using var doc = await GetAgentSessionsRootAsync(ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RelayMobileDevice>();
        }

        var result = new List<RelayMobileDevice>();
        foreach (var device in devices.EnumerateArray())
        {
            var id = ReadString(device, "id", "deviceId", "device_id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = ReadString(device, "name", "deviceName", "device_name");
            var lastSeenAtMs = ReadLong(device, "lastSeenAtMs", "last_seen_at_ms", "LastSeenAtMs");
            result.Add(new RelayMobileDevice(id, name, lastSeenAtMs));
        }

        return result
            .OrderByDescending(d => d.LastSeenAtMs)
            .ToList();
    }

    public async Task PostEventAsync(RelayEventEnvelope envelope, CancellationToken ct)
    {
        // Serialize the event through the polymorphic converter (embeds its "kind"
        // discriminator), then re-parse to a JsonNode so the PHP relay gets plain
        // JSON it stores and forwards opaquely — it never reinterprets the event.
        var evJson = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
        var body = new { kind = "event", sessionId = envelope.SessionId, @event = evJson };

        await PostAsync("/api/auth/agent_events.php", body, ct).ConfigureAwait(false);
    }

    public async Task ResetSessionEventsAsync(string sessionId, CancellationToken ct)
        => await PostAsync("/api/auth/agent_events.php", new { kind = "reset", sessionId }, ct).ConfigureAwait(false);

    public async Task ReplaceSessionEventsAsync(string sessionId, IReadOnlyList<RelayEventEnvelope> events, CancellationToken ct)
    {
        // Serialize each envelope through the polymorphic converter (like
        // PostEventAsync), then ship the whole ordered set in ONE request. The relay
        // replaces the session's events atomically (temp file + rename), so a phone
        // reading mid-projection sees either the old or the new complete transcript —
        // never a partial rebuild.
        var arr = new JsonArray();
        foreach (var envelope in events)
            arr.Add(JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(envelope, Json)));
        var body = new { kind = "replace", sessionId, events = arr };
        await PostAsync("/api/auth/agent_events.php", body, ct).ConfigureAwait(false);
    }

    public async Task PostMetaAsync(RelaySessionMeta meta, CancellationToken ct)
    {
        var body = new { kind = "meta", sessionId = meta.ConversationId, meta };
        await PostAsync("/api/auth/agent_events.php", body, ct).ConfigureAwait(false);
    }

    public async Task PostHeartbeatAsync(IReadOnlyList<string> sessionIds, CancellationToken ct)
        => await PostAsync("/api/auth/agent_events.php", new
        {
            kind = "heartbeat",
            sessionId = "__machine__",
            sessionIds,
            machineId = string.IsNullOrWhiteSpace(_machineId) ? null : _machineId,
            machineName = string.IsNullOrWhiteSpace(_machineName) ? null : _machineName,
        }, ct).ConfigureAwait(false);

    public async Task MarkMachineOfflineAsync(CancellationToken ct)
        => await PostAsync("/api/auth/agent_events.php", new
        {
            kind = "offline",
            sessionId = "__machine__",
            machineId = string.IsNullOrWhiteSpace(_machineId) ? null : _machineId,
        }, ct).ConfigureAwait(false);

    public Task<RelayCommandLease> LeaseCommandAsync(string sessionId, string cmdId, CancellationToken ct)
        => UpdateCommandLeaseAsync(sessionId, cmdId, ct);

    public Task<RelayCommandLease> RenewCommandLeaseAsync(string sessionId, string cmdId, CancellationToken ct)
        => UpdateCommandLeaseAsync(sessionId, cmdId, ct);

    public async Task<bool> CompleteCommandAsync(
        string sessionId,
        string cmdId,
        bool succeeded,
        string? error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_machineId)) return false;
        using var doc = await PostJsonAsync("/api/auth/agent_events.php", new
        {
            kind = "result",
            sessionId,
            cmdId,
            machineId = _machineId,
            status = succeeded ? "done" : "failed",
            error = string.IsNullOrWhiteSpace(error) ? null : error,
        }, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return ReadBool(root, "success") && ReadBool(root, "completed");
    }

    public async IAsyncEnumerable<RelayCommand> SubscribeCommandsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Stream? stream;
            try { stream = await OpenCommandStreamAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            catch
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }

            if (stream is null)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }

            using (stream)
            {
                var reader = SseStreamReader.ReadAsync(stream, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        bool hasNext;
                        try { hasNext = await reader.MoveNextAsync().ConfigureAwait(false); }
                        catch (OperationCanceledException) { yield break; }
                        catch { break; }

                        if (!hasNext) break;

                        var payload = reader.Current;
                        if (payload.IsDone) yield break;
                        if (TryParseCommand(payload.Data, out var cmd) && cmd is not null)
                            yield return cmd;
                    }
                }
                finally
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            // Stream ended (relay/proxy drop) — loop reconnects after a short backoff.
        }
    }

    private async Task<Stream?> OpenCommandStreamAsync(CancellationToken ct)
    {
        var url = _base + "/api/auth/agent_command_stream.php";
        if (!string.IsNullOrWhiteSpace(_machineId))
            url += "?machine=" + Uri.EscapeDataString(_machineId);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!TryAttachAuth(req)) return null;
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch { return null; }
        if (!resp.IsSuccessStatusCode) { resp.Dispose(); return null; }
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    private async Task<RelayCommandLease> UpdateCommandLeaseAsync(
        string sessionId,
        string cmdId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_machineId)) return new RelayCommandLease(false);
        using var doc = await PostJsonAsync("/api/auth/agent_events.php", new
        {
            kind = "lease",
            sessionId,
            cmdId,
            machineId = _machineId,
        }, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return new RelayCommandLease(
            Acquired: ReadBool(root, "success") && ReadBool(root, "leased"),
            ExpiresAtMs: ReadLong(root, "leaseExpiresAtMs", "lease_expires_at_ms"));
    }

    private async Task PostAsync(string path, object body, CancellationToken ct)
    {
        using var _ = await PostJsonAsync(path, body, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _base + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json")
        };
        if (!TryAttachAuth(req)) throw new InvalidOperationException("Relay auth is not available.");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetAgentSessionsRootAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _base + "/api/auth/agent_sessions.php?include_offline=1");
        if (!TryAttachAuth(req)) throw new InvalidOperationException("Relay auth is not available.");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Relay sessions request failed: {(int)resp.StatusCode}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private bool TryAttachAuth(HttpRequestMessage req)
    {
        var jwt = _auth.CurrentJwt;
        if (string.IsNullOrEmpty(jwt)) return false;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return true;
    }

    private static bool TryParseCommand(string data, out RelayCommand? cmd)
    {
        cmd = default;
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            string sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
            string cmdId = root.TryGetProperty("cmdId", out var c) ? c.GetString() ?? "" : "";
            string opStr = root.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
            string? payloadJson = null;
            if (root.TryGetProperty("payloadJson", out var p) && p.ValueKind == JsonValueKind.String)
                payloadJson = p.GetString();
            string? machineId = null;
            if (root.TryGetProperty("machineId", out var mid) && mid.ValueKind == JsonValueKind.String)
                machineId = mid.GetString();
            else if (root.TryGetProperty("machine_id", out var midSnake) && midSnake.ValueKind == JsonValueKind.String)
                machineId = midSnake.GetString();
            if (!Enum.TryParse<RelayCommandOp>(opStr, ignoreCase: true, out var op)) return false;
            cmd = new RelayCommand(sessionId, cmdId, op, payloadJson, machineId);
            return true;
        }
        catch { return false; }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static long ReadLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
                _ => 0
            };
        }

        return 0;
    }

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
}
