namespace MolaGPT.Core.Models;

/// <summary>
/// Single chat message shared by provider implementations.
/// </summary>
public sealed record ChatMessage(
    string Role,
    object Content,
    string? Name = null,
    IReadOnlyList<Attachment>? Attachments = null,
    string? ReasoningContent = null)
{
    public const string RoleSystem = "system";
    public const string RoleUser = "user";
    public const string RoleAssistant = "assistant";
    public const string RoleTool = "tool";

    public string AsText() => Content switch
    {
        string s => s,
        IEnumerable<object> parts => string.Join("\n", parts),
        _ => Content?.ToString() ?? string.Empty
    };
}
