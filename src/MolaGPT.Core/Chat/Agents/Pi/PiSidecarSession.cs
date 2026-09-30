using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Owns one persistent <c>pi --mode rpc</c> Node subprocess and speaks Pi's JSONL
/// RPC over stdin/stdout.
///
/// Not tied to a conversation: <see cref="PiRuntime"/> leases the process out and
/// points it at whichever transcript the turn needs. That matters because the
/// process is the expensive part — measured at ~95 MB resident and ~2.7s to boot,
/// against ~60ms to switch transcripts — so the pool keeps a couple of these warm
/// instead of one per open chat.
///
/// Validated end-to-end by the M0 PoC (see <c>pi-sidecar/</c>). This is the
/// product port: same protocol, same four seams.
/// </summary>
public sealed class PiSidecarSession : IAsyncDisposable
{
    private readonly PiSidecarLaunchOptions _launch;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _stdinLock = new();

    private Pipe? _pipe;
    private string? _activeModel;
    private string? _activeThinkingLevel;
    private bool _autoRetryEnabled;

    public PiSidecarSession(PiSidecarLaunchOptions launch, Action<string>? log = null)
    {
        _launch = launch;
        _log = log;
    }

    public bool IsAlive => _pipe?.Process is { HasExited: false };

    /// <summary>
    /// One process's stdout, read by a single pump for the life of the process.
    ///
    /// A response to a command that carries an id goes to whoever awaits it, even
    /// mid-turn; everything else goes to the turn reading <see cref="Events"/>, or
    /// nowhere between turns. Reading stdout from two places at once is what made
    /// every reply to a mid-turn command unreachable before.
    /// </summary>
    private sealed class Pipe(Process process)
    {
        public Process Process { get; } = process;
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending { get; } =
            new(StringComparer.Ordinal);

        /// <summary>Steers and follow-ups Pi has not answered yet.</summary>
        public List<Task> Enqueues { get; } = [];

        /// <summary>Guarded by the pipe itself, together with <see cref="Exited"/>, so a
        /// turn attaching as the process dies still sees its channel completed.</summary>
        public Channel<string>? Events;
        public bool Exited;
    }

    /// <summary>Start the RPC process and wait until it can answer a command,
    /// without opening a conversation or sending anything to a model.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(EnsureStarted, ct).ConfigureAwait(false);
            await RequestAsync("get_state", new { }, ct).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (IsAlive) return;

