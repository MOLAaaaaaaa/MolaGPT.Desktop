using System.Text.Json;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels;

/// <summary>One row in the standing-grants list: what to revoke, and what to show.</summary>
/// <param name="Key">The stored entry — a tool name, or a <c>path:</c> grant.</param>
public sealed record ToolGrantEntry(string Key, string Display);

/// <summary>
/// Persistence for the "始终允许" list. A plain list of entries, so a standing
/// grant is inspectable and revocable one at a time — unlike the permission-mode
/// flip it replaces, which was all-or-nothing per tool family.
///
/// Two kinds of entry share the list: a bare tool name, and a <c>path:</c>-prefixed
/// folder or drive the read-only tools may reach outside the working directory.
/// They live together so there is exactly one page to check when the question is
/// "what have I permanently allowed".
/// </summary>
public static class ToolGrantSettings
{
    public const string Key = "tools.alwaysAllow";

    public static IReadOnlyCollection<string> Read(SettingsRepository settings)
    {
        var raw = settings.Get(Key);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
        }
        catch
        {
            // Corrupt value must read as "nothing is granted", never as a grant.
            return Array.Empty<string>();
        }
    }

    public static void Write(SettingsRepository settings, IReadOnlyCollection<string> toolNames) =>
        settings.Set(Key, JsonSerializer.Serialize(toolNames));

    /// <summary>
    /// Human-readable form of a stored entry. The read/read-write distinction is
    /// spelled out rather than implied: the whole point of keeping them as
    /// separate kinds is that the user can tell, on this page, which folders
    /// something can only look at and which it can rewrite.
    /// </summary>
    public static string Describe(string entry) =>
        entry.StartsWith(ToolGrantStore.WritablePathGrantPrefix, StringComparison.Ordinal)
            ? "读写路径　" + entry[ToolGrantStore.WritablePathGrantPrefix.Length..]
            : entry.StartsWith(ToolGrantStore.PathGrantPrefix, StringComparison.Ordinal)
                ? "读取路径　" + entry[ToolGrantStore.PathGrantPrefix.Length..]
                : entry;

    /// <summary>Revoke one standing grant.</summary>
    public static void Revoke(SettingsRepository settings, string toolName) =>
        Write(settings, Read(settings).Where(n => !string.Equals(n, toolName, StringComparison.Ordinal)).ToArray());

    /// <summary>Revoke every standing grant.</summary>
    public static void RevokeAll(SettingsRepository settings) => settings.Remove(Key);
}
