using System.Diagnostics;
using System.Text;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Codex backend. Drives the <c>codex app-server</c> process over newline-delimited
/// JSON-RPC 2.0 on stdio (the same interface the Codex VS Code extension uses):
///   <c>codex app-server</c>  (stdio is the default transport)
/// Lifecycle: initialize → initialized → thread/start → turn/start, streaming
/// item/* and turn/completed notifications. Wire-format differences from Claude
/// Code are fully contained in <see cref="CodexSession"/>.
/// </summary>
public sealed class CodexBackend : IAgentBackend
{
    public const string BackendId = "codex";
    public string Id => BackendId;
    public string DisplayName => "Codex";

    public async Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ResolvedExecutablePath))
            throw new InvalidOperationException("Codex executable path was not resolved.");
        if (!Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException($"Working directory not found: {options.WorkingDirectory}");

        var process = CreateProcess(options);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the Codex app-server process.");
        }

        var session = new CodexSession(process, options);
        await session.HandshakeAsync(ct).ConfigureAwait(false);
        return session;
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
                // BOM-less UTF-8 for stdin; a BOM would corrupt the first JSON-RPC line.
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add("app-server");
        // stdio transport is the default; inherit environment for auth + PATH.
        return process;
    }

    /// <summary>Map our mode onto a Codex sandbox preset used in turn/start params.</summary>
    internal static string MapSandboxPolicy(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Plan => "read-only",
        AgentPermissionMode.AcceptEdits => "workspace-write",
        AgentPermissionMode.BypassPermissions => "danger-full-access",
        _ => "workspace-write"
    };

    /// <summary>Map the Codex approval axis onto the app-server <c>approvalPolicy</c>
    /// wire value (the JSON-RPC equivalent of <c>--ask-for-approval</c>).</summary>
    internal static string MapApprovalPolicy(CodexApprovalPolicy policy) => policy switch
    {
        CodexApprovalPolicy.Untrusted => "untrusted",
        CodexApprovalPolicy.Never => "never",
        _ => "on-request"
    };
}
