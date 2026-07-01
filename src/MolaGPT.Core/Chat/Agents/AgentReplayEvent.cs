namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// A transcript-rebuilding event, as opposed to a raw CLI <see cref="AgentEvent"/>.
/// The bridge records one of these for every state change it drives into the
/// reducer — not just the CLI events it forwards — so the log can replay a
/// complete transcript (user prompt, pending placeholder, CLI deltas, turn
/// boundary) on a fresh client. This is what Phase 2's relay ships to the phone:
/// the phone replays <see cref="AgentReplayEntry"/>s through its own reducer and
/// arrives at the same transcript state, without needing the original CLI stream.
///
/// Session phase / attention are NOT replay events — they are point-in-time
/// session state, synchronized via the <see cref="AgentSessionStateDto"/> snapshot
/// alongside the event log. A replay rebuilds the transcript; the snapshot
/// reconciles lifecycle state.
/// </summary>
public abstract record AgentReplayEvent
{
    /// <summary>Replay this event into a reducer, reproducing its transcript effect.</summary>
    public abstract void ApplyTo(AgentTranscriptReducer reducer);
}

/// <summary>The user submitted a turn (optimistic local echo of the prompt).</summary>
public sealed record UserTurnSubmitted(string Text) : AgentReplayEvent
{
    public override void ApplyTo(AgentTranscriptReducer reducer) => reducer.AddUser(Text);
}

/// <summary>A pending placeholder was shown while waiting for the first real event.</summary>
public sealed record PendingShown(string Label) : AgentReplayEvent
{
    public override void ApplyTo(AgentTranscriptReducer reducer) => reducer.BeginPending(Label);
}

/// <summary>One normalized CLI event was folded into the transcript.</summary>
public sealed record CliEventAppended(AgentEvent Event) : AgentReplayEvent
{
    public override void ApplyTo(AgentTranscriptReducer reducer) => reducer.Apply(Event);
}

/// <summary>The current turn was finalized (flush + close streaming blocks). Covers
/// both normal completion and user interrupt — the transcript effect is the same.</summary>
public sealed record TurnFinalized : AgentReplayEvent
{
    public override void ApplyTo(AgentTranscriptReducer reducer) => reducer.EndTurn();
}