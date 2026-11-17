using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat.Tools.PythonExecution;

/// <summary>One directory under the sessions root.</summary>
public sealed record PythonWorkspaceDirectory(string Name, string Path, DateTime LastWriteUtc);

/// <summary>
/// The on-disk layout of Python workspaces, for code that has to reason about
/// them without running anything: which directory belongs to which conversation,
/// and which directories are workspaces at all.
///
/// Two shapes exist. <c>conv-&lt;id&gt;</c> is a conversation's workspace.
/// <c>&lt;timestamp&gt;-&lt;hex&gt;</c> is a run that had no conversation id; its
/// output is still linked from the message that produced it. Anything else under
/// the root was not put there by the tool and is never a candidate for cleanup.
/// </summary>
public static partial class PythonWorkspaceStore
{
    public const string ConversationPrefix = "conv-";

    public static string SessionsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT", "python-tool", "sessions");

    /// <summary>The directory name a conversation's workspace has, whether or not
    /// it exists. Null for an empty id, which has no stable workspace.</summary>
    public static string? DirectoryNameFor(string? conversationId) =>
        PythonExecutionTool.SanitizeConversationId(conversationId) is { } slug
            ? ConversationPrefix + slug
            : null;

    public static bool IsWorkspaceName(string name) =>
        ConversationName().IsMatch(name) || ScratchName().IsMatch(name);

    public static bool IsScratchName(string name) => ScratchName().IsMatch(name);

    public static IReadOnlyList<PythonWorkspaceDirectory> Enumerate(string? sessionsRoot = null)
    {
        var root = new DirectoryInfo(sessionsRoot ?? SessionsRoot);
        if (!root.Exists) return [];
        try
        {
            return root.EnumerateDirectories()
                .Where(directory => IsWorkspaceName(directory.Name)
                                    && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(directory => new PythonWorkspaceDirectory(
                    directory.Name, directory.FullName, directory.LastWriteTimeUtc))
                .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Every workspace name mentioned in <paramref name="text"/>. Tool results
    /// store the working directory as a path, JSON-escaped once or twice depending
    /// on nesting, so the separator is matched as any run of slashes.
    /// </summary>
    public static void CollectReferencedNames(string? text, ISet<string> into)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match match in ReferencedName().Matches(text))
            into.Add(match.Groups[1].Value);
    }

    [GeneratedRegex(@"^\d{17}-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex ScratchName();

    [GeneratedRegex(@"^conv-[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ConversationName();

    [GeneratedRegex(@"sessions[\\/]+(conv-[A-Za-z0-9_-]+|\d{17}-[0-9a-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencedName();
}
