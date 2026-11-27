namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Deletes Pi session files whose conversation no longer exists.
///
/// Pi keeps its own transcript per conversation under <c>pi-sessions/</c> (that is
/// what lets a respawned sidecar resume instead of forgetting everything). Those
/// files must not outlive the conversation — both to bound disk use and because a
/// deleted conversation's content should not linger outside MolaGPT's database.
///
/// This is a sweep rather than a delete hook, deliberately:
/// <list type="bullet">
///   <item>Conversation deletion is a <em>soft</em> delete with an undo window.
///     Deleting the session file on the delete event would make an undone deletion
///     come back amnesiac.</item>
///   <item>Rows are hard-deleted only along the cloud-sync path, so there is no
///     single event that covers every provider.</item>
///   <item>A sweep also reclaims files orphaned by routes that notify nobody — a
///     crash mid-turn, a database reset, a conversation purge.</item>
/// </list>
/// Startup maintenance only considers files that predate the sweep's cutoff.
/// </summary>
public sealed class PiWorkSessionSweeper
{
    private readonly string _sessionRoot;

    public PiWorkSessionSweeper(string sessionRoot) => _sessionRoot = sessionRoot;

    /// <summary>
    /// Remove session files not belonging to any of <paramref name="liveConversationIds"/>.
    /// Returns the number of files deleted.
    /// </summary>
    /// <param name="liveSubagentKeys">Child sessions named in retained conversation history.</param>
    /// <param name="cutoff">Only files created and last written before this time are eligible.</param>
    /// <param name="ct">Stops a deferred sweep when the application shuts down.</param>
    /// <remarks>
    /// Matching is by <em>containment</em> of the sanitised conversation id in the file
    /// name, because Pi decorates the name it is given (<c>&lt;timestamp&gt;_&lt;id&gt;.jsonl</c>).
    /// Containment keeps this working if that decoration changes; the cost is that a
    /// conversation id which is a prefix of another would spare the other's file, which
    /// is the safe direction to err in.
    /// </remarks>
    public int Sweep(IReadOnlyCollection<string> liveConversationIds,
        IReadOnlyCollection<string> liveSubagentKeys, DateTime cutoff,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_sessionRoot) || File.GetAttributes(_sessionRoot).HasFlag(FileAttributes.ReparsePoint)) return 0;

        var live = liveConversationIds
            .Concat(liveSubagentKeys)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(PiWorkProvider.SanitizeSessionId)
            .ToArray();

        var removed = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(_sessionRoot, "*.jsonl", options))
        {
            if (ct.IsCancellationRequested) break;
            var name = Path.GetFileNameWithoutExtension(file);
            if (live.Any(id => name.Contains(id, StringComparison.Ordinal))) continue;

            try
            {
                if (File.GetCreationTimeUtc(file) >= cutoff || File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;
                File.Delete(file);
                removed++;
            }
            catch (IOException) { /* held open — next startup gets it */ }
            catch (UnauthorizedAccessException) { /* ditto */ }
        }
        return removed;
    }
}
