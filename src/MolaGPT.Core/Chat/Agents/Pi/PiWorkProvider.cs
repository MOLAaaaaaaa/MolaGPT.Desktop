using MolaGPT.Core.Chat.Providers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Drop-in <see cref="IChatProvider"/> that runs MolaGPT <b>Work</b> on top of the
/// Pi harness instead of the in-process 64-turn ReAct loop. Pi owns the loop,
/// context compaction, tree sessions, retry and multi-provider plumbing; MolaGPT
/// keeps its own billing endpoint (account quota / BYOK), its sandboxed tools, and
/// its approval flow. Pi runs as a persistent per-conversation Node sidecar; the
/// LLM stream comes back over JSONL RPC and is translated to <see cref="ChatChunk"/>
/// so the existing WPF chat UI consumes it unchanged.
///
/// M1 brick #1: additive and NOT yet registered — building it changes no product
/// behaviour. Wiring it into DI / Work routing behind a feature flag is brick #2.
/// The mechanism is the one proven end-to-end by the M0 PoC (see <c>pi-sidecar/</c>).
/// </summary>
public sealed class PiWorkProvider : IChatProvider, IAsyncDisposable
{
    /// <summary>Provider id the sidecar extension registers (must match the value
    /// passed on the <c>pi --provider</c> flag and to <c>set_model</c>).</summary>
    public const string SidecarProviderId = "molagpt-work";

