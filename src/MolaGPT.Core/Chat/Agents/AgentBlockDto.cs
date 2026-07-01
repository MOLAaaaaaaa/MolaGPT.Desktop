namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// One renderable transcript block, as plain serializable data. This is the
/// canonical, WPF-free shape of an agent transcript entry — what the desktop
/// status surface, the wire relay, and (mirrored in Kotlin) the phone all render
/// from. It replaces the former WPF <c>AgentBlock</c> hierarchy with a flat
/// record discriminated by <see cref="Kind"/>, so it serializes trivially and has
/// no Dispatcher/INotifyPropertyChanged dependency.
/// </summary>
public enum AgentBlockKind { User, AssistantText, Thinking, Tool, Permission, Pending }

/// <summary>The user's resolution of an in-line permission prompt.</summary>
public enum AgentPermissionChoice { Once, Always, Deny }

/// <summary>
/// Flat transcript block. Only the fields relevant to <see cref="Kind"/> are
/// populated; the rest stay null. Helper factories keep call-sites honest.
/// </summary>
public sealed record AgentBlockDto(
    AgentBlockKind Kind,
    string? Text = null,
    bool IsStreaming = false,
    // Kind == Tool
    string? ToolId = null,
    string? ToolName = null,
    AgentToolStatus? ToolStatus = null,
    string? ToolArguments = null,
    string? ToolResultPreview = null,
    // Kind == Permission
    string? PermissionId = null,
    string? PermissionTitle = null,
    string? PermissionDetail = null,
    AgentPermissionChoice? PermissionChoice = null,
    // Kind == Pending
    string? PendingLabel = null)
{
    public static AgentBlockDto User(string text) => new(AgentBlockKind.User, Text: text);
    public static AgentBlockDto AssistantText(string text, bool streaming) =>
        new(AgentBlockKind.AssistantText, Text: text, IsStreaming: streaming);
    public static AgentBlockDto Thinking(string text, bool streaming) =>
        new(AgentBlockKind.Thinking, Text: text, IsStreaming: streaming);
    public static AgentBlockDto Tool(
        string toolId, string? name, AgentToolStatus status,
        string? arguments = null, string? resultPreview = null) =>
        new(AgentBlockKind.Tool, ToolId: toolId, ToolName: name, ToolStatus: status,
            ToolArguments: arguments, ToolResultPreview: resultPreview);
    public static AgentBlockDto Permission(
        string permissionId, string title, string? detail,
        AgentPermissionChoice? choice = null) =>
        new(AgentBlockKind.Permission, PermissionId: permissionId, PermissionTitle: title,
            PermissionDetail: detail, PermissionChoice: choice);
    public static AgentBlockDto Pending(string label) =>
        new(AgentBlockKind.Pending, PendingLabel: label);
}