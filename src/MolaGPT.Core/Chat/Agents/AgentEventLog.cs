namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Append-only, monotonic-seq log of <see cref="AgentReplayEvent"/>s for one
/// session — the relay's source of truth for transcript reconstruction. The
/// bridge appends one entry per state change it drives into the reducer (user
/// prompt, pending, each CLI event, turn boundary), so a client replaying from
/// <see cref="Since"/> rebuilds the full transcript without the original CLI
/// stream. The seq here is the authoritative, wire-facing sequence number that
/// <see cref="AgentSessionStateDto.Seq"/> mirrors live.
///
/// <b>Thread safety.</b> The turn thread appends while a timer/relay thread
/// snapshots <see cref="Entries"/>/<see cref="Since"/>, so all access is
/// serialized on <see cref="_lock"/>; readers get immutable copies.
/// </summary>
public sealed class AgentEventLog
{
    private readonly List<AgentReplayEntry> _entries = new();
    private readonly object _lock = new();

    /// <summary>Monotonic seq of the last appended event (0 when empty).</summary>
    public long Seq
    {
        get { lock (_lock) return _entries.Count > 0 ? _entries[^1].Seq : 0; }
    }

    /// <summary>Immutable snapshot of every entry. Safe to enumerate off the lock.</summary>
    public IReadOnlyList<AgentReplayEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>Append a replay event, assigning the next monotonic seq. Returns the entry.</summary>
    public AgentReplayEntry Append(AgentReplayEvent ev)
    {
        lock (_lock)
        {
            var entry = new AgentReplayEntry(_entries.Count + 1, ev);
            _entries.Add(entry);
            return entry;
        }
    }

    /// <summary>Entries with seq strictly greater than <paramref name="sinceSeq"/>
    /// (reconnect catch-up; pass 0 for a fresh client to replay everything).
    /// Returns an immutable copy.</summary>
    public IReadOnlyList<AgentReplayEntry> Since(long sinceSeq)
    {
        lock (_lock) return _entries.Where(e => e.Seq > sinceSeq).ToList();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}

/// <summary>One seq-stamped replay event in the log.</summary>
public sealed record AgentReplayEntry(long Seq, AgentReplayEvent Event);