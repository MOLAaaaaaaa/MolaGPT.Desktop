namespace MolaGPT.Core.Chat.Tools.PythonExecution;

/// <summary>
/// Process-wide, in-memory allow list for the local Python tool. Rules added
/// here last for the lifetime of the running app ("this session") and are
/// intentionally NOT persisted — restarting the app clears them. Persistent
/// rules live in the user's settings instead.
/// </summary>
public interface IPythonSessionAllowList
{
    IReadOnlyCollection<string> Imports { get; }
    IReadOnlyCollection<string> PathPrefixes { get; }
    void AllowImport(string module);
    void AllowPathPrefix(string prefix);
}

public sealed class PythonSessionAllowList : IPythonSessionAllowList
{
    private readonly object _gate = new();
    private readonly HashSet<string> _imports = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pathPrefixes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Imports
    {
        get { lock (_gate) return _imports.ToArray(); }
    }

    public IReadOnlyCollection<string> PathPrefixes
    {
        get { lock (_gate) return _pathPrefixes.ToArray(); }
    }

    public void AllowImport(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return;
        lock (_gate) _imports.Add(module.Trim());
    }

    public void AllowPathPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return;
        lock (_gate) _pathPrefixes.Add(prefix.Trim());
    }
}
