namespace MolaGPT.Core.Chat.Tools.PythonExecution;

/// <summary>
/// Process-wide, in-memory allow list for the local Python tool. Rules added
/// here last for the lifetime of the running app ("this session") and are
/// intentionally NOT persisted — restarting the app clears them. Persistent
/// rules live in the user's settings instead.
/// </summary>
/// <remarks>
/// Imports only. This used to carry path prefixes as well, granted from the
/// approval dialog — but the Python tool is now confined to the conversation's
/// working directory, and that is not a boundary a dialog is allowed to move.
/// Widening it is a settings-page act (<c>AllowedPathPrefixes</c>), done
/// deliberately rather than mid-task.
/// </remarks>
public interface IPythonSessionAllowList
{
    IReadOnlyCollection<string> Imports { get; }
    void AllowImport(string module);
}

public sealed class PythonSessionAllowList : IPythonSessionAllowList
{
    private readonly object _gate = new();
    private readonly HashSet<string> _imports = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Imports
    {
        get { lock (_gate) return _imports.ToArray(); }
    }

    public void AllowImport(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return;
        lock (_gate) _imports.Add(module.Trim());
    }
}
