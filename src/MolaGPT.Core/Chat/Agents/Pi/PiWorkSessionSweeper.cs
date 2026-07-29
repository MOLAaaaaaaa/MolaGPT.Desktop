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
/// Runs at startup only, when no sidecar can hold a session file open.
/// </summary>
public sealed class PiWorkSessionSweeper
{
    private readonly string _sessionRoot;

    public PiWorkSessionSweeper(string sessionRoot) => _sessionRoot = sessionRoot;

    /// <summary>
    /// Remove session files not belonging to any of <paramref name="liveConversationIds"/>.
    /// Returns the number of files deleted.
    /// </summary>
    /// <remarks>
    /// Matching is by <em>containment</em> of the sanitised conversation id in the file
    /// name, because Pi decorates the name it is given (<c>&lt;timestamp&gt;_&lt;id&gt;.jsonl</c>).
    /// Containment keeps this working if that decoration changes; the cost is that a
    /// conversation id which is a prefix of another would spare the other's file, which
    /// is the safe direction to err in.
    /// </remarks>
    public int Sweep(IReadOnlyCollection<string> liveConversationIds)
    {
        // An empty set almost certainly means "could not read the database", not
        // "the user has no conversations". Deleting everything on that reading
        // would be unrecoverable, so do nothing instead.
        if (liveConversationIds.Count == 0) return 0;
        if (!Directory.Exists(_sessionRoot)) return 0;

        var live = liveConversationIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(PiWorkProvider.SanitizeSessionId)
            .ToArray();

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_sessionRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (live.Any(id => name.Contains(id, StringComparison.Ordinal))) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException) { /* held open — next startup gets it */ }
            catch (UnauthorizedAccessException) { /* ditto */ }
        }
        return removed;
    }
}
