using System.IO;

namespace MolaGPT.ViewModels.Services;

/// <summary>
/// One discovered Agent Skill: a folder containing a <c>SKILL.md</c> whose YAML
/// frontmatter provides <c>name</c> and <c>description</c>. The model opens
/// <see cref="SkillMdPath"/> on demand (progressive disclosure tier 2/3) with
/// whichever read tool the chat has — <c>read_file</c> or the Python tool — and
/// follows the instructions; only bundled scripts need Python to run.
/// </summary>
public sealed record SkillInfo(
    string Name,
    string Description,
    string DirectoryPath,
    string SkillMdPath,
    bool IsBuiltin)
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Scans the built-in skills folder (shipped next to the app) and the user
/// skills folder (under LocalApplicationData) and parses each
/// <c>SKILL.md</c> frontmatter. Pure file IO — no WPF — so it can live in the
/// view-model layer and be consumed by both the settings page and the composer.
/// </summary>
public sealed class SkillManager
{
    public const string SkillFileName = "SKILL.md";
    private const string RuntimeDirectoryName = "skills";

    private readonly string? _userOverride;
    private readonly string? _builtinOverride;

    public SkillManager() { }

    /// <summary>Test/customization hook: override either skills directory.</summary>
    public SkillManager(string? userSkillsDirectoryOverride, string? builtinSkillsDirectoryOverride)
    {
        _userOverride = userSkillsDirectoryOverride;
        _builtinOverride = builtinSkillsDirectoryOverride;
    }

    /// <summary>Built-in skills ship in <c>&lt;AppDir&gt;/skills</c> (read-only).</summary>
    public string BuiltinSkillsDirectory =>
        _builtinOverride ?? Path.Combine(GetAppDirectory(), RuntimeDirectoryName);

    /// <summary>User-imported skills live under LocalApplicationData (writable).</summary>
    public string UserSkillsDirectory => _userOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT",
        RuntimeDirectoryName);

    /// <summary>
    /// Discover all skills. Built-in skills come first; a user skill whose name
    /// collides with a built-in one shadows it (so users can override).
    /// </summary>
    public IReadOnlyList<SkillInfo> Discover()
    {
        var byName = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in ScanDirectory(BuiltinSkillsDirectory, isBuiltin: true))
            byName[skill.Name] = skill;

        // User skills override built-ins of the same name.
        foreach (var skill in ScanDirectory(UserSkillsDirectory, isBuiltin: false))
            byName[skill.Name] = skill;

        return byName.Values
            .OrderByDescending(s => s.IsBuiltin)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 从一个路径反推它属于哪个技能，认不出来就返回 false。
    ///
    /// 只看路径形状，不碰磁盘：调用方是流式渲染中的工具卡片，每来一个 delta 就重算
    /// 一次，那里不能有 IO。两个信号各自独立成立——文件名是 <c>SKILL.md</c>（说明书
    /// 本身，不管它放在哪），或者路径里有一段叫 <c>skills</c>（两个技能根目录都以它
    /// 结尾，开发期的 <c>src/skills</c> 也是），此时紧随其后的那一段就是技能名。
    /// </summary>
    public static bool TryGetSkillFromPath(string? path, out string? skillName)
    {
        skillName = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var segments = path.Split(
            new[] { '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 2
            && segments[^1].Equals(SkillFileName, StringComparison.OrdinalIgnoreCase))
        {
            skillName = segments[^2];
            return true;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            if (!segments[i].Equals(RuntimeDirectoryName, StringComparison.OrdinalIgnoreCase))
                continue;
            skillName = i + 1 < segments.Length ? segments[i + 1] : null;
            return true;
        }

        return false;
    }

    public void EnsureUserDirectory()
    {
        try { Directory.CreateDirectory(UserSkillsDirectory); }
        catch { /* best effort; surfaced later if import fails */ }
    }

    private static IEnumerable<SkillInfo> ScanDirectory(string root, bool isBuiltin)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var skillMd = Path.Combine(dir, SkillFileName);
            if (!File.Exists(skillMd))
                continue;

            SkillInfo? info = null;
            try
            {
                var (name, description) = ParseFrontmatter(File.ReadAllText(skillMd));
                var resolvedName = string.IsNullOrWhiteSpace(name)
                    ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : name!.Trim();
                if (!string.IsNullOrWhiteSpace(resolvedName))
                {
                    info = new SkillInfo(
                        resolvedName,
                        (description ?? string.Empty).Trim(),
                        dir,
                        skillMd,
                        isBuiltin);
                }
            }
            catch
            {
                info = null; // a malformed skill is skipped, not fatal
            }

            if (info is not null)
                yield return info;
        }
    }

    /// <summary>
    /// Minimal YAML frontmatter reader: takes the block between the first pair of
    /// <c>---</c> lines and extracts <c>name</c> / <c>description</c> as
    /// <c>key: value</c>. Strips surrounding quotes. Avoids a YAML dependency
    /// since we only need two scalar fields.
    /// </summary>
    public static (string? Name, string? Description) ParseFrontmatter(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, null);

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (lines[i].Trim() == "---") { start = i; }
            break;
        }
        if (start < 0)
            return (null, null);

        var end = -1;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { end = i; break; }
        }
        if (end < 0)
            return (null, null);

        string? name = null, description = null;
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = Unquote(line[(colon + 1)..].Trim());
            if (key == "name") name = value;
            else if (key == "description") description = value;
        }

        return (name, description);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static string GetAppDirectory()
    {
        var processPath = Environment.ProcessPath;
        var dir = string.IsNullOrWhiteSpace(processPath)
            ? null
            : Path.GetDirectoryName(processPath);
        return string.IsNullOrWhiteSpace(dir)
            ? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : dir!;
    }
}
