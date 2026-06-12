using System.Text.RegularExpressions;

namespace MolaGPT.ViewModels.Services;

/// <summary>
/// Resolves <c>{{var}}</c> placeholders in user-authored system prompts.
///
/// Supported variables (case-insensitive):
/// <list type="bullet">
///   <item><c>{{date}}</c> — local date <c>yyyy-MM-dd</c></item>
///   <item><c>{{time}}</c> — local time <c>HH:mm</c></item>
///   <item><c>{{datetime}}</c> — <c>yyyy-MM-dd HH:mm</c></item>
///   <item><c>{{model}}</c> — active model display name</item>
///   <item><c>{{model_id}}</c> — active model id</item>
///   <item><c>{{provider}}</c> — active provider display name</item>
///   <item><c>{{username}}</c> — MolaGPT account username (or "用户" when not signed in)</item>
/// </list>
///
/// Unknown placeholders are left as-is — users may write JSON-shaped text or
/// templated markers we shouldn't silently eat.
/// </summary>
public static partial class SystemPromptInterpolator
{
    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    public static string Interpolate(string? template, in PromptVariables vars)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        if (template.IndexOf("{{", StringComparison.Ordinal) < 0) return template;

        var promptVars = vars;
        return PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            return name switch
            {
                "date"      => promptVars.Now.ToString("yyyy-MM-dd"),
                "time"      => promptVars.Now.ToString("HH:mm"),
                "datetime"  => promptVars.Now.ToString("yyyy-MM-dd HH:mm"),
                "model"     => promptVars.ModelDisplayName ?? match.Value,
                "model_id"  => promptVars.ModelId ?? match.Value,
                "provider"  => promptVars.ProviderDisplayName ?? match.Value,
                "username"  => string.IsNullOrWhiteSpace(promptVars.Username) ? "用户" : promptVars.Username!,
                _ => match.Value
            };
        });
    }

    /// <summary>
    /// Combine a persona prompt and a conversation-level prompt using the
    /// configured mode. Returns null when both pieces are empty.
    /// </summary>
    /// <param name="personaPrompt">Persona's system prompt, or null.</param>
    /// <param name="conversationPrompt">Per-conversation override / append, or null.</param>
    /// <param name="mode">Either <c>"override"</c> (default) or <c>"append"</c>.</param>
    public static string? Combine(string? personaPrompt, string? conversationPrompt, string? mode)
    {
        var hasPersona = !string.IsNullOrWhiteSpace(personaPrompt);
        var hasConv = !string.IsNullOrWhiteSpace(conversationPrompt);

        if (!hasPersona && !hasConv) return null;
        if (!hasConv) return personaPrompt;
        if (!hasPersona) return conversationPrompt;

        return string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase)
            ? personaPrompt!.TrimEnd() + "\n\n" + conversationPrompt!.TrimStart()
            : conversationPrompt;
    }
}

/// <summary>
/// Context passed to <see cref="SystemPromptInterpolator.Interpolate"/>.
/// Kept as a struct so callers don't allocate per turn.
/// </summary>
public readonly struct PromptVariables
{
    public DateTimeOffset Now { get; init; }
    public string? ModelDisplayName { get; init; }
    public string? ModelId { get; init; }
    public string? ProviderDisplayName { get; init; }
    public string? Username { get; init; }
}
