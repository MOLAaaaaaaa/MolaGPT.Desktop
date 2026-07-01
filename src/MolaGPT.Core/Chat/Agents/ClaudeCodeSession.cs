using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// A live Claude Code session over the bidirectional stream-json protocol.
/// One persistent process retains context across turns. A single background
/// loop reads stdout NDJSON into a channel; <see cref="SendTurnAsync"/> writes a
/// user message to stdin and drains normalized events until that turn's
/// <c>result</c> marker arrives.
/// </summary>
internal sealed partial class ClaudeCodeSession : IAgentSession
{
    private readonly Process _process;
    private readonly Channel<AgentEvent> _channel;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, JsonElement> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly Task _readerTask;
    private readonly StringBuilder _stderr = new();
    private volatile string? _currentSessionId;
    private int _disposed;

    public string BackendId => ClaudeCodeBackend.BackendId;
    public bool IsAlive => _disposed == 0 && !SafeHasExited();

    /// <summary>Claude's own session id, captured from the <c>system/init</c>
    /// message. The bridge reads this after a turn to resume the right session
    /// next spawn (Claude may rotate the id when resuming with --fork-session, and
    /// self-assigns one when we start fresh without --session-id).</summary>
    public string? CurrentSessionId => _currentSessionId;

    public ClaudeCodeSession(Process process)
    {
        _process = process;
        _channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (_stderr) _stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();

        _readerTask = Task.Run(() => ReadLoopAsync(_lifetimeCts.Token));
    }

    public async IAsyncEnumerable<AgentEvent> SendTurnAsync(
        AgentTurnInput input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(ClaudeCodeSession));

        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteUserMessageAsync(input, ct).ConfigureAwait(false);

            while (true)
            {
                AgentEvent ev;
                var closed = false;
                try
                {
                    ev = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
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
            _turnGate.Release();
        }
    }

    public async Task ApproveAsync(string permissionId, AgentPermissionChoice choice, CancellationToken ct)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(ClaudeCodeSession));
        if (!_pendingApprovals.TryRemove(permissionId, out var originalInput))
            throw new InvalidOperationException($"Claude approval request is no longer pending: {permissionId}");

        // stream-json control protocol: reply to the can_use_tool request. Allow
        // must echo the (possibly modified) input as updatedInput; deny must carry
        // a message. Once/Always both allow — rule persistence is a later refinement.
        object inner = choice == AgentPermissionChoice.Deny
            ? new { behavior = "deny", message = "用户拒绝了该操作。" }
            : new { behavior = "allow", updatedInput = originalInput };

        var payload = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = permissionId,
                response = inner
            }
        });
        await WriteLineAsync(payload, ct).ConfigureAwait(false);
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        if (_disposed != 0 || SafeHasExited()) return;
        // Control-protocol interrupt request on stdin.
        var payload = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = Guid.NewGuid().ToString("N"),
            request = new { subtype = "interrupt" }
        });
        try
        {
            await WriteLineAsync(payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* process gone */ }
    }

    private async Task WriteUserMessageAsync(AgentTurnInput input, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = BuildUserContent(input) }
        });
        await WriteLineAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>Serialize all stdin writes (user turns, interrupts, approvals) so
    /// concurrent command dispatch can't interleave half-lines into the CLI.</summary>
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

    private static object BuildUserContent(AgentTurnInput input)
    {
        if (input.Images.Count == 0)
            return input.Text;

        var blocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(input.Text))
            blocks.Add(new { type = "text", text = input.Text });

        foreach (var image in input.Images)
        {
            if (TryBuildClaudeImageBlock(image, out var block))
                blocks.Add(block);
            else
                blocks.Add(new { type = "text", text = $"[image omitted: unsupported image reference {image}]" });
        }

        return blocks;
    }

    private static bool TryBuildClaudeImageBlock(string source, out object block)
    {
        block = new { };
        if (string.IsNullOrWhiteSpace(source))
            return false;

        if (TryParseDataUrl(source, out var mediaType, out var data))
        {
            block = new
            {
                type = "image",
                source = new { type = "base64", media_type = mediaType, data }
            };
            return true;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            block = new { type = "image", source = new { type = "url", url = source } };
            return true;
        }

        try
        {
            if (File.Exists(source))
            {
                var ext = Path.GetExtension(source).ToLowerInvariant();
                var mime = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    _ => "image/png"
                };
                block = new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = mime,
                        data = Convert.ToBase64String(File.ReadAllBytes(source))
                    }
                };
                return true;
            }
        }
        catch { /* fall through to omitted marker */ }

        return false;
    }

    private static bool TryParseDataUrl(string source, out string mediaType, out string data)
    {
        mediaType = "image/png";
        data = string.Empty;
        const string marker = ";base64,";
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        var idx = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx <= "data:".Length)
            return false;
        mediaType = source["data:".Length..idx];
        data = source[(idx + marker.Length)..];
        return mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && data.Length > 0;
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
        var code = SafeHasExited() ? _process.ExitCode.ToString() : "?";
        return string.IsNullOrEmpty(tail)
            ? $"Claude Code process ended unexpectedly (exit {code})."
            : $"Claude Code process ended (exit {code}): {tail}";
    }

    // ReadLoop + mapping live in the partial below.
}