        var psi = new ProcessStartInfo
        {
            FileName = _launch.NodePath,
            WorkingDirectory = _launch.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // node.exe is a console app: without this it flashes a black console
            // window over the WPF UI every time a sidecar spawns.
            CreateNoWindow = true,
            // No BOM: Pi's strict JSONL parser rejects a leading U+FEFF on the
            // first command (learned the hard way in M0).
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        Directory.CreateDirectory(_launch.SessionRoot);
        Directory.CreateDirectory(_launch.WorkingDirectory);
        PrepareAgentDirectory(_launch.AgentDirectory);

        foreach (var arg in new[]
                 {
                     _launch.CliJsPath, "--mode", "rpc",

                     // Boots without a session on purpose. The transcript still has
                     // to persist — the provider only sends the latest user message
                     // and lets Pi own the history — but a sidecar is no longer tied
                     // to one conversation: it is leased from a pool and pointed at
                     // whichever conversation needs it via `switch_session`, which
                     // takes an explicit path and creates the file lazily. Baking
                     // `--session-id` in instead would mean one process per
                     // conversation, which is what the pool exists to avoid.
                     "--no-session",

                     "--provider", PiWorkProvider.SidecarProviderId,
                     "--model", _launch.Model, "-e", _launch.ExtensionPath,

                     // Pi's orchestration tools, registered inactive. The extension
                     // declares them when the catalogue has tools that need them:
                     // codemode runs scripts that call tools (in parallel, and keep
                     // only what the model needs), tool_search declares deferred
                     // tools on request. Both reach tools through the extension, so
                     // every call still goes through MolaGPT's approval.
                     "-e", "builtin:codemode", "-e", "builtin:tool-search",

                     // Pi is a *coding* agent by default: `read, bash, edit, write`
                     // are enabled unless told otherwise. MolaGPT Work is not that —
                     // its only execution surface is the sandboxed Python tool (risk
                     // analyzer + session allow-list + approval). Adopting Pi as the
                     // harness must not smuggle in unsandboxed shell/file access, so
                     // built-ins are off and only our extension's tools survive.
                     "--no-builtin-tools",

                     // Isolate from the user's own Pi installation: no globally
                     // installed extensions/skills/templates/themes get loaded into
                     // Work, and no AGENTS.md/CLAUDE.md is picked up from the working
                     // directory. Explicit `-e` above is unaffected. Since Pi 0.99
                     // this also drops Pi's built-in extensions (MCP, codemode, tool
                     // search, llama.cpp); each has to be named with `-e builtin:`.
                     "--no-extensions", "--no-skills", "--no-prompt-templates",
                     "--no-themes", "--no-context-files",

                     // No startup network chatter (model-catalog refresh etc.); the
                     // model list is MolaGPT's, and this keeps first-turn latency down.
                     "--offline",
                 })
            psi.ArgumentList.Add(arg);

        // Every one of these is required by the extension, which throws at load if
        // one is missing rather than guessing a value for it. MOLA_PROVIDER_MODEL
        // and MOLA_PROVIDER_REASONING used to be here too and are gone: the default
        // model already travels as the `--model` argument above, and per-model
        // reasoning is a field of each entry in MOLA_PROVIDER_MODELS.
        psi.Environment["MOLA_PROVIDER_BASE_URL"] = _launch.BaseUrl;
        psi.Environment["MOLA_PROVIDER_API_KEY"] = _launch.ApiKey;
        psi.Environment["MOLA_PROVIDER_API"] = _launch.Api;
        psi.Environment["MOLA_PROVIDER_AUTH_HEADER"] = _launch.AuthHeader ? "true" : "false";
        psi.Environment["MOLA_PROVIDER_MODELS"] = _launch.ModelsJson;
        psi.Environment["MOLA_TOOL_CALLBACK_URL"] = _launch.ToolCallbackUrl;
        psi.Environment["MOLA_TOOL_TOKEN"] = _launch.ToolCallbackToken;
        psi.Environment["PI_CODING_AGENT_DIR"] = _launch.AgentDirectory;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Pi sidecar（node 未找到？）");
        var pipe = new Pipe(proc);
        _pipe = pipe;
        _activeModel = null;
        _activeThinkingLevel = null;
        _autoRetryEnabled = false;

        _ = Task.Run(() => PumpAsync(pipe));

        // Drain stderr so the pipe never blocks; forward diagnostics to the log.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                _log?.Invoke("[pi] " + line);
        });
    }

    /// <summary>
    /// Settings every sidecar runs with, pinned rather than left to Pi's defaults.
    /// </summary>
    private static readonly (string Key, string Value)[] PinnedSettings =
    [
        // One queued message per model call: each one becomes its own turn in the
        // transcript, which is how the app splits the reply around it.
        ("steeringMode", "one-at-a-time"),
        ("followUpMode", "one-at-a-time"),
        // A cache refresh is a model request billed to the user that nobody asked for.
        ("cacheWarming", "off"),
    ];

    private static readonly object AgentDirectoryGate = new();

    /// <summary>
    /// Pi's config directory for MolaGPT's sidecars, apart from the user's own
    /// <c>~/.pi/agent</c>. Sharing it let personal Pi settings change how Work
    /// behaves, and wrote the sidecars' <c>set_model</c> and
    /// <c>set_auto_compaction</c> into the user's settings.
    ///
    /// Merged rather than overwritten: Pi records its own state in the same file.
    /// A failed write only leaves Pi's defaults in place, so it never blocks a spawn.
    /// </summary>
    private void PrepareAgentDirectory(string directory)
    {
        lock (AgentDirectoryGate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "settings.json");
                JsonObject settings;
                try
                {
                    settings = File.Exists(path)
                        ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                        : new JsonObject();
                }
                catch (JsonException)
                {
                    settings = new JsonObject();
                }

                var changed = false;
                foreach (var (key, value) in PinnedSettings)
                {
                    if (settings[key] is JsonValue current
                        && current.TryGetValue<string>(out var text)
                        && text == value)
                        continue;
                    settings[key] = value;
                    changed = true;
                }
                if (!changed) return;

                var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, settings.ToJsonString(CommandJsonOptions), new UTF8Encoding(false));
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke("[pi] 无法写入 Agent 设置：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// JSONL commands go over a UTF-8 (no BOM) stdin pipe, so there is no reason to
    /// escape non-ASCII: the default encoder turns every Chinese character into
    /// <c>\uXXXX</c> and inflates a prompt carrying attachment text roughly sixfold,
    /// which is what pushes it past the pipe buffer in the first place.
    /// </summary>
    private static readonly JsonSerializerOptions CommandJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void Send(object command)
    {
        var json = JsonSerializer.Serialize(command, CommandJsonOptions);
        var stdin = (_pipe ?? throw new InvalidOperationException("Pi sidecar 未启动。")).Process.StandardInput;
        lock (_stdinLock)
        {
            stdin.WriteLine(json);
            stdin.Flush();
        }
    }

    private static Dictionary<string, object?> Command(string type, string id, object payload)
    {
        var command = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = type, ["id"] = id };
        foreach (var property in payload.GetType().GetProperties())
            command[property.Name] = property.GetValue(payload);
        return command;
    }

    private async Task PumpAsync(Pipe pipe)
    {
        try
        {
            string? line;
            while ((line = await pipe.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                line = line.TrimEnd('\r');
                if (line.Length == 0 || Route(pipe, line)) continue;
                Channel<string>? events;
                lock (pipe) events = pipe.Events;
                events?.Writer.TryWrite(line);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("[pi] 读取 sidecar 输出失败：" + ex.Message);
        }
        finally
        {
            Channel<string>? events;
            lock (pipe)
            {
                pipe.Exited = true;
                events = pipe.Events;
            }
            var exited = new InvalidOperationException("Pi sidecar 已退出。");
            foreach (var pending in pipe.Pending.Values) pending.TrySetException(exited);
            events?.Writer.TryComplete();
        }
    }

    /// <summary>Handle a line that is not the running turn's business: a response
    /// someone is awaiting, or an extension UI request. False for everything else.</summary>
    private bool Route(Pipe pipe, string line)
    {
        // Almost every line is a streaming delta; only parse the ones that can match.
        var response = line.Contains("\"response\"", StringComparison.Ordinal);
        var uiRequest = line.Contains("\"extension_ui_request\"", StringComparison.Ordinal);
        if (!response && !uiRequest) return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if (type == "extension_ui_request")
            {
                AnswerUiRequest(root);
                return true;
            }

            if (type == "response"
                && root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && pipe.Pending.TryRemove(id.GetString()!, out var pending))
            {
                pending.TrySetResult(root.Clone());
                return true;
            }
        }
        catch (JsonException)
        {
            // Not a protocol line; the turn decides what to do with it.
        }
        return false;
    }

    /// <summary>
    /// Extension UI requests are answered inline so the loop can't stall. Tool
    /// approval is handled inside <see cref="Tools.IChatToolHost"/> when the loopback
    /// callback executes, not over the RPC UI channel.
    /// </summary>
    private void AnswerUiRequest(JsonElement request)
    {
        if (!request.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return;
        var method = request.TryGetProperty("method", out var m) ? m.GetString() : null;
        // Dialog methods need an answer; fire-and-forget ones don't.
        if (method is "confirm")
            Send(new { type = "extension_ui_response", id = id.GetString(), confirmed = true });
        else if (method is "select" or "input" or "editor")
            Send(new { type = "extension_ui_response", id = id.GetString(), cancelled = true });
    }

    /// <summary>Send one command with an id and return its response. Safe mid-turn:
    /// the reply is routed here by id rather than read off the turn's stream.</summary>
    private Task<JsonElement> StartRequest(string type, object payload)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Pi sidecar 未启动。");
        var id = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pipe)
        {
            if (pipe.Exited) throw new InvalidOperationException("Pi sidecar 已退出。");
            pipe.Pending[id] = pending;
        }

        try
        {
            Send(Command(type, id, payload));
        }
        catch
        {
            pipe.Pending.TryRemove(id, out _);
            throw;
        }

        var response = CheckedAsync(type, pending.Task);
        // A caller that stops waiting leaves the reply behind; observe its failure.
        _ = response.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return response;
    }

    private static async Task<JsonElement> CheckedAsync(string type, Task<JsonElement> reply)
    {
        var root = await reply.ConfigureAwait(false);
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var error = root.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
            throw new InvalidOperationException($"Pi sidecar 拒绝了 {type}：{error}");
        }
        return root;
    }

    private Task<JsonElement> RequestAsync(string type, object payload, CancellationToken ct) =>
        StartRequest(type, payload).WaitAsync(ct);

    /// <summary>
    /// Queue a message into the running turn. <c>steer</c> lands after the current
    /// batch of tool calls, before the next model call; <c>follow_up</c> when the
    /// agent would otherwise stop, continuing the same run.
    ///
    /// Not awaited by the caller: the app holds the message until the run takes it
    /// in, and sends whatever is left as the next message once the turn ends. Only
    /// meaningful mid-turn — an idle sidecar holds the message until the next
    /// switch_session discards it.
    /// </summary>
    public bool Enqueue(string text, bool followUp)
    {
        if (!IsAlive || string.IsNullOrWhiteSpace(text)) return false;
        var pipe = _pipe!;
        Task<JsonElement> request;
        try
        {
            request = StartRequest(followUp ? "follow_up" : "steer", new { message = text });
        }
        catch (InvalidOperationException ex)
        {
            _log?.Invoke("[pi] 无法插入消息：" + ex.Message);
            return false;
        }

        lock (pipe.Enqueues) pipe.Enqueues.Add(request);
        _ = request.ContinueWith(task =>
        {
            lock (pipe.Enqueues) pipe.Enqueues.Remove(task);
            if (task.IsFaulted) _log?.Invoke("[pi] 插入的消息未被接受：" + task.Exception?.InnerException?.Message);
        }, TaskScheduler.Default);
        return true;
    }

    /// <summary>How long a clear waits for queued messages still on their way.</summary>
    private static readonly TimeSpan EnqueueSettleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wait until Pi has answered every steer and follow-up sent so far.
    ///
    /// Since Pi 0.99 both pass through the extension input hooks before they reach
    /// the queue, while clear_queue acts at once — so a clear sent right behind a
    /// message runs first and misses it.
    /// </summary>
    private static async Task WaitForEnqueuesAsync(Pipe pipe)
    {
        Task[] inflight;
        lock (pipe.Enqueues) inflight = [.. pipe.Enqueues];
        if (inflight.Length == 0) return;
        try
        {
            await Task.WhenAll(inflight).WaitAsync(EnqueueSettleTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A refused message is not queued, and a slow one is not worth holding
            // the clear for; either way, clear what is there.
        }
    }

    /// <summary>
    /// Take one message back out of the running turn's queue. False when Pi had
    /// already taken it in.
    ///
    /// Pi's queue has no "remove one": it is emptied and everything else goes back
    /// in order. What goes back is what the clear actually removed, not the app's
    /// view of the queue, which lags behind Pi and would re-send a message Pi had
    /// just taken in. A run that ends before they are back never reads them; the
    /// app still holds them and sends them as the next message.
    /// </summary>
    public async Task<bool> WithdrawAsync(string text, bool followUp, CancellationToken ct)
    {
        var pipe = _pipe;
        if (pipe is null || !IsAlive) return false;
        await WaitForEnqueuesAsync(pipe).ConfigureAwait(false);
        var cleared = await RequestAsync("clear_queue", new { }, ct).ConfigureAwait(false);

        var withdrawn = false;
        foreach (var (queued, isFollowUp) in QueuedMessages(cleared))
        {
            if (!withdrawn && isFollowUp == followUp && queued == text)
            {
                withdrawn = true;
                continue;
            }
            Enqueue(queued, isFollowUp);
        }
        return withdrawn;
    }

    private static IEnumerable<(string Text, bool FollowUp)> QueuedMessages(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var (key, followUp) in new[] { ("steering", false), ("followUp", true) })
        {
            if (!data.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in list.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    yield return (item.GetString()!, followUp);
        }
    }

    /// <summary>
    /// Point this sidecar at <paramref name="sessionPath"/>, spawning it first if
    /// it is not running yet. Returns true when the process was already alive, so
    /// the caller can tell a cold start from a warm hand-over.
    ///
    /// Pi opens the path whether or not it exists — a new conversation simply has
    /// no file until something is written — so one call covers both resuming an
    /// old transcript and starting a fresh one. Measured at ~60ms against a
    /// 1.25 MB transcript, versus ~2.7s to boot another process.
    /// </summary>
    public async Task<bool> SwitchSessionAsync(string sessionPath, CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wasAlive = IsAlive;
            await Task.Run(() =>
            {
                EnsureStarted();
                if (RepairMissingSessionWorkingDirectory(sessionPath, _launch.WorkingDirectory))
                    _log?.Invoke("[pi] 已迁移会话工作目录：" + sessionPath);
            }, ct).ConfigureAwait(false);
            await RequestAsync("switch_session", new { sessionPath }, ct).ConfigureAwait(false);
            _activeModel = null;
            _activeThinkingLevel = null;
            _autoRetryEnabled = false;
            return wasAlive;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Summarize the transcript now rather than waiting for the threshold.
    ///
    /// Takes the turn gate because it is a turn in all but name: it calls the model
    /// and it rewrites the history. Slow by nature — the summary is a model call —
    /// so the caller has to show it as work in progress, not as a click that
    /// appeared to do nothing.
    ///
    /// <paramref name="modelId"/> is selected first for the same reason a turn does
    /// it: a compaction that ran on whichever model the process happened to boot
    /// with would summarize the conversation using a model the user did not choose.
    /// </summary>
    public async Task<PiCompactionResult?> CompactAsync(
        string modelId,
        string? customInstructions,
        CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(EnsureStarted, ct).ConfigureAwait(false);

            if (!string.Equals(_activeModel, modelId, StringComparison.Ordinal))
            {
                await RequestAsync(
                    "set_model",
                    new { provider = PiWorkProvider.SidecarProviderId, modelId },
                    ct).ConfigureAwait(false);
                _activeModel = modelId;
            }

            var response = await RequestAsync(
                "compact",
                new { customInstructions },
                ct).ConfigureAwait(false);

            if (!response.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tokensBefore = data.TryGetProperty("tokensBefore", out var tb) && tb.TryGetInt32(out var before)
                ? before
                : 0;
            var summary = data.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            var tokensAfter =
                data.TryGetProperty("estimatedTokensAfter", out var ta) && ta.TryGetInt32(out var after)
                    ? after
                    : 0;
            return new PiCompactionResult(tokensBefore, summary, tokensAfter);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Turn Pi's automatic compaction on or off for this sidecar.
    ///
    /// Deliberately not cached on the session: a <c>switch_session</c> resets the
    /// sidecar to Pi's default (on), and a remembered "off" here would go stale
    /// without anything noticing. The preference belongs to the caller, which
    /// re-applies it — one owner, no half-truth. That owner is
    /// <see cref="PiRuntime.AutoCompactionEnabled"/>, re-sent on every lease.
    /// </summary>
    public async Task SetAutoCompactionAsync(bool enabled, CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsAlive) return;
            await RequestAsync("set_auto_compaction", new { enabled }, ct).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Older MolaGPT builds launched Pi from the downloaded runtime directory, so
    /// Pi persisted that versioned directory in every session header. Replacing a
    /// runtime removes the old directory and Pi then refuses to open the transcript.
    /// Only the stale header field is changed; every transcript entry is preserved.
    /// </summary>
    internal static bool RepairMissingSessionWorkingDirectory(
        string sessionPath,
        string workingDirectory)
    {
        if (!File.Exists(sessionPath)) return false;

        var lines = File.ReadAllLines(sessionPath);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return false;

        JsonObject? header;
        try
        {
            header = JsonNode.Parse(lines[0]) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (header is null
            || header["type"]?.GetValue<string>() != "session"
            || header["cwd"] is not JsonValue cwdValue
            || !cwdValue.TryGetValue<string>(out var storedWorkingDirectory)
            || string.IsNullOrWhiteSpace(storedWorkingDirectory)
            || Directory.Exists(storedWorkingDirectory))
        {
            return false;
        }

        header["cwd"] = workingDirectory;
        lines[0] = header.ToJsonString(CommandJsonOptions);

        var temporaryPath = sessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(false));
            File.Move(temporaryPath, sessionPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return true;
    }

    /// <summary>
    /// Send one user turn and stream the raw Pi RPC event lines (JSONL) until the
    /// run settles. Serialized: Work drives one turn at a time per conversation.
    /// </summary>
    public async IAsyncEnumerable<string> SendTurnAsync(
        string modelId,
        string thinkingLevel,
        string userText,
        IReadOnlyList<PiImage> images,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        Pipe? pipe = null;
        Channel<string>? events = null;
        try
        {
            // Spawn + prompt run off the caller's thread on purpose. Writing to the
            // sidecar's stdin is a synchronous pipe write, and a prompt carrying an
            // attachment's extracted text easily exceeds the pipe buffer — the write
            // then blocks until Node drains it, which on a cold sidecar means
            // waiting out the whole ~1.3s Node boot. Callers reach this from an
            // `await foreach`, whose iterator body runs on the caller's thread until
            // the first real suspension, so doing this inline froze the UI.
            await Task.Run(EnsureStarted, ct).ConfigureAwait(false);
            pipe = _pipe!;

            // Re-sent whenever the model changes — and after every session switch,
            // which re-creates the runtime and forgets the selection. The whole
            // model list is registered at spawn, so this is a selection rather than
            // a reason to start another process.
            //
            // Deliberately awaited rather than fired off: an unregistered model is
            // answered with an error the stream would otherwise swallow, and the
            // turn would then run on whichever model the process booted with. A
            // wrong-model answer that looks completely normal is worse than a
            // failure, and it is exactly what an out-of-date sidecar extension —
            // one that still registers a single model — would produce.
            if (!string.Equals(_activeModel, modelId, StringComparison.Ordinal))
            {
                await RequestAsync(
                    "set_model",
                    new { provider = PiWorkProvider.SidecarProviderId, modelId },
                    ct).ConfigureAwait(false);
                _activeModel = modelId;
            }

            events = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            lock (pipe)
            {
                if (pipe.Exited) events.Writer.TryComplete();
                else pipe.Events = events;
            }

            var promptId = Guid.NewGuid().ToString("N");
            await Task.Run(() =>
            {
                // Fire-and-forget, unlike set_model: getting these wrong degrades a
                // setting, it does not produce an answer from the wrong model. Both
                // are re-sent after a session switch, which rebuilds the runtime and
                // forgets them.
                if (!string.Equals(_activeThinkingLevel, thinkingLevel, StringComparison.Ordinal))
                {
                    Send(new { type = "set_thinking_level", level = thinkingLevel });
                    _activeThinkingLevel = thinkingLevel;
                }
                if (!_autoRetryEnabled)
                {
                    // Let Pi ride out a provider hiccup instead of surfacing it as a
                    // failed turn the user has to retry by hand.
                    Send(new { type = "set_auto_retry", enabled = true });
                    _autoRetryEnabled = true;
                }

                // Images ride on the prompt command rather than being flattened into
                // text: dropping them would silently cost vision, which the direct
                // provider supports.
                Send(images.Count > 0
                    ? Command("prompt", promptId, new { message = userText, images })
                    : Command("prompt", promptId, new { message = userText }));
            }, ct).ConfigureAwait(false);

            await foreach (var line in events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // The prompt's own reply is read here rather than awaited: a rejected
                // prompt starts no run, so no agent_settled would ever end the loop.
                if (line.Contains(promptId, StringComparison.Ordinal) && ReadResponse(line) is { } reply)
                {
                    if (!reply.Success) throw new InvalidOperationException("Pi 未接受本轮输入：" + reply.Error);
                    // Consumed by an extension input handler: no run starts.
                    if (reply.Disposition == "handled") yield break;
                    continue;
                }

                yield return line;

                if (IsSettled(line)) yield break;
            }
        }
        finally
        {
            // Stopping has to reach Pi. Cancelling only ends this read loop; the
            // sidecar would keep running the turn — still calling the model, still
            // calling tools whose callbacks now have nowhere to go — and keep
            // writing events. The next turn would then read the abandoned turn's
            // output, leaving the stream a whole turn out of step and the UI
            // waiting forever for a reply that already came and went.
            if (ct.IsCancellationRequested && pipe is not null && events is not null)
                await AbortAndDrainAsync(pipe, events).ConfigureAwait(false);

            if (pipe is not null)
            {
                lock (pipe)
                {
                    if (ReferenceEquals(pipe.Events, events)) pipe.Events = null;
                }
            }

            _turnGate.Release();
        }
    }

    private sealed record PromptReply(bool Success, string? Error, string? Disposition);

    private static PromptReply? ReadResponse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "response") return null;
            var success = !(root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False);
            var error = root.TryGetProperty("error", out var e) ? e.ToString() : null;
            var disposition = root.TryGetProperty("data", out var data)
                              && data.ValueKind == JsonValueKind.Object
                              && data.TryGetProperty("disposition", out var d)
                              && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            return new PromptReply(success, error, disposition);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tell Pi to stop and read until the turn settles, so the stream is back at a
    /// turn boundary before anyone sends the next prompt. Bounded: if the sidecar
    /// does not settle, kill it rather than hand out a session in an unknown state —
    /// the next turn respawns and resumes from the persisted session.
    /// </summary>
    private async Task AbortAndDrainAsync(Pipe pipe, Channel<string> events)
    {
        if (!IsAlive) return;

        try
        {
            // A message still on its way would land after the clear below and be
            // written to the transcript after the abort, while the app has put its
            // text back in the input box.
            await WaitForEnqueuesAsync(pipe).ConfigureAwait(false);

            // First, or Pi carries on with whatever was queued into the turn once
            // the abort lands. The app puts that text back in the input box.
            Send(new { type = "clear_queue" });
            Send(new { type = "abort" });
            // Auto-retry means the agent can be waiting to try again rather than
            // running; aborting the turn alone leaves that timer to fire.
            Send(new { type = "abort_retry" });

            using var cts = new CancellationTokenSource(AbortDrainTimeout);
            await foreach (var line in events.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                if (IsSettled(line)) return;
            // The channel completes when the process exits.
        }
        catch (Exception ex)
        {
            _log?.Invoke("[pi] 中止后未能在超时内回到空闲，重启 sidecar：" + ex.Message);
            if (ReferenceEquals(_pipe, pipe)) _pipe = null;
            try { if (!pipe.Process.HasExited) pipe.Process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            pipe.Process.Dispose();
        }
    }

    /// <summary>How long to wait for Pi to wind down after an abort. Long enough for
    /// an in-flight tool call to return, short enough that a wedged sidecar does not
    /// hold the conversation hostage.</summary>
    private static readonly TimeSpan AbortDrainTimeout = TimeSpan.FromSeconds(10);

    private static bool IsSettled(string line)
    {
        if (!line.Contains("\"agent_settled\"", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "agent_settled";
        }
        catch (JsonException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _turnGate.Dispose();
        var pipe = _pipe;
        _pipe = null;
        if (pipe is null) return;
        try { if (!pipe.Process.HasExited) pipe.Process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
        try { pipe.Process.Dispose(); }
        catch { /* ignore */ }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>What a compaction actually did.</summary>
/// <param name="TokensBefore">Context size at the moment of the cut. There is no
/// "after" to pair it with: the next honest measurement only exists once the model
/// has replied again, which is why the gauge goes unknown rather than to zero.</param>
/// <param name="EstimatedTokensAfter">Pi's own estimate of the compacted history's
/// size, from a character heuristic rather than the model — 0 on runtimes that do
/// not report it.</param>
public sealed record PiCompactionResult(
    int TokensBefore,
    string? Summary,
    int EstimatedTokensAfter = 0);

/// <summary>One image on a prompt, in Pi's <c>ImageContent</c> shape. Property
/// names are lower-case because they go on the wire as-is.</summary>
public sealed record PiImage(string data, string mimeType)
{
    public string type => "image";
}

/// <summary>Everything needed to spawn one sidecar process. Deliberately carries
/// nothing conversation-specific: which transcript the process is working on is
/// chosen per turn with <see cref="PiSidecarSession.SwitchSessionAsync"/>.</summary>
/// <param name="SessionRoot">Directory holding Pi's session files. Created up
/// front so the first <c>switch_session</c> has somewhere to land.</param>
/// <param name="ModelsJson">The provider's whole model list in Pi's
/// <c>ProviderConfigInput.models</c> shape, carrying each model's wire api and
/// compatibility profile. Registered up front so switching models mid-conversation
/// is a <c>set_model</c> rather than a respawn.</param>
/// <param name="AgentDirectory">Pi's config directory (<c>PI_CODING_AGENT_DIR</c>),
/// owned by MolaGPT rather than shared with the user's own Pi.</param>
public sealed record PiSidecarLaunchOptions(
    string NodePath,
    string CliJsPath,
    string ExtensionPath,
    string WorkingDirectory,
    string SessionRoot,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Api,
    bool AuthHeader,
    string ToolCallbackUrl,
    string ToolCallbackToken,
    string ModelsJson,
    string AgentDirectory);
