using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

/// <summary>
/// First-class persona / role registry. Each persona bundles a system prompt
/// + avatar emoji + optional default tool toggles. Backed by
/// <see cref="PersonaRepository"/>.
///
/// Lifecycle:
/// <list type="bullet">
///   <item>App startup constructs this singleton and calls <see cref="Reload"/>.</item>
///   <item>Built-in seeds are inserted by <see cref="EnsureBuiltinsSeeded"/> on first run.</item>
///   <item><see cref="ChatViewModel.ActivePersonaId"/> holds the selection per conversation.</item>
/// </list>
///
/// Built-in personas (<see cref="PersonaItemViewModel.IsBuiltin"/>) cannot be
/// deleted via the UI, but can be duplicated and the copy freely edited.
/// </summary>
public sealed partial class PersonaListViewModel : ObservableObject
{
    public const string BuiltinDefaultId = "builtin-default";

    private const string LegacyBuiltinDefaultPrompt =
        "你是 MolaGPT 的默认助手。请用简洁、准确、友好的中文回答用户。\n\n" +
        "如果问题信息不足，先提出必要的澄清问题；如果可以直接解决，就给出清晰可执行的答案。";

    private const string BuiltinDefaultPrompt =
        "你是 MolaGPT 的默认助手。请用简洁、准确、友好的中文回答用户。\n\n" +
        "当前背景：\n" +
        "- 日期：{{date}}\n" +
        "- 时间：{{time}}\n" +
        "- 用户：{{username}}\n" +
        "- 当前模型：{{model}}\n" +
        "- 服务商：{{provider}}\n\n" +
        "如果问题信息不足，先提出必要的澄清问题；如果可以直接解决，就给出清晰可执行的答案。";

    public ObservableCollection<PersonaItemViewModel> Personas { get; } = new();

    private readonly PersonaRepository? _repo;

    /// <summary>Raised after the personas collection or any individual persona changes,
    /// so dependent views (Composer picker, Welcome quick row, dialogs) can refresh.</summary>
    public event EventHandler? PersonasChanged;

    public PersonaListViewModel() { }

    public PersonaListViewModel(PersonaRepository? repo)
    {
        _repo = repo;
        Reload();
    }

    public void Reload()
    {
        Personas.Clear();
        if (_repo is null) return;
        foreach (var row in _repo.ListActive())
            Personas.Add(PersonaItemViewModel.From(row));
        PersonasChanged?.Invoke(this, EventArgs.Empty);
    }

    public PersonaItemViewModel? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : Personas.FirstOrDefault(p => p.Id == id);

    public PersonaItemViewModel CreateBlank()
    {
        var item = CreateBlankDraft();
        Save(item);
        return item;
    }

    public PersonaItemViewModel CreateBlankDraft()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Use the lightbulb glyph for fresh user-created personas so they
        // visually distinguish from the built-in "通用助手" (robot). User can
        // pick any other icon from the catalog in Settings.
        var defaultIcon = PersonaIconCatalog.All.FirstOrDefault(o => o.Key == "lightbulb").Glyph;
        if (string.IsNullOrEmpty(defaultIcon))
            defaultIcon = PersonaIconCatalog.DefaultGlyph;

