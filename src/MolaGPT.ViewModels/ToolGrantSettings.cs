using System.Text.Json;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels;

/// <summary>
/// Persistence for the "始终允许" tool list. A plain list of tool names, so a
/// standing grant is inspectable and revocable one entry at a time — unlike the
/// permission-mode flip it replaces, which was all-or-nothing per tool family.
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

    /// <summary>Revoke one standing grant.</summary>
    public static void Revoke(SettingsRepository settings, string toolName) =>
        Write(settings, Read(settings).Where(n => !string.Equals(n, toolName, StringComparison.Ordinal)).ToArray());

    /// <summary>Revoke every standing grant.</summary>
    public static void RevokeAll(SettingsRepository settings) => settings.Remove(Key);
}
