using System.Diagnostics;
using System.Text;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Claude Code backend. Drives the <c>claude</c> CLI as a persistent process in
/// bidirectional stream-json mode:
///   <c>claude -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages</c>
/// User turns are written to stdin as JSON lines; assistant output, tool calls,
/// and the per-turn <c>result</c> marker arrive on stdout as NDJSON.
/// </summary>
public sealed class ClaudeCodeBackend : IAgentBackend
{
    public const string BackendId = "claude-code";
    public string Id => BackendId;
    public string DisplayName => "Claude Code";

    public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ResolvedExecutablePath))
            throw new InvalidOperationException("Claude Code executable path was not resolved.");
        if (!Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException($"Working directory not found: {options.WorkingDirectory}");

        var process = CreateProcess(options);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the Claude Code process.");
        }

        var session = new ClaudeCodeSession(process);
        // Kick off the initialize handshake to discover the model catalog
        // (initialize.models) in the background — the phone's picker reads it via
        // session meta; falls back to observed ids if it isn't ready yet.
        session.BeginInitialize();
        return Task.FromResult<IAgentSession>(session);
    }

    private static Process CreateProcess(AgentSessionOptions options)
    {
        var process = new Process
        {
            StartInfo =
            {
                FileName = options.ResolvedExecutablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = options.WorkingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // BOM-less UTF-8: Encoding.UTF8 would prepend a BOM to the first
                // stdin line, which the stream-json parser rejects.
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            },
            EnableRaisingEvents = true
        };

        var args = process.StartInfo.ArgumentList;
        args.Add("-p");
        args.Add("--input-format");
        args.Add("stream-json");
        args.Add("--output-format");
        args.Add("stream-json");
        args.Add("--verbose");
        args.Add("--include-partial-messages");
        args.Add("--permission-mode");
        args.Add(MapPermissionMode(options.PermissionMode));
        // Route tool-permission prompts through the stream-json control protocol:
        // when a non-auto-approved tool fires, Claude emits a `control_request`
        // {subtype: can_use_tool} on stdout and blocks for our `control_response`
        // on stdin. Without this, gated tools are silently auto-handled and the
        // phone never sees an approval card. (Flag is valid on 2.1.x though hidden
        // from --help.)
        args.Add("--permission-prompt-tool");
        args.Add("stdio");

        // Resume an existing conversation by session id (keeps full context, reuses
        // the same id — no --fork-session). Otherwise bind a brand-new session to
        // our stable conversation id so the on-disk .jsonl matches what we track.
        if (!string.IsNullOrWhiteSpace(options.ResumeSessionId))
        {
            args.Add("--resume");
            args.Add(options.ResumeSessionId);
        }
        else if (IsValidSessionId(options.SessionId))
        {
            args.Add("--session-id");
            args.Add(options.SessionId!);
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            args.Add("--model");
            args.Add(options.Model);
        }

        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            args.Add("--effort");
            args.Add(options.ReasoningEffort);
        }

        if (!string.IsNullOrWhiteSpace(options.SystemPromptAppend))
        {
            args.Add("--append-system-prompt");
            args.Add(options.SystemPromptAppend);
        }

        // The working directory is already cwd; make it explicitly tool-accessible.
        args.Add("--add-dir");
        args.Add(options.WorkingDirectory);

        // Inherit the environment (PATH, ANTHROPIC_API_KEY / OAuth) — unlike the
        // Python tool, the agent CLI needs the user's real auth + toolchain.
        return process;
    }

    /// <summary>Map our mode onto Claude Code's <c>--permission-mode</c> choices
    /// (2.1.x: acceptEdits / auto / bypassPermissions / default / dontAsk / plan).</summary>
    private static string MapPermissionMode(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Plan => "plan",
        AgentPermissionMode.AcceptEdits => "acceptEdits",
        AgentPermissionMode.BypassPermissions => "bypassPermissions",
        _ => "default"
    };

    /// <summary>Claude's <c>--session-id</c> requires a canonical UUID; reject the
    /// dash-less "N" form and anything non-UUID so we don't fail the spawn.</summary>
    private static bool IsValidSessionId(string? id)
        => Guid.TryParseExact(id, "D", out _);
}