        return new PersonaItemViewModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "新角色",
            Avatar = defaultIcon,
            SystemPrompt = "",
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = Personas.Count
        };
    }

    public PersonaItemViewModel Duplicate(PersonaItemViewModel source)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var copy = new PersonaItemViewModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = source.Name + " 副本",
            Avatar = source.Avatar,
            SystemPrompt = source.SystemPrompt,
            DefaultEnableNetwork = source.DefaultEnableNetwork,
            DefaultEnableWebFetch = source.DefaultEnableWebFetch,
            DefaultThinking = source.DefaultThinking,
            DefaultReasoningEffort = source.DefaultReasoningEffort,
            IsBuiltin = false,
            Pinned = false,
            SortOrder = Personas.Count,
            CreatedAt = now,
            UpdatedAt = now
        };
        Save(copy);
        return copy;
    }

    public void Save(PersonaItemViewModel item)
    {
        item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_repo is not null) _repo.Upsert(item.ToRow());

        var existing = Personas.FirstOrDefault(p => p.Id == item.Id);
        if (existing is null) Personas.Add(item);
        else if (!ReferenceEquals(existing, item))
        {
            var idx = Personas.IndexOf(existing);
            Personas[idx] = item;
        }
        PersonasChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(string id)
    {
        var target = Personas.FirstOrDefault(p => p.Id == id);
        if (target is null || target.IsBuiltin) return false;
        if (_repo is not null)
            _repo.SoftDelete(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Personas.Remove(target);
        PersonasChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Insert the canonical built-in personas iff the table is empty. Idempotent:
    /// re-running is a no-op once any persona exists.
    /// </summary>
    public void EnsureBuiltinsSeeded()
    {
        if (_repo is null) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Avatar glyphs come from PersonaIconCatalog so the catalog stays the
        // central catalog for what icons are supported. All glyphs
        // resolve to Segoe Fluent Icons code points so they render in the
        // same monochrome family as the rest of the app chrome (no emoji).
        var iconRobot     = PersonaIconCatalog.All.First(o => o.Key == "robot").Glyph;
        var iconEdit      = PersonaIconCatalog.All.First(o => o.Key == "edit").Glyph;
        var iconCode      = PersonaIconCatalog.All.First(o => o.Key == "code").Glyph;
        var iconGlobe     = PersonaIconCatalog.All.First(o => o.Key == "globe").Glyph;
        var iconBook      = PersonaIconCatalog.All.First(o => o.Key == "book").Glyph;
        var seeds = new[]
        {
            new PersonaRow
            {
                Id = BuiltinDefaultId,
                Name = "通用助手",
                Avatar = iconRobot,
                SystemPrompt = BuiltinDefaultPrompt,
                IsBuiltin = true,
                Pinned = true,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            },
            new PersonaRow
            {
                Id = "builtin-writer",
                Name = "写作助手",
                Avatar = iconEdit,
                SystemPrompt = "你是一位资深的中文写作助手，擅长结构化表达、精准用词与节奏控制。\n\n请遵循以下原则：\n- 主动询问写作目的、读者与篇幅约束；\n- 修改建议给出具体替换文本，不止于评论；\n- 避免空话和套话，倾向用具体例子说明观点；\n- 输出时使用 Markdown，长文加分级标题。",
                IsBuiltin = true,
                SortOrder = 1,
                CreatedAt = now,
                UpdatedAt = now
            },
            new PersonaRow
            {
                Id = "builtin-coder",
                Name = "代码助手",
                Avatar = iconCode,
                SystemPrompt = "你是一位经验丰富的软件工程师，回答需具备工程师的严谨与坦诚。\n\n请遵循：\n- 先理解需求与现状，再给方案；如信息不足，先反问；\n- 给代码必给可运行的最小示例，并标注语言；\n- 指出潜在边界条件、错误处理与性能注意点；\n- 拒绝臆造 API；不确定的部分明确标注；\n- 中文回答，但代码、命令、API 名保留英文原文。",
                IsBuiltin = true,
                SortOrder = 2,
                CreatedAt = now,
                UpdatedAt = now
            },
            new PersonaRow
            {
                Id = "builtin-translator",
                Name = "翻译助手",
                Avatar = iconGlobe,
                SystemPrompt = "你是一位专业译者。除非用户另行指定，默认按以下规则工作：\n- 中译英时追求自然流畅，避免直译腔；\n- 英译中时保留专业术语原文（首次出现给括注），整体行文符合中文表达习惯；\n- 保留原文格式：列表、代码块、链接、Markdown 结构原样保留；\n- 仅输出译文，不解释翻译过程；除非用户要求对比，否则不附原文。",
                IsBuiltin = true,
                SortOrder = 3,
                CreatedAt = now,
                UpdatedAt = now
            },
            new PersonaRow
            {
                Id = "builtin-scholar",
                Name = "严谨学者",
                Avatar = iconBook,
                SystemPrompt = "你是一位治学严谨的研究者，回答应当：\n- 区分事实、推断与个人观点，必要时标注置信度；\n- 对不确定的内容明确说\"我不确定\"，不要编造引用；\n- 引用数据/研究时尽量给出年份与作者，但若来源不可考则诚实声明；\n- 鼓励用户基于一手资料独立验证；\n- 回答结构清晰，复杂论点先给结论再展开。",
                IsBuiltin = true,
                SortOrder = 4,
                CreatedAt = now,
                UpdatedAt = now
            }
        };

        if (_repo.CountAll() > 0)
        {
            EnsureDefaultPersonaPrompt(seeds[0], now);
            Reload();
            return;
        }

        _repo.InsertManyIfEmpty(seeds);
        Reload();
    }

    private void EnsureDefaultPersonaPrompt(PersonaRow seed, long timestampMs)
    {
        if (_repo is null) return;

        var existing = _repo.Get(BuiltinDefaultId);
        if (existing is null)
        {
            _repo.Upsert(seed);
            return;
        }

        if (!ShouldUpdateDefaultPrompt(existing.SystemPrompt)) return;

        existing.SystemPrompt = BuiltinDefaultPrompt;
        existing.UpdatedAt = timestampMs;
        _repo.Upsert(existing);
    }

    private static bool ShouldUpdateDefaultPrompt(string? currentPrompt) =>
        string.IsNullOrWhiteSpace(currentPrompt) ||
        string.Equals(currentPrompt.Trim(), LegacyBuiltinDefaultPrompt, StringComparison.Ordinal);
}

/// <summary>
/// View-model wrapper around <see cref="PersonaRow"/>. Mutable for inline edit
/// in the Settings tab; <see cref="PersonaListViewModel.Save"/> persists.
/// </summary>
public sealed partial class PersonaItemViewModel : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _avatar;
    [ObservableProperty] private string _systemPrompt = string.Empty;
    [ObservableProperty] private bool? _defaultEnableNetwork;
    [ObservableProperty] private bool? _defaultEnableWebFetch;
    [ObservableProperty] private bool? _defaultThinking;
    [ObservableProperty] private string? _defaultReasoningEffort;
    [ObservableProperty] private int _sortOrder;
    [ObservableProperty] private bool _pinned;
    [ObservableProperty] private bool _isBuiltin;
    [ObservableProperty] private long _createdAt;
    [ObservableProperty] private long _updatedAt;

    public string DisplayAvatar => PersonaIconCatalog.Resolve(Avatar);

    public string Preview
    {
        get
        {
            var first = (SystemPrompt ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(first)) return "自定义角色，尚未设置提示词";
            return first.Length > 60 ? first[..60] + "…" : first;
        }
    }

    public PersonaRow ToRow() => new()
    {
        Id = Id,
        Name = Name,
        Avatar = Avatar,
        SystemPrompt = SystemPrompt ?? string.Empty,
        DefaultEnableNetwork = DefaultEnableNetwork,
        DefaultEnableWebFetch = DefaultEnableWebFetch,
        DefaultThinking = DefaultThinking,
        DefaultReasoningEffort = DefaultReasoningEffort,
        SortOrder = SortOrder,
        Pinned = Pinned,
        IsBuiltin = IsBuiltin,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };

    public static PersonaItemViewModel From(PersonaRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Avatar = row.Avatar,
        SystemPrompt = row.SystemPrompt,
        DefaultEnableNetwork = row.DefaultEnableNetwork,
        DefaultEnableWebFetch = row.DefaultEnableWebFetch,
        DefaultThinking = row.DefaultThinking,
        DefaultReasoningEffort = row.DefaultReasoningEffort,
        SortOrder = row.SortOrder,
        Pinned = row.Pinned,
        IsBuiltin = row.IsBuiltin,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    partial void OnSystemPromptChanged(string value) => OnPropertyChanged(nameof(Preview));
    partial void OnAvatarChanged(string? value) => OnPropertyChanged(nameof(DisplayAvatar));
}
