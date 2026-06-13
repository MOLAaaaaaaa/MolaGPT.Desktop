using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

/// <summary>
/// Registry of Agent Skills (built-in + user-imported). Drives the settings
/// "技能" tab and supplies the tier-1 skill catalog injected into the system
/// prompt. Skills are executed via the local Python tool, so the catalog is
/// only meaningful for BYOK chats with that tool enabled.
///
/// Enabled state is persisted as a set of DISABLED skill names, so any newly
/// shipped built-in skill is enabled by default.
/// </summary>
public sealed partial class SkillsViewModel : ObservableObject
{
    private const string DisabledNamesKey = "skills_disabled";

    private readonly SkillManager _manager;
    private readonly SettingsRepository? _settingsRepo;
    private bool _loading;

    public ObservableCollection<SkillItemViewModel> Skills { get; } = new();

    /// <summary>Raised whenever the skill set or any enabled toggle changes, so
    /// the composer's injected catalog stays current.</summary>
    public event EventHandler? SkillsChanged;

    public SkillsViewModel() : this(new SkillManager(), null) { }

    public SkillsViewModel(SkillManager manager, SettingsRepository? settingsRepo)
    {
        _manager = manager;
        _settingsRepo = settingsRepo;
        Reload();
    }

    public string BuiltinSkillsDirectory => _manager.BuiltinSkillsDirectory;
    public string UserSkillsDirectory => _manager.UserSkillsDirectory;

    /// <summary>Make sure the user skills folder exists before opening/importing.</summary>
    public void EnsureUserDirectoryForImport() => _manager.EnsureUserDirectory();

    public bool HasEnabledSkills => Skills.Any(s => s.Enabled);

