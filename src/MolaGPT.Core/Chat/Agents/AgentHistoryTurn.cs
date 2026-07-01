using MolaGPT.Core.Chat.Agents.Relay;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// A turn reconstructed from an agent's on-disk history. Each event preserves the
/// original transcript order so phone replay does not move tool calls above/below
/// the assistant text that surrounded them.
/// </summary>
public sealed record AgentHistoryTurn(IReadOnlyList<RelayTranscriptEvent> Events);
