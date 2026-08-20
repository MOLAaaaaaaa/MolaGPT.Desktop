using System.IO;

namespace MolaGPT.Core.Chat.Tools;

/// <summary>
/// Decides whether a path a tool is about to touch lies inside the conversation's
/// working directory, and — when it does not — produces the prefixes a user can
/// grant instead of being asked again.
///
/// Every check here runs on a <b>resolved absolute path</b>, never on the argument
/// the model wrote. <c>notes.txt</c>, <c>..\..\.ssh\id_rsa</c> and <c>C:\Windows</c>
/// are indistinguishable as strings in a tool call and land in very different
/// places; deciding on the raw value is how a sandbox gets walked out of, and it
/// is also how an approval dialog ends up showing the user something other than
/// what the tool will open.
/// </summary>
public static class WorkspaceScope
{
    /// <summary>
    /// Absolute, separator-normalised, no trailing separator. Returns null for
    /// anything that cannot be turned into a path at all — callers treat null as
    /// "outside", never as "inside".
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"', '\''));
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // A drive root trims down to "C:" — put the separator back so it
            // cannot prefix-match "C:\Users" against "C:\Users2".
            return trimmed.Length == 0 || trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="fullPath"/> is <paramref name="prefix"/>
    /// itself or sits underneath it.</summary>
    public static bool Covers(string? prefix, string? fullPath)
    {
        var root = Normalize(prefix);
        var target = Normalize(fullPath);
        if (root is null || target is null) return false;

        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase)) return true;

        // A drive prefix already ends in a separator; anything else needs one
        // added so "D:\report" does not swallow "D:\reports".
        var boundary = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only when there is a workspace and the target is in it. No workspace
    /// means nothing is inside — the safe direction, since the alternative would
    /// silently auto-approve the whole disk for a conversation that has not made
    /// its working directory yet.
    /// </summary>
    public static bool IsInside(string? workspaceRoot, string? fullPath) =>
        Covers(workspaceRoot, fullPath);

    /// <summary>
    /// The folder a "remember this folder" grant should cover: the target itself
    /// when it is a directory, otherwise the directory holding it. Falls back to
    /// the drive for a path with no parent.
    /// </summary>
    public static string? FolderPrefix(string? fullPath)
    {
        var target = Normalize(fullPath);
        if (target is null) return null;

        try
        {
            if (Directory.Exists(target)) return target;
            var parent = Path.GetDirectoryName(target);
            return Normalize(parent) ?? DrivePrefix(target);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The volume root, so "记住整个磁盘" has something to record. Null for
    /// a UNC path, which has no drive to grant — the folder is the widest offer.</summary>
    public static string? DrivePrefix(string? fullPath)
    {
        var target = Normalize(fullPath);
        if (target is null) return null;

        try
        {
            var root = Path.GetPathRoot(target);
            if (string.IsNullOrWhiteSpace(root)) return null;
            // \\server\share is a root but not a drive; granting it would be a
            // grant over a whole file server, which is not what "磁盘" offers.
            return root!.StartsWith(@"\\", StringComparison.Ordinal) ? null : Normalize(root);
        }
        catch
        {
            return null;
        }
    }
}
