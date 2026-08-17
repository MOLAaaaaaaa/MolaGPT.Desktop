using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Owns one persistent <c>pi --mode rpc</c> Node subprocess for a single
/// conversation and speaks Pi's JSONL RPC over stdin/stdout. Long-lived and
/// reused across turns (spawned lazily, torn down when the conversation closes
/// or goes idle) so the ~80–150 MB Node+Pi footprint is paid once per active
/// Work conversation — never per turn, never in Chat/BYOK-direct mode.
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
    private bool _modelSet;

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
        Directory.CreateDirectory(_launch.SessionDir);

        foreach (var arg in new[]
                 {
                     _launch.CliJsPath, "--mode", "rpc",

                     // Persist Pi's own session, keyed by the MolaGPT conversation.
                     // This is NOT optional bookkeeping: the provider only sends the
                     // latest user message and lets Pi own the history, so an
                     // ephemeral (--no-session) sidecar loses the entire conversation
                     // whenever it respawns — on idle reclaim, a model switch, a tool
                     // toggle, or an app restart. `--session-id` resumes if the file
                     // exists and creates it otherwise.
                     "--session-id", _launch.SessionId,
                     "--session-dir", _launch.SessionDir,

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

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Pi sidecar（node 未找到？）");
        _process = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;
        _modelSet = false;

        // Drain stderr so the pipe never blocks; forward diagnostics to the log.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                _log?.Invoke("[pi] " + line);
        });
    }

    private void Send(object command)
    {
        var json = JsonSerializer.Serialize(command);
        lock (_stdinLock)
        {
            _stdin!.WriteLine(json);
            _stdin.Flush();
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
        string userText,
        IReadOnlyList<PiImage> images,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            if (!_modelSet)
            {
                Send(new { type = "set_model", provider = PiWorkProvider.SidecarProviderId, modelId = _launch.Model });
                _modelSet = true;
            }
            // Images ride on the prompt command rather than being flattened into
            // text: dropping them would silently cost vision, which the direct
            // provider supports.
            if (images.Count > 0)
                Send(new { type = "prompt", message = userText, images });
            else
                Send(new { type = "prompt", message = userText });

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

/// <summary>One image on a prompt, in Pi's <c>ImageContent</c> shape. Property
/// names are lower-case because they go on the wire as-is.</summary>
public sealed record PiImage(string data, string mimeType)
{
    public string type => "image";
}

/// <summary>Everything needed to spawn one sidecar process.</summary>
/// <param name="SessionId">Pi session id — the MolaGPT conversation id, sanitised
/// (it becomes a <c>&lt;id&gt;.jsonl</c> filename). Stable across respawns so the
/// conversation survives them.</param>
/// <param name="SessionDir">Directory holding Pi's session files.</param>
public sealed record PiSidecarLaunchOptions(
    string NodePath,
    string CliJsPath,
    string ExtensionPath,
    string WorkingDirectory,
    string SessionId,
    string SessionDir,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Api,
    bool AuthHeader,
    bool Reasoning,
    string ToolCallbackUrl,
    string ToolCallbackToken);
