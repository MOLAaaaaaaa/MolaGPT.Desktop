using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MolaGPT.Core.Models;

/// <summary>
/// What one block of a prompt template draws from. Every kind except
/// <see cref="Text"/> is a slot the role's own data fills in; the template only
/// decides where it goes, which role sends it and how it is wrapped.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptBlockKind>))]
public enum PromptBlockKind
{
    Text, Main, CharacterName, Description, Personality, Scenario, UserPersona,
    LoreBefore, LoreAfter, Summary, Events, Examples, History, PostHistory
}

public sealed class PromptBlock : INotifyPropertyChanged
{
    public const string Slot = "{{slot}}";

    private string _name = "";
    private bool _enabled = true;
    private string _role = "system";
    private int? _depth;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public PromptBlockKind Kind { get; set; }

    /// <summary>Only <see cref="PromptBlockKind.Text"/> blocks are named by the user.</summary>
    public string Name
    {
        get => _name;
        set { _name = value; Changed(nameof(Name)); Changed(nameof(DisplayName)); }
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Changed(nameof(Enabled)); }
    }

    public string Role
    {
        get => _role;
        set { _role = value; Changed(nameof(Role)); Changed(nameof(Detail)); }
    }

    /// <summary>
    /// <see cref="PromptBlockKind.Text"/>: the text itself.
    /// <see cref="PromptBlockKind.Main"/> / <see cref="PromptBlockKind.PostHistory"/>:
    /// what is sent when the role leaves its own prompt empty; the role's prompt
    /// reaches it through <c>{{original}}</c>.
    /// Everything else: a wrapper around the role's content, which lands at
    /// <c>{{slot}}</c>. Empty sends the content as it is.
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>Null keeps the block in template order; a number inserts it that
    /// many messages from the end of the conversation instead. Blocks without a
    /// role (the history and the examples) are never inserted.</summary>
    public int? Depth
    {
        get => _depth;
        set { _depth = value; Changed(nameof(Depth)); Changed(nameof(Detail)); }
    }

    [JsonIgnore] public string DisplayName => Kind == PromptBlockKind.Text
        ? string.IsNullOrWhiteSpace(Name) ? "自定义文本" : Name : Label(Kind);

    [JsonIgnore] public string Detail => HasRole
        ? RoleLabel(Role) + (Depth switch { null => "", 0 => " · 对话末尾", var depth => $" · 距末尾 {depth} 条" })
        : "";

    [JsonIgnore] public bool HasRole => Kind is not (PromptBlockKind.History or PromptBlockKind.Examples);
    [JsonIgnore] public bool UsesFormat => Kind is not (PromptBlockKind.Text or PromptBlockKind.Main
        or PromptBlockKind.PostHistory or PromptBlockKind.History or PromptBlockKind.Examples);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));

    public static string Label(PromptBlockKind kind) => kind switch
    {
        PromptBlockKind.Main => "主提示词",
        PromptBlockKind.CharacterName => "角色名",
        PromptBlockKind.Description => "角色资料",
        PromptBlockKind.Personality => "性格与表达",
        PromptBlockKind.Scenario => "场景",
        PromptBlockKind.UserPersona => "用户身份",
        PromptBlockKind.LoreBefore => "世界书 · 角色资料之前",
        PromptBlockKind.LoreAfter => "世界书 · 角色资料之后",
        PromptBlockKind.Summary => "剧情摘要",
        PromptBlockKind.Events => "剧情事件",
        PromptBlockKind.Examples => "对话示例",
        PromptBlockKind.History => "对话历史",
        PromptBlockKind.PostHistory => "补充指令",
        _ => "自定义文本"
    };

    public static string RoleLabel(string role) => role switch { "user" => "用户", "assistant" => "角色", _ => "系统" };
}

public sealed class PromptTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<PromptBlock> Blocks { get; set; } = [];

    /// <summary>
    /// The order roles were assembled in before templates existed, block for block.
    /// A role that has never been given a template keeps sending exactly this.
    /// </summary>
    public static PromptTemplate CreateDefault(string name = "默认编排") => new()
    {
        Name = name,
        Blocks =
        [
            new() { Kind = PromptBlockKind.Main },
            new() { Kind = PromptBlockKind.LoreBefore },
            new() { Kind = PromptBlockKind.CharacterName, Text = "角色：\n" + PromptBlock.Slot },
            new() { Kind = PromptBlockKind.Description, Text = "角色资料：\n" + PromptBlock.Slot },
            new() { Kind = PromptBlockKind.Personality, Text = "性格与表达：\n" + PromptBlock.Slot },
            new() { Kind = PromptBlockKind.Scenario, Text = "场景：\n" + PromptBlock.Slot },
            new() { Kind = PromptBlockKind.UserPersona, Text = "用户身份：\n" + PromptBlock.Slot },
            new() { Kind = PromptBlockKind.LoreAfter },
            new() { Kind = PromptBlockKind.Summary, Text = "剧情摘要：\n" + PromptBlock.Slot },
            new() { Kind = PromptBlockKind.Events, Text = "已经发生的事件：\n" + PromptBlock.Slot },
            new() { Kind = PromptBlockKind.Examples },
            new() { Kind = PromptBlockKind.History },
            new() { Kind = PromptBlockKind.PostHistory }
        ]
    };

    /// <summary>The wrapper a newly added block starts with.</summary>
    public static string DefaultFormat(PromptBlockKind kind) =>
        CreateDefault().Blocks.FirstOrDefault(block => block.Kind == kind)?.Text ?? "";

    /// <summary>Null when the template can be sent as it stands.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "请填写编排名称。";
        if (Blocks.Count(block => block.Kind == PromptBlockKind.History) != 1)
            return "「" + Name + "」：对话历史须有且仅有一块。";
        foreach (var group in Blocks.Where(block => block.Kind != PromptBlockKind.Text).GroupBy(block => block.Kind))
            if (group.Count() > 1) return "「" + Name + "」：「" + PromptBlock.Label(group.Key) + "」重复。";
        var history = Blocks.FindIndex(block => block.Kind == PromptBlockKind.History);
        if (Blocks.FindIndex(block => block.Kind == PromptBlockKind.Examples) > history)
            return "「" + Name + "」：对话示例须位于对话历史之前。";
        foreach (var block in Blocks)
        {
            if (block.Role is not ("system" or "user" or "assistant"))
                return "「" + block.DisplayName + "」：消息身份无效。";
            if (block.UsesFormat && block.Text.Length > 0 && !block.Text.Contains(PromptBlock.Slot, StringComparison.Ordinal))
                return "「" + block.DisplayName + "」：格式须包含 " + PromptBlock.Slot + "。";
            if (block.Depth is < 0 || block.Depth is not null && !block.HasRole)
                return "「" + block.DisplayName + "」：插入位置无效。";
            if (block.Depth == 0 && block.Role == "assistant")
                return "「" + block.DisplayName + "」：对话末尾仅支持系统或用户身份。";
        }
        var last = Blocks.Skip(history + 1).LastOrDefault(block => block.Enabled && block.Depth is null);
        if (last?.Role == "assistant")
            return "「" + Name + "」：对话历史之后的最后一块不能为角色身份。";
        return null;
    }
}
