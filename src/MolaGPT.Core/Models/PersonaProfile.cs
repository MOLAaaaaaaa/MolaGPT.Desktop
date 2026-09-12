using System.Text.Json;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace MolaGPT.Core.Models;

public enum ConversationMode { Chat, Atmosphere }
public enum RoleCompatibility { CharacterCardSpec, SillyTavern }
public enum LoreScanScope { Inherit, Recent, All, None }
public enum LoreSelectiveLogic { AndAny, NotAll, NotAny, AndAll }
public enum LorePosition { BeforeCharacter, AfterCharacter, BeforeNote, AfterNote, AtDepth, BeforeExamples, AfterExamples, Outlet }

public sealed class PersonaProfile
{
    public ConversationMode? Mode { get; set; }
    public RoleCompatibility Compatibility { get; set; }
    public string CardSpec { get; set; } = "chara_card_v3";
    public string? SourceCardFile { get; set; }
    public string? AvatarSourceFile { get; set; }
    public bool CardAvatarChanged { get; set; }
    public string Nickname { get; set; } = "";
    public string Creator { get; set; } = "";
    public string CreatorNotes { get; set; } = "";
    public string CharacterVersion { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string UserName { get; set; } = "";
    public string UserDescription { get; set; } = "";
    public string Greeting { get; set; } = "";
    public List<string> AlternateGreetings { get; set; } = [];
    public string ExampleDialogue { get; set; } = "";
    public string PostHistoryInstructions { get; set; } = "";
    public string CharacterNote { get; set; } = "";
    public int CharacterNoteDepth { get; set; } = 4;
    public string CharacterNoteRole { get; set; } = "system";
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    public bool? EnablePython { get; set; }
    public bool? EnableFileTools { get; set; }
    public bool? EnableImageGeneration { get; set; }
    public bool? EnableMcp { get; set; }
    public List<Lorebook> Lorebooks { get; set; } = [];
    public string? EmbeddedLorebookId { get; set; }
    public List<string> SharedLorebookIds { get; set; } = [];
    public int LoreBudget { get; set; } = 2048;
    public string? UserPersonaId { get; set; }
    public List<string> ImportNotes { get; set; } = [];

    [JsonIgnore]
    public ConversationMode DefaultMode => Mode ?? (
        !string.IsNullOrWhiteSpace(Description) || !string.IsNullOrWhiteSpace(Personality)
        || !string.IsNullOrWhiteSpace(Scenario) || !string.IsNullOrWhiteSpace(UserName)
        || !string.IsNullOrWhiteSpace(UserDescription) || !string.IsNullOrWhiteSpace(Greeting)
        || !string.IsNullOrWhiteSpace(ExampleDialogue) || !string.IsNullOrWhiteSpace(PostHistoryInstructions)
        || AlternateGreetings.Count > 0 || Lorebooks.Count > 0
            ? ConversationMode.Atmosphere : ConversationMode.Chat);
}

public sealed class Lorebook : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public JsonObject? CardData { get; set; }
    private string _name = "世界书";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool Enabled { get; set; } = true;
    public int ScanDepth { get; set; } = 4;
    public LoreScanScope? ScanScope { get; set; }
    public int TokenBudget { get; set; } = 2048;
    public bool RecursiveScanning { get; set; }
    public List<LoreEntry> Entries { get; set; } = [];
}

public sealed class LoreEntry : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public JsonObject? CardData { get; set; }
    private string _name = "";
    private string _content = "";
    private List<string> _keywords = [];
    private bool _enabled = true;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        }
    }
    public string Content
    {
        get => _content;
        set { if (_content == value) return; _content = value; PropertyChanged?.Invoke(this, new(nameof(Content))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public List<string> Keywords
    {
        get => _keywords;
        set
        {
            _keywords = value;
            PropertyChanged?.Invoke(this, new(nameof(Keywords)));
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        }
    }
    [JsonIgnore] public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name
        : Keywords.FirstOrDefault() ?? "未命名条目";
    public List<string> SecondaryKeywords { get; set; } = [];
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); }
    }
    public bool Constant { get; set; }
    public bool CaseSensitive { get; set; }
    public bool Selective { get; set; }
    public LoreSelectiveLogic SelectiveLogic { get; set; }
    public bool UseRegex { get; set; }
    public bool MatchWholeWords { get; set; }
    public int Priority { get; set; }
    public int? InsertionOrder { get; set; }
    public int? BudgetPriority { get; set; }
    public int? ScanDepth { get; set; }
    public LoreScanScope? ScanScope { get; set; }
    public bool BeforeCharacter { get; set; }
    public LorePosition? Position { get; set; }
    public int Depth { get; set; } = 4;
    public string Role { get; set; } = "system";
    public bool ExcludeRecursion { get; set; }
    public bool PreventRecursion { get; set; }
    public int DelayUntilRecursion { get; set; }
    public int Probability { get; set; } = 100;
    public bool UseProbability { get; set; } = true;
    public string Group { get; set; } = "";
    public string OutletName { get; set; } = "";
    public bool GroupOverride { get; set; }
    public int GroupWeight { get; set; } = 100;
    public int Sticky { get; set; }
    public int Cooldown { get; set; }
    public int Delay { get; set; }
    public bool IgnoreBudget { get; set; }

    [JsonIgnore] public int Order => InsertionOrder ?? -Priority;
    [JsonIgnore] public int SelectionPriority => BudgetPriority ?? (InsertionOrder.HasValue ? InsertionOrder.Value : Priority);
    [JsonIgnore] public LorePosition Placement => Position ?? (BeforeCharacter ? LorePosition.BeforeCharacter : LorePosition.AfterCharacter);
}

public sealed class ConversationRoleContext
{
    public string? UserName { get; set; }
    public string? UserDescription { get; set; }
    public string? Scenario { get; set; }
    public string AuthorNote { get; set; } = "";
    public int AuthorNoteDepth { get; set; } = 4;
    public string AuthorNoteRole { get; set; } = "system";
    public List<string>? SharedLorebookIds { get; set; }
    public string? UserPersonaId { get; set; }
    public int GreetingIndex { get; set; }
    public string Summary { get; set; } = "";
    public List<string> SummarySourceIds { get; set; } = [];
    public List<StoryMemory> Memories { get; set; } = [];
    public bool AutoSummarize { get; set; }
    public string? LastSummarizedMessageId { get; set; }
    public List<string> ExcludedSummaryMessageIds { get; set; } = [];
    public Dictionary<string, LoreActivationState> LoreStates { get; set; } = [];
    public string HistoryRevision { get; set; } = "";
    public bool NeedsHistorySync { get; set; }
    public bool? EnableNetwork { get; set; }
    public bool? EnableWebFetch { get; set; }
    public bool? EnableThinking { get; set; }
    public string? ReasoningEffort { get; set; }
}

public sealed record LoreActivationState(int ActivatedAt, string SourceMessageId);

public sealed class UserPersona
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Avatar { get; set; }
}

public sealed class StoryMemory : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public List<string> SourceMessageIds { get; set; } = [];
    public string SourceQuote { get; set; } = "";
    public bool Pinned { get; set; }
}

public static class RoleJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) where T : new() =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("角色数据不能为空。");
}
