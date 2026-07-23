using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// A live Codex session over JSON-RPC 2.0 on stdio. A single background loop
/// reads stdout JSONL and routes each message either to a pending-request
/// completion (by <c>id</c>) or, for notifications, to the active turn's event
/// channel. One thread is started at handshake; each <see cref="SendTurnAsync"/>
/// issues a <c>turn/start</c> and drains notifications until <c>turn/completed</c>.
/// </summary>
internal sealed partial class CodexSession : IAgentSession
{
    private readonly Process _process;
    private readonly AgentSessionOptions _options;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _stderr = new();

    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Lock _pendingGate = new();
    private readonly ConcurrentDictionary<string, PendingApprovalRequest> _pendingApprovals = new(StringComparer.Ordinal);
    private long _nextId;

    private Task _readerTask = Task.CompletedTask;
    private string? _threadId;
    private int _disposed;

    /// <summary>Current model override, seeded from options and mutated live by
    /// <see cref="SetModelAsync"/>. Codex has no in-flight "set model" control, but
    /// it applies a per-turn <c>model</c> override — so switching just updates this
    /// field and the next <c>turn/start</c> picks it up (no process restart).</summary>
    private volatile string? _currentModel;

    // The channel for the turn currently in flight (null between turns).
    private volatile Channel<AgentEvent>? _turnChannel;

    public string BackendId => CodexBackend.BackendId;
    public bool IsAlive => _disposed == 0 && !SafeHasExited();
    public bool IsTurnActive => _turnGate.CurrentCount == 0;

    /// <summary>Codex's app-server thread id — stable across resume, so the bridge
    /// can keep tracking the same conversation.</summary>
    public string? CurrentSessionId => _threadId;

