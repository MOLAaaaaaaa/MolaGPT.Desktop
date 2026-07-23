using MolaGPT.Core.Chat.Agents.Relay;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// A turn reconstructed from an agent's on-disk history. Each event preserves the
/// original transcript order so phone replay does not move tool calls above/below
/// the assistant text that surrounded them.
/// <para><see cref="IsOpen"/> marks a tail turn whose transcript ended without a
/// terminal marker (Claude <c>result</c> / Codex <c>task_complete</c>): the CLI is
/// still writing it — or died mid-flight — so no <see cref="TurnDoneEvent"/> was
/// synthesized for it.</para>
/// </summary>
public sealed record AgentHistoryTurn(IReadOnlyList<RelayTranscriptEvent> Events, bool IsOpen = false);
