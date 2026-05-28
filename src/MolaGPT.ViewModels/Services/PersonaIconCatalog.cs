namespace MolaGPT.ViewModels.Services;

/// <summary>
/// Avatar icon catalog for personas. Each entry pairs a stable string key
/// with a Segoe Fluent Icons / Segoe MDL2 Assets glyph (matches the project's
/// <c>Font.Icon</c> resource family).
///
/// The persona <see cref="MolaGPT.Storage.PersonaRow.Avatar"/> field stores
/// the glyph character directly (not the key), so legacy rows survive even
/// after we reshuffle this list — only the picker UI cares about the keys.
///
/// We intentionally avoid Unicode emoji here. Emoji require Segoe UI Emoji
/// which renders as colorful pictographs and clashes with the rest of the
/// monochrome Fluent icon set used across the desktop chrome.
/// </summary>
public static class PersonaIconCatalog
{
    public readonly record struct IconOption(string Key, string Glyph, string Label);

    /// <summary>Default glyph used when a persona's avatar is missing or
    /// unrecognized. Picked to look generic and approachable.</summary>
    public static readonly string DefaultGlyph = G(0xE99A); // Robot

    public static readonly IReadOnlyList<IconOption> All = new[]
    {
        new IconOption("robot",     G(0xE99A), "AI 机器人"),
        new IconOption("contact",   G(0xE77B), "用户"),
        new IconOption("people",    G(0xE716), "人物"),
        new IconOption("edit",      G(0xE70F), "编辑"),
        new IconOption("code",      G(0xE943), "代码"),
        new IconOption("globe",     G(0xE774), "地球"),
        new IconOption("book",      G(0xE82D), "书本"),
        new IconOption("lightbulb", G(0xE82F), "灵感"),
        new IconOption("chat",      G(0xE8BD), "对话"),
        new IconOption("brain",     G(0xF8B0), "推理"),
        new IconOption("flag",      G(0xE7C1), "标记"),
        new IconOption("star",      G(0xE734), "收藏"),
    };

    /// <summary>Map a stored avatar value to the glyph that should be
    /// rendered. Falls back to the default glyph for empty or null
    /// values. Any non-empty value is passed through verbatim so users who
    /// manually picked an icon still see it.</summary>
    public static string Resolve(string? avatar) =>
        string.IsNullOrWhiteSpace(avatar) ? DefaultGlyph : avatar!;

    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);
}
