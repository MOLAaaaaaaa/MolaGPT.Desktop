using System.Collections.Concurrent;

namespace MolaGPT.Core.Chat.Tools;

/// <summary>
/// Default <see cref="IToolGrantStore"/>: session grants live in memory, persistent
/// grants are delegated to whatever the host uses for settings.
///
/// The split matters. A session grant is deliberately unable to outlive the process,
/// so "don't ask again" while doing one task cannot quietly become a standing
/// privilege the user has forgotten granting. Persisting is a separate, explicit
/// choice — and one that stays revocable, since it is a list of tool names rather
/// than a permission mode flipped open.
/// </summary>
public sealed class ToolGrantStore : IToolGrantStore
{
    /// <summary>
    /// Marks a persisted entry as a read-only path grant rather than a tool name.
    /// Path grants share the one list so they show up in — and can be revoked
    /// from — the same settings page as everything else; a standing permission
    /// the user cannot find is not meaningfully revocable.
    /// </summary>
    public const string PathGrantPrefix = "path:";

    /// <summary>
    /// A read-write path grant: the Python tool may work in this folder. Kept as
    /// a separate kind rather than a flag on the read grant so the settings page
    /// can name the difference, and so the two can never be confused by a
    /// prefix-match — "让它看看" and "让它改" are different answers to different
    /// questions.
    /// </summary>
    public const string WritablePathGrantPrefix = "pathrw:";

    private readonly ConcurrentDictionary<string, bool> _session = new(StringComparer.Ordinal);

    // Value is "may also write". Read-write entries satisfy read checks too.
    private readonly ConcurrentDictionary<string, bool> _sessionPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyCollection<string>> _loadPersisted;
    private readonly Action<IReadOnlyCollection<string>>? _savePersisted;

    /// <param name="loadPersisted">Reads the persisted allow-list. Called on each
    /// check so a revocation made in settings takes effect immediately.</param>
    /// <param name="savePersisted">Writes the persisted allow-list. Null makes
    /// <see cref="ToolGrantScope.Always"/> degrade to a session grant rather than
    /// silently doing nothing.</param>
    public ToolGrantStore(
        Func<IReadOnlyCollection<string>>? loadPersisted = null,
        Action<IReadOnlyCollection<string>>? savePersisted = null)
    {
        _loadPersisted = loadPersisted ?? (static () => Array.Empty<string>());
        _savePersisted = savePersisted;
    }

    public bool IsGranted(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return false;
        if (_session.ContainsKey(toolName)) return true;

        try
        {
            return _loadPersisted().Contains(toolName, StringComparer.Ordinal);
        }
        catch
        {
            // A broken settings read must never be mistaken for a grant.
            return false;
        }
    }

    public void Grant(string toolName, ToolGrantScope scope)
    {
        if (string.IsNullOrEmpty(toolName) || scope == ToolGrantScope.Once) return;

        if (scope == ToolGrantScope.Session || _savePersisted is null)
        {
            _session[toolName] = true;
            return;
        }

        var current = new List<string>();
        try { current.AddRange(_loadPersisted()); }
        catch { /* start from what we can see */ }

        if (!current.Contains(toolName, StringComparer.Ordinal))
        {
            current.Add(toolName);
            _savePersisted(current);
        }

        // Also grant for this session so the very next call is covered even if the
        // write did not land.
        _session[toolName] = true;
    }

    public bool IsPathGranted(string fullPath, bool forWriting = false)
    {
        var target = WorkspaceScope.Normalize(fullPath);
        if (target is null) return false;

        foreach (var (prefix, writable) in _sessionPaths)
        {
            if (forWriting && !writable) continue;
            if (WorkspaceScope.Covers(prefix, target)) return true;
        }

        try
        {
            foreach (var entry in _loadPersisted())
            {
                var (prefix, writable) = Decode(entry);
                if (prefix is null) continue;
                if (forWriting && !writable) continue;
                if (WorkspaceScope.Covers(prefix, target)) return true;
            }
        }
        catch
        {
            // A broken settings read must never be mistaken for a grant.
        }

        return false;
    }

    public IReadOnlyCollection<string> WritablePathPrefixes
    {
        get
        {
            var prefixes = new List<string>(
                _sessionPaths.Where(p => p.Value).Select(p => p.Key));

            try
            {
                foreach (var entry in _loadPersisted())
                    if (Decode(entry) is ({ } prefix, true))
                        prefixes.Add(prefix);
            }
            catch
            {
                // Same rule as everywhere else here: unreadable settings grant nothing.
            }

            return prefixes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void GrantPath(string pathPrefix, ToolGrantScope scope, bool allowWriting = false)
    {
        var normalized = WorkspaceScope.Normalize(pathPrefix);
        if (normalized is null || scope == ToolGrantScope.Once) return;

        if (scope == ToolGrantScope.Session || _savePersisted is null)
        {
            Remember(normalized, allowWriting);
            return;
        }

        var current = new List<string>();
        try { current.AddRange(_loadPersisted()); }
        catch { /* start from what we can see */ }

        var entry = (allowWriting ? WritablePathGrantPrefix : PathGrantPrefix) + normalized;
        if (!current.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            current.Add(entry);
            _savePersisted(current);
        }

        Remember(normalized, allowWriting);
    }

    /// <summary>Records a session grant without ever downgrading one already held:
    /// granting read to a folder that is already read-write must not take the
    /// write away.</summary>
    private void Remember(string normalized, bool allowWriting) =>
        _sessionPaths.AddOrUpdate(normalized, allowWriting, (_, existing) => existing || allowWriting);

    /// <summary>Splits a persisted entry into its prefix and whether it allows
    /// writing. Null prefix for entries that are tool names rather than paths.</summary>
    private static (string? Prefix, bool Writable) Decode(string entry) =>
        entry.StartsWith(WritablePathGrantPrefix, StringComparison.Ordinal)
            ? (entry[WritablePathGrantPrefix.Length..], true)
            : entry.StartsWith(PathGrantPrefix, StringComparison.Ordinal)
                ? (entry[PathGrantPrefix.Length..], false)
                : (null, false);

    /// <summary>Drop every session grant — used when the user revokes from settings,
    /// so revocation is not defeated by an in-memory copy.</summary>
    public void ClearSessionGrants()
    {
        _session.Clear();
        _sessionPaths.Clear();
    }
}
