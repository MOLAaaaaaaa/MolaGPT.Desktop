using System.Text.Json;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

/// <summary>
/// The locations one run may touch, enforced inside the interpreter by the audit
/// hook in <c>runner.py</c> rather than guessed from the source text.
///
/// Read and write are separate lists on purpose, and seeded very differently.
/// Writing is what cannot be taken back, so it stays narrow; reading is what
/// makes ordinary work possible, so it is broad. This is the same split Codex
/// draws with <c>workspace-write</c>, where reads are wide and only writes are
/// pinned to the workspace.
/// </summary>
/// <param name="Readable">Absolute roots readable by this run.</param>
/// <param name="Writable">Absolute roots writable by this run. Always a subset of
/// what is readable — a place you may write is a place you may read.</param>
/// <param name="Denied">Absolute paths refused under every circumstance, ranking
/// above both lists above.</param>
/// <param name="AllowNetwork">Whether outbound sockets are permitted.</param>
public sealed record PythonSandboxScope(
    IReadOnlyList<string> Readable,
    IReadOnlyList<string> Writable,
    IReadOnlyList<string> Denied,
    bool AllowNetwork)
{
    /// <summary>
    /// The scope a run starts with: the workspace to write in, a broad view of
    /// the machine to read from, and everything the interpreter needs in order to
    /// be an interpreter.
    ///
    /// Writing starts at the workspace and nowhere else — not the desktop, not
    /// Documents, none of the user's own folders. Seeding those would make the
    /// common case frictionless by permanently answering a question the user
    /// never got asked, and writing is the half that cannot be taken back. They
    /// are reachable the same way any other folder is: the model declares the
    /// folder and the user grants it once, or the first write fails with the
    /// path named and the user grants it then. Either way the grant persists, so
    /// the cost is one prompt per folder for the life of the install.
    /// </summary>
    public static PythonSandboxScope CreateDefault(
        string workspaceRoot,
        string? pythonExecutablePath,
        bool allowNetwork,
        IEnumerable<string>? grantedWritable = null,
        IEnumerable<string>? grantedReadable = null)
    {
        var writable = new List<string> { workspaceRoot };
        if (grantedWritable is not null)
            writable.AddRange(grantedWritable);

        // Everything writable, plus what the interpreter reads to function at
        // all. Omitting these does not produce a permission error the user can
        // act on — it produces "import pandas failed", which looks like a broken
        // product rather than a policy decision.
        var readable = new List<string>(writable);
        readable.AddRange(RuntimeReadRoots(workspaceRoot, pythonExecutablePath));
        if (grantedReadable is not null)
            readable.AddRange(grantedReadable);

        return new PythonSandboxScope(
            Normalize(readable),
            Normalize(writable),
            Normalize(ProtectedPaths.All),
            allowNetwork);
    }

    /// <summary>
    /// The scope for a run the user has put in full-access mode: no path limits,
    /// only the paths that are refused in every mode.
    ///
    /// A scope needs a way to be widened when it gets something wrong, and in
    /// full access that way — the approval dialog — is switched off. Enforcing
    /// one anyway would strand the task with an error nobody can grant past.
    /// </summary>
    public static PythonSandboxScope DenyOnly(bool allowNetwork) => new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Normalize(ProtectedPaths.All),
        allowNetwork);

    /// <summary>
    /// The writable roots a run starts with, without building a whole scope.
    /// Used to answer the only question a declared path raises: is this somewhere
    /// the run could already write, or does it need the user?
    /// </summary>
    public static IReadOnlyList<string> DefaultWritableRoots(
        string workspaceRoot,
        IEnumerable<string>? granted = null)
    {
        var writable = new List<string> { workspaceRoot };
        if (granted is not null) writable.AddRange(granted);
        return Normalize(writable);
    }

    /// <summary>
    /// Broad on purpose. Reading is what a run has to do constantly and mostly
    /// harmlessly — the interpreter reads its own standard library, matplotlib
    /// enumerates every font on the machine before it can draw a single label —
    /// and each omission surfaces as a broken feature rather than a question the
    /// user can answer. Enumerating individual font directories was tried and is
    /// a losing game: they live under Windows, under the per-user profile, and
    /// under ProgramData for Office, and missing any one of them breaks plotting.
    ///
    /// What keeps this from being "read anything": the deny list outranks it, and
    /// writing stays pinned to <see cref="UserContentFolders"/> plus the
    /// workspace. This is the shape Codex's workspace-write uses.
    /// </summary>
    private static IEnumerable<string> RuntimeReadRoots(string workspaceRoot, string? pythonExecutablePath)
    {
        yield return Path.Combine(workspaceRoot, ".packages");

        if (!string.IsNullOrWhiteSpace(pythonExecutablePath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(pythonExecutablePath));
            if (!string.IsNullOrWhiteSpace(dir)) yield return dir!;
        }

        var roots = new[]
        {
            Environment.SpecialFolder.UserProfile,        // per-user fonts, and the user's own files
            Environment.SpecialFolder.Windows,            // system fonts, System32
            Environment.SpecialFolder.CommonApplicationData, // ProgramData: Office fonts, shared app data
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        };

        foreach (var root in roots)
        {
            var path = SafeFolder(root);
            if (path is not null) yield return path;
        }
    }

    private static string? SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> paths) =>
        paths
            .Select(WorkspaceScope.Normalize)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Wire form handed to <c>runner.py</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        readable = Readable,
        writable = Writable,
        denied = Denied,
        allow_network = AllowNetwork
    });
}
