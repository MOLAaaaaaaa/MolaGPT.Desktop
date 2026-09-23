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
/// prompt. A skill is a file the model opens on demand, so the catalog is
/// meaningful to any BYOK chat with either the read-only file tools or the
/// Python tool; only skills that bundle scripts additionally need Python.
///
/// Enabled state is persisted as a set of DISABLED skill names, so any newly
/// shipped built-in skill is enabled by default.
/// </summary>
public sealed partial class SkillsViewModel : ObservableObject
{
    private const string DisabledNamesKey = "skills_disabled";

    /// <summary>
    /// 与「设置 → 浏览器使用 → 启用浏览器使用」共用一个状态的技能。
    ///
    /// 对用户来说这是一个功能：工具没有技能，模型是在凭空发明点击顺序；技能没有
    /// 工具，那是一份指向不存在的东西的说明书。所以两个开关联动，任一处改动另一
    /// 处跟随。
    /// </summary>
    public const string BrowserSkillName = "browser-use";

    private readonly SkillManager _manager;
    private readonly SettingsRepository? _settingsRepo;
    private readonly SettingsViewModel? _settings;
    private bool _loading;
    private bool _syncingBrowserLink;

    public ObservableCollection<SkillItemViewModel> Skills { get; } = new();

    /// <summary>Raised whenever the skill set or any enabled toggle changes, so
    /// the composer's injected catalog stays current.</summary>
    public event EventHandler? SkillsChanged;

    public SkillsViewModel() : this(new SkillManager(), null) { }

    public SkillsViewModel(SkillManager manager, SettingsRepository? settingsRepo, SettingsViewModel? settings = null)
    {
        _manager = manager;
        _settingsRepo = settingsRepo;
        _settings = settings;
        Reload();
        if (_settings is null) return;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.BrowserToolEnabled))
                PullBrowserSkillFromSettings();
        };
        PullBrowserSkillFromSettings();
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
        // 重新发现会重建整份列表，浏览器技能的勾选状态要重新对齐开关，
        // 否则刷新一次就能把两者拆开。
        PullBrowserSkillFromSettings();
        SkillsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(SkillItemViewModel.Enabled))
            return;
        if (sender is SkillItemViewModel { Name: BrowserSkillName } browser)
            PushBrowserSkillToSettings(browser.Enabled);
        PersistDisabledNames();
        SkillsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PullBrowserSkillFromSettings()
    {
        if (_settings is null || _syncingBrowserLink) return;
        if (Skills.FirstOrDefault(skill => skill.Name == BrowserSkillName) is not { } item) return;
        if (item.Enabled == _settings.BrowserToolEnabled) return;

        // 只挡住回写设置那一步：勾选变化仍然要走 OnItemPropertyChanged，
        // 才会落盘并通知 composer 刷新技能目录。
        _syncingBrowserLink = true;
        try { item.Enabled = _settings.BrowserToolEnabled; }
        finally { _syncingBrowserLink = false; }
    }

    private void PushBrowserSkillToSettings(bool enabled)
    {
        if (_settings is null || _syncingBrowserLink) return;
        if (_settings.BrowserToolEnabled == enabled) return;

        _syncingBrowserLink = true;
        try { _settings.BrowserToolEnabled = enabled; }
        finally { _syncingBrowserLink = false; }
    }

    /// <summary>
    /// Tier-1 catalog: each enabled skill's name + description + SKILL.md path,
    /// plus instructions for opening the file on demand. Returns null when there
    /// is nothing to inject.
    ///
    /// The instruction is worded for the tools this chat actually has. Three
    /// combinations exist and they are not interchangeable: naming
    /// <c>execute_python_code</c> to a chat without it sends the model after a
    /// tool that is not in its list, and promising it can run a skill's scripts
    /// when it cannot is how a "done" comes back for work that never happened.
    /// </summary>
    public string? BuildCatalogForPrompt(bool canUseReadTool = false, bool canRunPython = true)
    {
        var enabled = Skills.Where(s => s.Enabled).ToArray();
        if (enabled.Length == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("## 可用技能（Skills）");
        if (canUseReadTool && canRunPython)
        {
            sb.AppendLine(
                "下面是已启用的技能。当用户的任务匹配某个技能时，先用 read_file 工具读取该技能的 SKILL.md "
                + "（用其绝对路径），按其中的完整步骤操作；技能文件夹内可能还有 scripts/ 等资源，"
                + "用 read_file 查看、用 execute_python_code 运行。不要凭空臆造步骤。");
        }
        else if (canUseReadTool)
        {
            sb.AppendLine(
                "下面是已启用的技能。当用户的任务匹配某个技能时，先用 read_file 工具读取该技能的 SKILL.md "
                + "（用其绝对路径），按其中的完整步骤操作；技能文件夹内的 scripts/ 等资源同样用 read_file 查看。"
                + "本对话没有代码执行能力，技能里需要运行脚本的步骤做不了，如实说明，不要假装执行。"
                + "不要凭空臆造步骤。");
        }
        else
        {
            sb.AppendLine(
                "下面是已启用的技能。当用户的任务匹配某个技能时，先用 execute_python_code 工具读取该技能的 SKILL.md "
                + "（用其绝对路径，如 open(path, encoding='utf-8').read() 并打印），按其中的完整步骤操作；"
                + "技能文件夹内可能还有 scripts/ 等资源，按 SKILL.md 指引按需读取。不要凭空臆造步骤。");
        }
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
}