    public CodexSession(Process process, AgentSessionOptions options)
    {
        _process = process;
        _options = options;
        _currentModel = options.Model;

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (_stderr)
                {
                    _stderr.AppendLine(e.Data);
                    // Stderr only feeds BuildExitError; cap it like ClaudeCodeSession.
                    if (_stderr.Length > 32 * 1024)
                        _stderr.Remove(0, _stderr.Length - 16 * 1024);
                }
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>Run initialize → initialized → thread/start.</summary>
    public async Task HandshakeAsync(CancellationToken ct)
    {
        _readerTask = Task.Run(() => ReadLoopAsync(_lifetimeCts.Token));

        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "molagpt_desktop", title = "MolaGPT Desktop", version = "1.0.0" }
        }, ct).ConfigureAwait(false);

        await NotifyAsync("initialized", new { }, ct).ConfigureAwait(false);

        // Resume an existing thread when asked; otherwise start a fresh one. Both
        // carry the sandbox + approval posture and an optional model override.
        if (!string.IsNullOrWhiteSpace(_options.ResumeSessionId))
        {
            var resumeResult = await RequestAsync("thread/resume", WithModel(new Dictionary<string, object?>
            {
                ["threadId"] = _options.ResumeSessionId,
                ["sandbox"] = CodexBackend.MapSandboxPolicy(_options.PermissionMode),
                ["approvalPolicy"] = CodexBackend.MapApprovalPolicy(_options.ApprovalPolicy ?? CodexApprovalPolicy.OnRequest)
            }), ct).ConfigureAwait(false);
            _threadId = ExtractThreadId(resumeResult) ?? _options.ResumeSessionId;
            return;
        }

        var startResult = await RequestAsync("thread/start", WithModel(new Dictionary<string, object?>
        {
            ["cwd"] = _options.WorkingDirectory,
            ["sandbox"] = CodexBackend.MapSandboxPolicy(_options.PermissionMode),
            ["approvalPolicy"] = CodexBackend.MapApprovalPolicy(_options.ApprovalPolicy ?? CodexApprovalPolicy.OnRequest)
        }), ct).ConfigureAwait(false);

        _threadId = ExtractThreadId(startResult)
            ?? throw new InvalidOperationException("Codex thread/start did not return a thread id.");

        // Discover the real model catalog (model/list) in the background for the
        // phone's picker; best-effort, never blocks the handshake.
        BeginDiscoverModels();
    }

    /// <summary>Attach the model override to a params bag only when one is set, so a
    /// null model leaves Codex on its configured default.</summary>
    private Dictionary<string, object?> WithModel(Dictionary<string, object?> p)
    {
        if (!string.IsNullOrWhiteSpace(_currentModel)) p["model"] = _currentModel;
        return p;
    }

    /// <summary>Turn-level overrides are how app-server applies reasoning effort.
    /// Model is repeated here too so resumed threads pick up the current override.</summary>
    private Dictionary<string, object?> WithTurnOptions(Dictionary<string, object?> p)
    {
        if (!string.IsNullOrWhiteSpace(_currentModel)) p["model"] = _currentModel;
        if (!string.IsNullOrWhiteSpace(_options.ReasoningEffort)) p["effort"] = _options.ReasoningEffort;
        return p;
    }

    public async IAsyncEnumerable<AgentEvent> SendTurnAsync(
        AgentTurnInput input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(CodexSession));
        if (_threadId is null) throw new InvalidOperationException("Codex session was not initialized.");

        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        _turnChannel = channel;
        // Approval requests from a previous turn are dead once that turn ended
        // (app-server cancels them); drop them so abandoned prompts don't leak.
        _pendingApprovals.Clear();
        try
        {
            // Fire turn/start. The response only confirms the turn object; the
            // actual streaming arrives as notifications routed into the channel.
            string? startError = null;
            try
            {
                await RequestAsync("turn/start", WithTurnOptions(new Dictionary<string, object?>
                {
                    ["threadId"] = _threadId,
                    ["input"] = BuildTurnInput(input)
                }), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                startError = ex.Message;
            }

            if (startError is not null)
            {
                yield return AgentEvent.Failure($"Codex turn/start failed: {startError}");
                yield break;
            }

            while (true)
            {
                AgentEvent ev;
                var closed = false;
                try
                {
                    ev = await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    closed = true;
                    ev = AgentEvent.Failure(BuildExitError());
                }

                yield return ev;

                if (closed || ev.Kind is AgentEventKind.TurnComplete or AgentEventKind.Error)
                    yield break;
            }
        }
        finally
        {
            _turnChannel = null;
            _turnGate.Release();
        }
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        if (_disposed != 0 || SafeHasExited() || _threadId is null) return;
        try
        {
            await RequestAsync("turn/interrupt", new { threadId = _threadId }, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    public async Task ApproveAsync(string permissionId, AgentPermissionChoice choice, CancellationToken ct)
    {
        if (!_pendingApprovals.TryRemove(permissionId, out var request))
            throw new InvalidOperationException($"Codex approval request is no longer pending: {permissionId}");

        var result = BuildApprovalResult(request.Method, request.Params, choice);
        await RespondAsync(request.JsonRpcId, result, ct).ConfigureAwait(false);
    }

    private static object[] BuildTurnInput(AgentTurnInput input)
    {
        var items = new List<object>();
        if (!string.IsNullOrWhiteSpace(input.Text))
            items.Add(new { type = "text", text = input.Text, text_elements = Array.Empty<object>() });

        foreach (var image in input.Images)
        {
            if (string.IsNullOrWhiteSpace(image)) continue;
            if (IsLikelyLocalPath(image))
                items.Add(new { type = "localImage", path = image });
            else
                items.Add(new { type = "image", url = image });
        }

        if (items.Count == 0)
            items.Add(new { type = "text", text = string.Empty, text_elements = Array.Empty<object>() });
        return items.ToArray();
    }

    private static bool IsLikelyLocalPath(string value)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return uri.IsFile;
        return Path.IsPathRooted(value);
    }

    private bool SafeHasExited()
    {
        try { return _process.HasExited; }
        catch { return true; }
    }

    private string BuildExitError()
    {
        string tail;
        lock (_stderr) tail = _stderr.ToString().Trim();
        return string.IsNullOrEmpty(tail)
            ? "Codex app-server process ended unexpectedly."
            : $"Codex app-server process ended: {tail}";
    }

    private sealed record PendingApprovalRequest(JsonElement JsonRpcId, string Method, JsonElement Params);
}
