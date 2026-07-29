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
    private readonly ConcurrentDictionary<string, bool> _session = new(StringComparer.Ordinal);
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

    /// <summary>Drop every session grant — used when the user revokes from settings,
    /// so revocation is not defeated by an in-memory copy.</summary>
    public void ClearSessionGrants() => _session.Clear();
}