    public void Reload()
    {
        _loading = true;
        try
        {
            var disabled = LoadDisabledNames();
            Skills.Clear();
            foreach (var info in _manager.Discover())
            {
                var item = new SkillItemViewModel(info)
                {
                    Enabled = !disabled.Contains(info.Name)
                };
                item.PropertyChanged += OnItemPropertyChanged;
                Skills.Add(item);
            }
        }
        finally
        {
            _loading = false;
        }
        SkillsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(SkillItemViewModel.Enabled))
            return;
        PersistDisabledNames();
        SkillsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Tier-1 catalog: each enabled skill's name + description + SKILL.md path,
    /// plus instructions telling the model to read the file on demand and run it
    /// via the Python tool. Returns null when there is nothing to inject.
    /// </summary>
    public string? BuildCatalogForPrompt()
    {
        var enabled = Skills.Where(s => s.Enabled).ToArray();
        if (enabled.Length == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("## 可用技能（Skills）");
        sb.AppendLine(
            "下面是已启用的技能。当用户的任务匹配某个技能时，先用 execute_python_code 工具读取该技能的 SKILL.md "
            + "（用其绝对路径，如 open(path, encoding='utf-8').read() 并打印），按其中的完整步骤操作；"
            + "技能文件夹内可能还有 scripts/ 等资源，按 SKILL.md 指引按需读取。不要凭空臆造步骤。");
        foreach (var s in enabled)
        {
            var desc = string.IsNullOrWhiteSpace(s.Description) ? "(无描述)" : s.Description.Trim();
            sb.Append("- ").Append(s.Name).Append(" — ").Append(desc)
              .Append(" — SKILL.md: ").Append(s.SkillMdPath).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Directories the Python tool should be allowed to read without a
    /// path-approval prompt, so reading SKILL.md / bundled scripts is friction-free.</summary>
    public IReadOnlyList<string> AllowedReadRoots()
    {
        var roots = new List<string>();
        if (Directory.Exists(BuiltinSkillsDirectory)) roots.Add(BuiltinSkillsDirectory);
        if (Directory.Exists(UserSkillsDirectory)) roots.Add(UserSkillsDirectory);
        return roots;
    }

    /// <summary>Import a skill from a .zip (containing a SKILL.md, possibly one
    /// level deep) or a folder. Returns the imported skill's name on success.</summary>
    public string ImportFromPath(string sourcePath)
    {
        _manager.EnsureUserDirectory();

        if (Directory.Exists(sourcePath))
            return ImportFromFolder(sourcePath);

        if (File.Exists(sourcePath) &&
            string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
            return ImportFromZip(sourcePath);

        throw new InvalidOperationException("请选择一个包含 SKILL.md 的文件夹或 .zip 压缩包。");
    }

    private string ImportFromFolder(string folder)
    {
        var skillRoot = LocateSkillRoot(folder)
            ?? throw new InvalidOperationException("所选文件夹及其一级子目录中未找到 SKILL.md。");
        var name = Path.GetFileName(skillRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dest = ReserveDestination(name);
        CopyDirectory(skillRoot, dest);
        Reload();
        return name;
    }

    private string ImportFromZip(string zipPath)
    {
        var staging = Path.Combine(UserSkillsDirectory, $".import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ExtractZipSafely(zipPath, staging);
            var skillRoot = LocateSkillRoot(staging)
                ?? throw new InvalidOperationException("压缩包及其一级子目录中未找到 SKILL.md。");
            var name = Path.GetFileName(skillRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".import-", StringComparison.Ordinal))
                name = Path.GetFileNameWithoutExtension(zipPath);
            var dest = ReserveDestination(name);
            CopyDirectory(skillRoot, dest);
            Reload();
            return name;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    public void DeleteUserSkill(SkillItemViewModel item)
    {
        if (item.IsBuiltin) return; // built-ins are read-only
        // Safety: only delete inside the user skills directory.
        var full = Path.GetFullPath(item.DirectoryPath);
        var root = Path.GetFullPath(UserSkillsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝删除用户技能目录之外的内容。");
        Directory.Delete(full, recursive: true);
        Reload();
    }

    private static string? LocateSkillRoot(string root)
    {
        if (File.Exists(Path.Combine(root, SkillManager.SkillFileName)))
            return root;
        foreach (var sub in Directory.EnumerateDirectories(root))
        {
            if (File.Exists(Path.Combine(sub, SkillManager.SkillFileName)))
                return sub;
        }
        return null;
    }

    private string ReserveDestination(string name)
    {
        var safe = MakeSafeFolderName(name);
        var dest = Path.Combine(UserSkillsDirectory, safe);
        var i = 2;
        while (Directory.Exists(dest))
            dest = Path.Combine(UserSkillsDirectory, $"{safe}-{i++}");
        return dest;
    }

    private static string MakeSafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var safe = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "skill" : safe;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    private static void ExtractZipSafely(string zipPath, string destinationDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destinationDir);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("压缩包包含非法路径。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    private HashSet<string> LoadDisabledNames()
    {
        var raw = _settingsRepo?.Get(DisabledNamesKey);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var part in raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(part);
        }
        return set;
    }

    private void PersistDisabledNames()
    {
        if (_settingsRepo is null) return;
        var disabled = Skills.Where(s => !s.Enabled).Select(s => s.Name);
        var joined = string.Join(",", disabled);
        if (string.IsNullOrEmpty(joined))
            _settingsRepo.Remove(DisabledNamesKey);
        else
            _settingsRepo.Set(DisabledNamesKey, joined);
    }
}

public sealed partial class SkillItemViewModel : ObservableObject
{
    public SkillItemViewModel(SkillInfo info)
    {
        Name = info.Name;
        Description = info.Description;
        DirectoryPath = info.DirectoryPath;
        SkillMdPath = info.SkillMdPath;
        IsBuiltin = info.IsBuiltin;
        _enabled = info.Enabled;
    }

    public string Name { get; }
    public string Description { get; }
    public string DirectoryPath { get; }
    public string SkillMdPath { get; }
    public bool IsBuiltin { get; }

    [ObservableProperty] private bool _enabled;

    public string SourceLabel => IsBuiltin ? "内置" : "自定义";
}
