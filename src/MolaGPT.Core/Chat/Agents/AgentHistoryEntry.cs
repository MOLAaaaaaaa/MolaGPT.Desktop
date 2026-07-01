namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// A past agent session discovered on disk (Claude Code or Codex local history).
/// Used to populate the console sidebar and to resume the real CLI conversation.
/// </summary>
public sealed record AgentHistoryEntry(
    string BackendId,
    string SessionId,
    string WorkingDirectory,
    string Title,
    DateTimeOffset LastModified,
    string FilePath);