    private readonly PiWorkProviderConfig _config;
    private readonly IChatToolHost _toolHost;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    private readonly PiWorkToolBridge _bridge;
    private readonly PiWorkLlmShim _shim;
    private readonly ConcurrentDictionary<string, SessionHolder> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);
    private readonly Timer _idleSweep;
    private int _sidecarsCreated;

    /// <summary>How many sidecar processes this provider has started. Sidecars are
    /// the expensive part (~110 MB and a cold start each), so this is the number to
    /// watch when reasoning about whether something is churning them.</summary>
    public int SidecarsCreated => Volatile.Read(ref _sidecarsCreated);

    /// <summary>How long a conversation's sidecar may sit unused before it is
    /// reclaimed. Node+Pi costs ~80–150 MB resident, so an abandoned Work chat must
    /// not keep paying it; the next turn simply respawns (lazily, as on first use).</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(1);

    public PiWorkProvider(PiWorkProviderConfig config, IChatToolHost toolHost, HttpClient http, Action<string>? log = null)
    {
        _config = config;
        _toolHost = toolHost;
        _http = http;
        _log = log;
        _bridge = new PiWorkToolBridge(log);
        _shim = new PiWorkLlmShim(http, log);
        _idleSweep = new Timer(_ => SweepIdleSessions(), null, IdleSweepInterval, IdleSweepInterval);
    }

    /// <summary>Reclaim sidecars for conversations nobody has spoken to recently.
    /// A session driving a turn is never swept, however long that turn runs — an
    /// agent loop can legitimately think for longer than the idle timeout, and
    /// killing it mid-stream would surface as a mysterious failure.</summary>
    private void SweepIdleSessions()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (key, holder) in _sessions)
        {
            PiSidecarSession? victim = null;
            lock (holder.Gate)
            {
                if (holder.InUse || holder.LastUsedUtc > cutoff || holder.Session is null) continue;
                if (!_sessions.TryRemove(new KeyValuePair<string, SessionHolder>(key, holder))) continue;
                victim = holder.Session;
                holder.Session = null;
            }

            if (victim is null) continue;
            _log?.Invoke($"[pi-work] 回收空闲 sidecar：{key}");
            _ = Task.Run(async () =>
            {
                try { await victim.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log?.Invoke("[pi-work] 回收失败：" + ex.Message); }
            });
        }
    }

    public string Id => _config.ProviderId;
    public string DisplayName => _config.DisplayName;
    public ProviderKind Kind => ProviderKind.MolaGptLocalTools;
    public IReadOnlyList<ProviderModel> Models => _config.Models;

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var latestUser = LatestUserMessage(request.Messages);
        var userText = latestUser?.AsText() ?? string.Empty;
        var images = ExtractImages(latestUser);
        if (string.IsNullOrWhiteSpace(userText) && images.Count == 0) yield break;

        var options = LocalToolOptions.FromExtraBody(request.ExtraBody);
        var modelSupportsVision = Models.FirstOrDefault(m => m.Id == request.ModelId)?.SupportsVision ?? false;

        // LocalHttpClient must be supplied: ChatToolHost refuses search_web /
        // web_fetch / read_file / glob_files / grep_files without it.
        var toolContext = new ChatToolContext(request, Id, request.ModelId, modelSupportsVision, Models, _http);

        // MolaGPT's live tool set for this turn, in the same order the direct
        // provider sends it. The sidecar registers exactly these, so names and
        // schemas can never drift from what ChatToolHost dispatches on.
        var localDefs = LocalToolRegistry.BuildOpenAiToolDefinitions(options);
        var hostDefs = await _toolHost.BuildToolDefinitionsAsync(toolContext, options, ct).ConfigureAwait(false);
        var toolCatalogJson = JsonSerializer.Serialize(localDefs.Concat(hostDefs).ToArray());

        var creds = _config.ResolveCreds(request);

        // Reasoning settings ride in on the body merge rather than Pi's own
        // set_thinking_level: providers disagree about how to ask for reasoning
        // (DeepSeek's thinking:{type}, Qwen's enable_thinking + budget, everyone
        // else's reasoning_effort), and Pi's mapping is not MolaGPT's. Reusing the
        // shared table is the only way the Pi path asks for exactly what the direct
        // path asks for.
        creds = creds with { ExtraBody = MergeThinking(creds.ExtraBody, request) };

        // Only one turn drives the shared bridge + shim at a time (Work is sequential).
        await _bridgeGate.WaitAsync(ct).ConfigureAwait(false);

        // Claimed (InUse) before the gate is released, so the idle sweep can never
        // reclaim the sidecar this turn is about to stream from. The tool set is
        // deliberately NOT part of the identity: the extension re-reads this
        // catalogue on every turn (before_agent_start) and reconciles Pi's tools in
        // place, so toggling 联网搜索 / 视觉 / MCP costs nothing but a catalogue fetch.
        var holder = ClaimSession(request.ConversationId, creds);
        // Personas and per-model prompts arrive as system messages. Pi substitutes
        // its own coding-assistant prompt when nobody says otherwise, so failing to
        // forward these would quietly discard whatever the user selected.
        var systemPrompt = ExtractSystemPrompt(request.Messages);

        _bridge.SetCatalog(() => toolCatalogJson);
        _bridge.SetSystemPrompt(() => systemPrompt);
        _bridge.SetDispatcher((name, argsJson, toolCt) =>
            _toolHost.ExecuteAsync(name, argsJson, toolContext, options, toolCt));
        _shim.SetTarget(new PiWorkLlmShim.ForwardTarget(creds.Endpoint, creds.TokenProvider, creds.OnUnauthorized, creds.Headers, creds.ExtraBody, creds.Auth));

        string? errorMessage = null;
        var pendingArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await foreach (var line in holder.Session!.SendTurnAsync(userText, images, ct).ConfigureAwait(false))
            {
                var chunk = MapLine(line, options, pendingArgs, ref errorMessage);
                if (chunk is not null) yield return chunk;
            }
        }
        finally
        {
            lock (holder.Gate)
            {
                holder.InUse = false;
                holder.LastUsedUtc = DateTime.UtcNow; // idle clock starts when the turn ends
            }
            _bridge.SetDispatcher(null);
            _bridge.SetCatalog(null);
            _bridge.SetSystemPrompt(null);
            _shim.SetTarget(null);
            _bridgeGate.Release();
        }

        if (errorMessage is not null)
            throw new InvalidOperationException(errorMessage);
    }

    /// <summary>Get (or lazily spawn) the conversation's sidecar and mark it in use
    /// for the turn about to run. The caller MUST clear <see cref="SessionHolder.InUse"/>
    /// when the turn finishes.</summary>
    private SessionHolder ClaimSession(string? conversationId, PiProviderCreds creds)
    {
        var key = conversationId ?? "draft";
        var signature = creds.Signature;
        while (true)
        {
            var holder = _sessions.GetOrAdd(key, _ => new SessionHolder());
            lock (holder.Gate)
            {
                // The holder may have been retired (conversation closed / idle swept)
                // between GetOrAdd and the lock; if so it is no longer the registered
                // one, and reusing it would hand back a session nobody will dispose.
                if (!_sessions.TryGetValue(key, out var current) || !ReferenceEquals(current, holder))
                    continue;

                holder.LastUsedUtc = DateTime.UtcNow;
                holder.InUse = true;

                // Reuse the live session unless the model/creds/tool set changed
                // (all baked into the process at spawn).
                if (holder.Session is { IsAlive: true } && holder.Signature == signature)
                    return holder;

                if (holder.Session is not null)
                    _ = holder.Session.DisposeAsync().AsTask();

                // The sidecar is pointed at the local shim, never at the real
                // endpoint: no credential (JWT or BYOK key) is ever baked into the
                // Node process env. Its "api key" is the shim's throwaway token.
                var launch = new PiSidecarLaunchOptions(
                    _config.NodePath, _config.CliJsPath, _config.ExtensionPath, _config.WorkingDirectory,
                    SanitizeSessionId(key), _config.SessionRoot,
                    _shim.BaseUrl, _shim.Token, creds.Model, creds.Api, AuthHeader: true, creds.Reasoning,
                    _bridge.Url, _bridge.Token);
                holder.Session = new PiSidecarSession(launch, _log);
                holder.Signature = signature;
                Interlocked.Increment(ref _sidecarsCreated);
                return holder;
            }
        }
    }

    /// <summary>Tear down a conversation's sidecar (call when the conversation is
    /// closed/deleted, or on idle timeout — the memory-conscious teardown that
    /// keeps the Node footprint scoped to active Work conversations).</summary>
    public async Task CloseConversationAsync(string? conversationId)
    {
        if (_sessions.TryRemove(conversationId ?? "draft", out var holder) && holder.Session is not null)
            await holder.Session.DisposeAsync().ConfigureAwait(false);
    }

    private ChatChunk? MapLine(
        string line,
        LocalToolOptions options,
        IDictionary<string, string> pendingArgs,
        ref string? errorMessage)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "message_update":
                    if (root.TryGetProperty("assistantMessageEvent", out var ev))
                    {
                        var kind = ev.TryGetProperty("type", out var k) ? k.GetString() : null;
                        var delta = ev.TryGetProperty("delta", out var d) ? d.GetString() : null;
                        if (kind == "text_delta" && delta is not null)
                            return new ChatChunk(DeltaText: delta);
                        if (kind == "thinking_delta" && delta is not null)
                            return new ChatChunk(DeltaThinking: delta);
                    }
                    return null;

                case "tool_execution_start":
                {
                    var startId = Str(root, "toolCallId");
                    var startArgs = root.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";

                    // Pi reports the arguments when a call starts but not when it
                    // ends, and the finished card needs them — hold on to them.
                    pendingArgs[startId] = startArgs;
                    return new ChatChunk(Tool: OpenAICompatibleProvider.BuildToolDelta(
                        startId, Str(root, "toolName"), startArgs, options, "running"));
                }

                case "tool_execution_end":
                {
                    var isError = root.TryGetProperty("isError", out var er) && er.GetBoolean();
                    var endId = Str(root, "toolCallId");
                    var resultJson = root.TryGetProperty("result", out var r) ? UnwrapToolResult(r) : null;
                    pendingArgs.Remove(endId, out var endArgs);

                    // Built by the same function the direct provider uses, so the
                    // duration, exit code and permission labels that live inside the
                    // tool's own result reach the card here too.
                    var delta = OpenAICompatibleProvider.BuildToolDelta(
                        endId,
                        Str(root, "toolName"),
                        endArgs ?? "{}",
                        options,
                        isError ? "error" : "completed",
                        resultJson);

                    // MapLine's own status vocabulary is what the UI already reacts
                    // to; only the detail-building needed the "error" spelling.
                    return new ChatChunk(Tool: delta with { Status = isError ? "failed" : "completed" });
                }

                case "agent_end":
                    if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                        foreach (var mm in msgs.EnumerateArray())
                            if (mm.TryGetProperty("stopReason", out var sr) && sr.GetString() == "error")
                                errorMessage = mm.TryGetProperty("errorMessage", out var em) ? em.GetString() : "Pi agent error";
                    return null;

                case "agent_settled":
                    return new ChatChunk(FinishReason: "stop");

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Fold the turn's thinking parameters into the model's custom parameters, so
    /// the shim sends both. Custom parameters win on a clash: they are the user's
    /// explicit override, exactly as in the direct provider where ApplyCustomBody
    /// runs after the thinking block.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement>? MergeThinking(
        IReadOnlyDictionary<string, JsonElement>? custom,
        ChatRequest request)
    {
        var thinking = new Dictionary<string, object?>(StringComparer.Ordinal);
        ThinkingParams.Apply(thinking, request);
        if (thinking.Count == 0) return custom;

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in thinking)
            merged[key] = JsonSerializer.SerializeToElement(value);
        if (custom is not null)
            foreach (var (key, value) in custom)
                merged[key] = value;
        return merged;
    }

    /// <summary>Join the turn's system messages, in order. Null when there are
    /// none, which leaves Pi on its own prompt rather than blanking it.</summary>
    private static string? ExtractSystemPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var parts = messages
            .Where(m => m.Role == ChatMessage.RoleSystem)
            .Select(m => m.AsText())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }

    private static ChatMessage? LatestUserMessage(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == ChatMessage.RoleUser)
                return messages[i];
        return null;
    }

    /// <summary>Image attachments on the turn, in Pi's wire shape. Pi owns the
    /// conversation history, so only this turn's images need sending — earlier ones
    /// are already in its session.</summary>
    private static IReadOnlyList<PiImage> ExtractImages(ChatMessage? message)
    {
        if (message?.Attachments is not { Count: > 0 } attachments) return Array.Empty<PiImage>();

        var images = new List<PiImage>();
        foreach (var attachment in attachments)
        {
            if (!attachment.IsImage || attachment.Bytes is not { Length: > 0 }) continue;
            images.Add(new PiImage(
                Convert.ToBase64String(attachment.Bytes),
                string.IsNullOrWhiteSpace(attachment.MimeType) ? "image/png" : attachment.MimeType));
        }
        return images;
    }

    /// <summary>Pi writes the session as <c>&lt;id&gt;.jsonl</c>, so the id must be a
    /// safe filename. Conversation ids are already tame; this is belt-and-braces.
    /// Public so the session-file sweeper can derive the same ids it must match.</summary>
    public static string SanitizeSessionId(string key)
    {
        var chars = key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var safe = new string(chars);
        return safe.Length <= 120 ? safe : safe[..120];
    }

    /// <summary>
    /// Pull the tool's own output out of Pi's result envelope
    /// (<c>{content:[{type:"text",text:"…"}], details:…}</c>).
    ///
    /// Showing the envelope raw is what made the tool card display a wall of
    /// escaped JSON instead of the result: the payload the direct provider puts on
    /// the card is the tool's output string, not the transport wrapper around it.
    /// </summary>
    private static string UnwrapToolResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    parts.Add(text.GetString() ?? "");
            }
            if (parts.Count > 0)
                return Truncate(PrettyJson(string.Join("\n", parts)), ResultPreviewLimit);
        }

        // Unrecognised shape (a future Pi change, a non-text part): the raw value
        // beats showing nothing.
        return Truncate(result.GetRawText(), ResultPreviewLimit);
    }

    /// <summary>Indent the tool's JSON output for the card, the way the direct
    /// provider does. Non-JSON output (plain stdout) is shown as-is.</summary>
    private static string PrettyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, DisplayJson);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Matches the direct provider's preview budget, so the same result is
    /// not cut short just because it arrived through Pi.</summary>
    private const int ResultPreviewLimit = 1600;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public async ValueTask DisposeAsync()
    {
        await _idleSweep.DisposeAsync().ConfigureAwait(false);
        foreach (var holder in _sessions.Values)
            if (holder.Session is not null)
                await holder.Session.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
        _bridge.Dispose();
        _shim.Dispose();
        _bridgeGate.Dispose();
    }

    private sealed class SessionHolder
    {
        public object Gate { get; } = new();
        public PiSidecarSession? Session { get; set; }
        public string? Signature { get; set; }

        /// <summary>A turn is currently streaming from this session — do not reclaim.</summary>
        public bool InUse { get; set; }

        /// <summary>When the last turn finished; the idle sweep measures from here.</summary>
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }
}

