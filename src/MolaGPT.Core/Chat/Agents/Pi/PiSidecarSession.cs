using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private string? _activeModel;
    private string? _activeThinkingLevel;
    private bool _autoRetryEnabled;

    public PiSidecarSession(PiSidecarLaunchOptions launch, Action<string>? log = null)
    {
        _launch = launch;
        _log = log;
    }

    public bool IsAlive => _process is { HasExited: false };

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
                     // directory. Explicit `-e` above is unaffected.
                     "--no-extensions", "--no-skills", "--no-prompt-templates",
                     "--no-themes", "--no-context-files",

                     // No startup network chatter (model-catalog refresh etc.); the
                     // model list is MolaGPT's, and this keeps first-turn latency down.
                     "--offline",
                 })
            psi.ArgumentList.Add(arg);

        psi.Environment["MOLA_PROVIDER_BASE_URL"] = _launch.BaseUrl;
        psi.Environment["MOLA_PROVIDER_API_KEY"] = _launch.ApiKey;
        psi.Environment["MOLA_PROVIDER_MODEL"] = _launch.Model;
        psi.Environment["MOLA_PROVIDER_API"] = _launch.Api;
        psi.Environment["MOLA_PROVIDER_AUTH_HEADER"] = _launch.AuthHeader ? "true" : "false";
        psi.Environment["MOLA_PROVIDER_REASONING"] = _launch.Reasoning ? "true" : "false";
        psi.Environment["MOLA_TOOL_CALLBACK_URL"] = _launch.ToolCallbackUrl;
        psi.Environment["MOLA_TOOL_TOKEN"] = _launch.ToolCallbackToken;
        if (!string.IsNullOrWhiteSpace(_launch.ModelsJson))
            psi.Environment["MOLA_PROVIDER_MODELS"] = _launch.ModelsJson!;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Pi sidecar（node 未找到？）");
        _process = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;
        _activeModel = null;
        _activeThinkingLevel = null;
        _autoRetryEnabled = false;

        // Drain stderr so the pipe never blocks; forward diagnostics to the log.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                _log?.Invoke("[pi] " + line);
        });
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
        lock (_stdinLock)
        {
            _stdin!.WriteLine(json);
            _stdin.Flush();
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
    /// Takes the turn gate because it is a turn in all but name: it calls the model,
    /// it rewrites the history, and it shares the one stdout reader. Slow by nature —
    /// the summary is a model call — so the caller has to show it as work in
    /// progress, not as a click that appeared to do nothing.
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
                using (await RequestAsync(
                           "set_model",
                           new { provider = PiWorkProvider.SidecarProviderId, modelId },
                           ct).ConfigureAwait(false))
                {
                }
                _activeModel = modelId;
            }

            using var response = await RequestAsync(
                "compact",
                new { customInstructions },
                ct).ConfigureAwait(false);

            if (!response.RootElement.TryGetProperty("data", out var data)
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
            using (await RequestAsync("set_auto_compaction", new { enabled }, ct).ConfigureAwait(false))
            {
            }
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

    /// <summary>Send one command and read until its response arrives, discarding
    /// unrelated traffic. Only safe between turns — the caller must hold the turn
    /// gate, since it shares the single stdout reader with the streaming path.</summary>
    private async Task<JsonDocument> RequestAsync(string type, object payload, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var command = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = type, ["id"] = id };
        foreach (var property in payload.GetType().GetProperties())
            command[property.Name] = property.GetValue(payload);
        Send(command);

        var reader = _stdout!;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Pi sidecar 在响应 {type} 前退出。");
            if (TryHandleUiRequest(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }

            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.String
                && responseId.GetString() == id)
            {
                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    var error = root.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
                    doc.Dispose();
                    throw new InvalidOperationException($"Pi sidecar 拒绝了 {type}：{error}");
                }
                return doc;
            }
            doc.Dispose();
        }
    }

    /// <summary>
    /// Send one user turn and stream the raw Pi RPC event lines (JSONL) until the
    /// run settles. Serialized: Work drives one turn at a time per conversation.
    /// Extension UI requests (e.g. select) are auto-cancelled so the agent never
    /// hangs — tool approval is handled inside <see cref="Tools.IChatToolHost"/>
    /// when the loopback callback executes, not over the RPC UI channel.
    /// </summary>
    public async IAsyncEnumerable<string> SendTurnAsync(
        string modelId,
        string thinkingLevel,
        string userText,
        IReadOnlyList<PiImage> images,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
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
                using (await RequestAsync(
                           "set_model",
                           new { provider = PiWorkProvider.SidecarProviderId, modelId },
                           ct).ConfigureAwait(false))
                {
                }
                _activeModel = modelId;
            }

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
            }, ct).ConfigureAwait(false);

            await Task.Run(() =>
            {
                // Images ride on the prompt command rather than being flattened into
                // text: dropping them would silently cost vision, which the direct
                // provider supports.
                if (images.Count > 0)
                    Send(new { type = "prompt", message = userText, images });
                else
                    Send(new { type = "prompt", message = userText });
            }, ct).ConfigureAwait(false);

            var reader = _stdout!;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) yield break;          // process exited
                line = line.TrimEnd('\r');
                if (line.Length == 0) continue;

                // Answer/close any extension UI request inline so the loop can't stall.
                if (TryHandleUiRequest(line)) continue;

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
            if (ct.IsCancellationRequested)
                await AbortAndDrainAsync().ConfigureAwait(false);

            _turnGate.Release();
        }
    }

    /// <summary>
    /// Tell Pi to stop and read until the turn settles, so the stream is back at a
    /// turn boundary before anyone sends the next prompt. Bounded: if the sidecar
    /// does not settle, kill it rather than hand out a session in an unknown state —
    /// the next turn respawns and resumes from the persisted session.
    /// </summary>
    private async Task AbortAndDrainAsync()
    {
        if (!IsAlive) return;

        try
        {
            Send(new { type = "abort" });
            // Auto-retry means the agent can be waiting to try again rather than
            // running; aborting the turn alone leaves that timer to fire.
            Send(new { type = "abort_retry" });

            using var cts = new CancellationTokenSource(AbortDrainTimeout);
            var reader = _stdout!;
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line is null) return;                       // process exited
                if (TryHandleUiRequest(line)) continue;
                if (IsSettled(line)) return;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("[pi] 中止后未能在超时内回到空闲，重启 sidecar：" + ex.Message);
            var proc = _process;
            _process = null;
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            proc?.Dispose();
        }
    }

    /// <summary>How long to wait for Pi to wind down after an abort. Long enough for
    /// an in-flight tool call to return, short enough that a wedged sidecar does not
    /// hold the conversation hostage.</summary>
    private static readonly TimeSpan AbortDrainTimeout = TimeSpan.FromSeconds(10);

    private bool TryHandleUiRequest(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "extension_ui_request")
            {
                if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var method = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
                    // Dialog methods need an answer; fire-and-forget ones don't.
                    if (method is "confirm")
                        Send(new { type = "extension_ui_response", id = id.GetString(), confirmed = true });
                    else if (method is "select" or "input" or "editor")
                        Send(new { type = "extension_ui_response", id = id.GetString(), cancelled = true });
                }
                return true;
            }
        }
        catch { /* not our concern; let the normal path see it */ }
        return false;
    }

    private static bool IsSettled(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "agent_settled";
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _turnGate.Dispose();
        var proc = _process;
        _process = null;
        if (proc is null) return;
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
        try { proc.Dispose(); }
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
    bool Reasoning,
    string ToolCallbackUrl,
    string ToolCallbackToken,
    string? ModelsJson = null);
