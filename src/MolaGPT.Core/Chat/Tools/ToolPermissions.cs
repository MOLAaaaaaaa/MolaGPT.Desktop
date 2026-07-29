namespace MolaGPT.Core.Chat.Tools;

[Flags]
public enum ToolCapability
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    External = 1 << 2,
    Destructive = 1 << 3
}

public enum ToolPermissionMode
{
    Approval,
    FullAccess
}

public sealed record ToolApprovalRequest(
    string ToolName,
    string DisplayName,
    ToolCapability Capabilities,
    string ArgumentsJson,
    string? Description = null,
    bool AlwaysAsk = false);

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