/// <summary>Static config for the provider: identity, models, and how to find node
/// + the sidecar assets. <see cref="ResolveCreds"/> is called per request so the
/// account-quota JWT (or a BYOK key) can be supplied live by the DI wiring (brick #2).</summary>
public sealed record PiWorkProviderConfig(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<ProviderModel> Models,
    string NodePath,
    string CliJsPath,
    string ExtensionPath,
    string WorkingDirectory,
    string SessionRoot,
    Func<ChatRequest, PiProviderCreds> ResolveCreds);

/// <summary>Per-request billing target. Account-quota → the MolaGPT relay URL +
/// the live account JWT; BYOK → the user's own chat endpoint + key. Neither the
/// URL nor the token reaches the sidecar: both are consumed by
/// <see cref="PiWorkLlmShim"/> inside the desktop process.</summary>
/// <param name="Endpoint">Absolute URL of the real chat-completions endpoint.</param>
/// <param name="TokenProvider">Resolves the bearer token at request time, so a
/// rotating account JWT is always current without respawning the sidecar.</param>
public sealed record PiProviderCreds(
    string Endpoint,
    Func<CancellationToken, Task<string?>> TokenProvider,
    string Model,
    string Api = "openai-completions",
    bool Reasoning = false,
    Action? OnUnauthorized = null,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
    IReadOnlyDictionary<string, JsonElement>? ExtraBody = null,
    PiWorkLlmShim.AuthStyle Auth = PiWorkLlmShim.AuthStyle.Bearer)
{
    /// <summary>Identity used to decide whether a live sidecar can be reused or
    /// must be respawned. Only values baked into the process at spawn time count —
    /// notably <em>not</em> the token, which the shim now injects per request.</summary>
    public string Signature => $"{Endpoint}|{Model}|{Api}|{Reasoning}";
}
