namespace MolaGPT.Core.Chat.Tools;

[Flags]
public enum ToolCapability
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    External = 1 << 2,
    Destructive = 1 << 3,

    /// <summary>
    /// The call resolves to a path outside the conversation's working directory.
    /// Orthogonal to <see cref="Read"/>/<see cref="Write"/> on purpose: a read
    /// that leaves the workspace is still only a read, but it is the user's own
    /// disk rather than a sandbox we created, so it is theirs to allow.
    /// </summary>
    OutsideWorkspace = 1 << 4
}

public enum ToolPermissionMode
{
    Approval,
    FullAccess
}

/// <summary>
/// Paths the local tools refuse regardless of settings or permission mode.
///
/// Only the credential store: it holds every provider API key at once, and an
/// approval dialog is a poor defence for that one file — it names a path, the
/// user is mid-task, and a single careless click leaks all of them. Not a
/// default for the user-facing deny list, because a default can be cleared by
/// anyone editing that field for an unrelated reason.
///
/// Enforced where a resolved absolute path exists (the read-only file tools);
/// advisory where only source text does (the Python analyzer sees literal paths,
/// not ones assembled at runtime). It raises the floor; it is not a boundary.
/// </summary>
public static class ProtectedPaths
{
    public static IReadOnlyList<string> All { get; } = BuildAll();

    private static IReadOnlyList<string> BuildAll()
    {
        // Must match where AppServices creates the CredentialStore.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Array.Empty<string>()
            : new[] { Path.Combine(localAppData, "MolaGPT", "creds.json") };
    }

    /// <summary>The caller's deny list with the built-ins folded in, first.</summary>
    public static IReadOnlyList<string> Combine(IReadOnlyList<string>? configured) =>
        configured is null || configured.Count == 0 ? All : All.Concat(configured).ToArray();
}

/// <param name="ResolvedPath">The absolute path this call will actually touch,
/// resolved the same way the tool resolves it. Present whenever
/// <see cref="ToolCapability.OutsideWorkspace"/> is set, because that decision
/// cannot be reviewed from <paramref name="ArgumentsJson"/> alone.</param>
public sealed record ToolApprovalRequest(
    string ToolName,
    string DisplayName,
    ToolCapability Capabilities,
    string ArgumentsJson,
    string? Description = null,
    bool AlwaysAsk = false,
    string? ResolvedPath = null);

public enum ToolApprovalDecision
{
    Denied,
    Approved
}

/// <summary>
/// How long an approval lasts. Agent harnesses converge on this distinction
/// (OpenCode's once/always/reject, Claude Code's session vs settings scope) because
/// the two extremes are both wrong as a default: asking every single time trains
/// people to click through, and granting forever turns one hurried click into a
/// permanent, invisible privilege.
/// </summary>
public enum ToolGrantScope
{
    /// <summary>This call only. Always safe, always the default.</summary>
    Once,

    /// <summary>Until the app closes. Held in memory, never written to disk.</summary>
    Session,

    /// <summary>Persisted. Survives restarts until the user revokes it.</summary>
    Always
}

/// <summary>
/// Remembers "don't ask me again" answers, keyed by the <b>exact</b> tool name.
/// Per-tool on purpose: the MCP tools all share one permission mode, so a grant
/// expressed as a mode change would silently cover every tool on every configured
/// MCP server — far more than the one call the user was looking at.
/// </summary>
public interface IToolGrantStore
{
    bool IsGranted(string toolName);

    /// <summary>Record a grant. <see cref="ToolGrantScope.Once"/> stores nothing.</summary>
    void Grant(string toolName, ToolGrantScope scope);

    /// <summary>
    /// True when an earlier "记住" covers <paramref name="fullPath"/>.
    /// </summary>
    /// <param name="forWriting">Ask about a tool that can also modify or delete
    /// what it reaches — the Python tool. A read grant never satisfies this: the
    /// user answered "让它看看这个文件夹", and silently reading that as "让它改这个
    /// 文件夹" is the one upgrade a permission store must never perform.</param>
    bool IsPathGranted(string fullPath, bool forWriting = false);

    /// <summary>
    /// Remember access to everything under <paramref name="pathPrefix"/> — the
    /// folder (or, for read-only tools, the drive) the user picked in the dialog.
    ///
    /// Deliberately <b>not</b> keyed by tool name. The user is answering "may this
    /// app reach D:\论文", not "may read_file specifically reach it"; keying by tool
    /// would ask again for grep_files over the same folder, which teaches people
    /// to stop reading the dialog. It is also strictly narrower than the per-tool
    /// grant it sits beside: that one covers every path, this one covers one
    /// subtree.
    /// </summary>
    /// <param name="allowWriting">Record a read-write grant, which implies read.</param>
    void GrantPath(string pathPrefix, ToolGrantScope scope, bool allowWriting = false);

    /// <summary>
    /// Prefixes the user has granted read-write. Handed to the Python risk
    /// analyzer, which already knows how to treat an allowed prefix as
    /// unremarkable — so a remembered folder stops producing prompts without the
    /// analyzer needing to learn about grants at all.
    /// </summary>
    IReadOnlyCollection<string> WritablePathPrefixes { get; }
}

/// <summary>
/// One policy entry point for every local tool. Read-only calls may be approved
/// automatically by the implementation; write, destructive, and explicitly
/// sensitive calls can surface a shared approval dialog.
/// </summary>
public interface IToolApprovalService
{
    Task<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request,
        ToolPermissionMode mode,
        CancellationToken ct);
}

public static class ToolCapabilityCatalog
{
    public static ToolApprovalRequest ForBuiltIn(string toolName, string argumentsJson) => toolName switch
    {
        "search_web" => new(toolName, "联网搜索", ToolCapability.Read | ToolCapability.External, argumentsJson),
        "web_fetch" => new(toolName, "网页读取", ToolCapability.Read | ToolCapability.External, argumentsJson),
        "read_file" => new(toolName, "读取文件", ToolCapability.Read, argumentsJson),
        "glob_files" => new(toolName, "查找文件", ToolCapability.Read, argumentsJson),
        "grep_files" => new(toolName, "搜索文件内容", ToolCapability.Read, argumentsJson),
        _ => new(toolName, toolName, ToolCapability.Write, argumentsJson)
    };
}
