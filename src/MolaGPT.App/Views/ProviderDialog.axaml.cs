using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// Editor for one BYOK provider. Returns the saved <see cref="ProviderEntry"/>,
/// or null when cancelled.
///
/// The stored shapes are immutable records, so editing happens on the mutable
/// row types below and is folded back into records on save. That is what makes
/// "cancel" mean anything: nothing the user typed touches the registry until the
/// save button assembles a new entry.
/// </summary>
public partial class ProviderDialog : MolaContentWindow
{
    private static readonly string[] Types = ["openai-compat", "anthropic", "gemini"];

    private readonly string _id;
    private readonly int _sortOrder;
    private readonly ObservableCollection<ModelRow> _models = [];
    private readonly ObservableCollection<HeaderRow> _headers = [];

    public ProviderDialog() : this(null) { }

    public ProviderDialog(ProviderEntry? existing)
    {
        InitializeComponent();

        _id = existing?.Id ?? Guid.NewGuid().ToString("n");
        _sortOrder = existing?.SortOrder ?? 0;

        Title = existing is null ? "添加模型服务" : $"编辑「{existing.Name}」";

        PART_Type.SelectedIndex = Math.Max(0, Array.IndexOf(Types, existing?.Type ?? Types[0]));
        PART_Name.Text = existing?.Name ?? string.Empty;
        PART_BaseUrl.Text = existing?.BaseUrl ?? string.Empty;
        PART_ApiKey.Text = existing?.ApiKey ?? string.Empty;

        foreach (var model in existing?.Models ?? [])
        {
            _models.Add(new ModelRow
            {
                Id = model.Id,
                DisplayName = model.DisplayName,
                Vision = model.Vision,
                Thinking = model.Thinking,
                ReasoningEffort = model.ReasoningEffort,
                Tools = model.Tools,
                Source = model
            });
        }

        foreach (var header in existing?.CustomHeaders ?? [])
            _headers.Add(new HeaderRow { Name = header.Name, Value = header.Value });

        PART_Models.ItemsSource = _models;
        PART_Headers.ItemsSource = _headers;
        _models.CollectionChanged += (_, _) => RefreshEmptyStates();
        RefreshEmptyStates();

        PART_AddModel.Click += (_, _) => _models.Add(new ModelRow());
        PART_AddHeader.Click += (_, _) => _headers.Add(new HeaderRow());
        PART_RevealKey.Click += (_, _) =>
            PART_ApiKey.PasswordChar = PART_ApiKey.PasswordChar == '\0' ? '•' : '\0';

        PART_Cancel.Click += (_, _) => Close(null);
        PART_Save.Click += (_, _) => Save();
    }

    private void RefreshEmptyStates() => PART_NoModels.IsVisible = _models.Count == 0;

    private void OnRemoveModel(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ModelRow row }) _models.Remove(row);
    }

    private void OnRemoveHeader(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HeaderRow row }) _headers.Remove(row);
    }

    private void Save()
    {
        var name = PART_Name.Text?.Trim() ?? string.Empty;
        var baseUrl = PART_BaseUrl.Text?.Trim();

        if (name.Length == 0)
        {
            Fail("给这个服务起个名字。");
            return;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Fail("接口地址不能为空。");
            return;
        }

        var models = _models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => ToEntry(m))
            .ToList();

        if (models.Count == 0)
        {
            Fail("至少要有一个填了 ID 的模型。");
            return;
        }

        var headers = _headers
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .Select(h => new CustomHeaderEntry(h.Name.Trim(), h.Value ?? string.Empty))
            .ToList();

        Close(new ProviderEntry(
            Id: _id,
            Type: Types[Math.Max(0, PART_Type.SelectedIndex)],
            Name: name,
            BaseUrl: baseUrl!.TrimEnd('/'),
            ApiKey: PART_ApiKey.Text,
            Models: models,
            Enabled: true,
            SortOrder: _sortOrder,
            CustomHeaders: headers.Count > 0 ? headers : null));
    }

    /// <summary>
    /// Rebuilds the record from the edited row, carrying over every field the
    /// dialog does not expose. Constructing a fresh <see cref="ProviderModelEntry"/>
    /// from the four visible flags would silently wipe thinking budgets, effort
    /// levels, per-model system prompts and custom body overrides set elsewhere.
    /// </summary>
    private static ProviderModelEntry ToEntry(ModelRow row)
    {
        var id = row.Id.Trim();
        var display = string.IsNullOrWhiteSpace(row.DisplayName) ? id : row.DisplayName.Trim();

        return (row.Source ?? new ProviderModelEntry(id, display)) with
        {
            Id = id,
            DisplayName = display,
            Vision = row.Vision,
            Thinking = row.Thinking,
            ReasoningEffort = row.ReasoningEffort,
            Tools = row.Tools
        };
    }

    private void Fail(string message)
    {
        PART_Error.Text = message;
        PART_Error.IsVisible = true;
    }
}

/// <summary>Mutable edit buffer for one model row.</summary>
public sealed partial class ModelRow : ObservableObject
{
    private static readonly string[] ThinkingKinds =
    [
        "OpenAiReasoningEffort",
        "AnthropicAdaptive",
        "AnthropicBudget",
        "DeepSeekV4",
        "GeminiThinkingLevel",
        "GeminiBudget",
        "QwenThinkingBudget"
    ];

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private bool _vision;
    [ObservableProperty] private bool _thinking;
    [ObservableProperty] private bool _reasoningEffort;
    [ObservableProperty] private bool _tools;
    [ObservableProperty] private bool _supportsTemperature = true;
    [ObservableProperty] private bool _supportsTopP = true;
    [ObservableProperty] private string _contextWindowText = string.Empty;
    [ObservableProperty] private int _thinkingKindIndex;
    [ObservableProperty] private string _budgetMinText = string.Empty;
    [ObservableProperty] private string _budgetMaxText = string.Empty;
    [ObservableProperty] private string _budgetDefaultText = string.Empty;
    [ObservableProperty] private string _defaultEffort = string.Empty;
    [ObservableProperty] private string _effortLevelsText = string.Empty;
    [ObservableProperty] private bool _imageEdit;
    [ObservableProperty] private bool _isImageProvider;

    [ObservableProperty] private string _priceInputText = string.Empty;
    [ObservableProperty] private string _priceOutputText = string.Empty;
    [ObservableProperty] private string _priceCacheReadText = string.Empty;
    [ObservableProperty] private string _priceCacheWriteText = string.Empty;

    public ObservableCollection<BodyRow> CustomBodyRows { get; } = [];

    /// <summary>The record this row was loaded from, so fields the dialog does
    /// not show survive a round trip.</summary>
    public ProviderModelEntry? Source { get; init; }

    /// <summary>Where the loaded price came from, kept so a row that is merely
    /// reopened and saved does not get relabelled as hand-entered. Reassigned when
    /// a catalogue refresh writes new numbers into the boxes.</summary>
    public ModelPricing? LoadedPricing { get; set; }

    public string PriceSourceLabel => (LoadedPricing?.Source, Pricing()) switch
    {
        (_, null) => "未设置价格，本模型不统计费用",
        (ModelPricing.SourceEndpoint, _) => "价格来自接口返回",
        (ModelPricing.SourceModelsDev, _) => "价格来自 models.dev",
        _ => "价格为手动填写，不会被自动获取覆盖"
    };

    /// <summary>Reads the four boxes back into a price, or null when input and
    /// output are not both present — a price missing either half cannot bill.</summary>
    public ModelPricing? Pricing()
    {
        var input = ParsePrice(PriceInputText);
        var output = ParsePrice(PriceOutputText);
        if (input is null || output is null) return null;
        var cacheRead = ParsePrice(PriceCacheReadText);
        var cacheWrite = ParsePrice(PriceCacheWriteText);
        var loaded = LoadedPricing;
        var unchanged = loaded is not null
                        && loaded.Input == input && loaded.Output == output
                        && loaded.CacheRead == cacheRead && loaded.CacheWrite == cacheWrite;
        return new ModelPricing(input.Value, output.Value, cacheRead, cacheWrite,
            unchanged ? loaded!.Source : ModelPricing.SourceManual);
    }

    public void LoadPricing(ModelPricing? pricing)
    {
        PriceInputText = FormatPrice(pricing?.Input);
        PriceOutputText = FormatPrice(pricing?.Output);
        PriceCacheReadText = FormatPrice(pricing?.CacheRead);
        PriceCacheWriteText = FormatPrice(pricing?.CacheWrite);
        OnPropertyChanged(nameof(PriceSourceLabel));
    }

    private static double? ParsePrice(string? text) =>
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : null;

    private static string FormatPrice(double? value) =>
        value is { } price ? price.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

    partial void OnPriceInputTextChanged(string value) => OnPropertyChanged(nameof(PriceSourceLabel));
    partial void OnPriceOutputTextChanged(string value) => OnPropertyChanged(nameof(PriceSourceLabel));

    public static int ThinkingKindIndexFor(string? value)
    {
        var index = Array.FindIndex(ThinkingKinds, item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, index);
    }

    public static string ThinkingKindForIndex(int index) =>
        ThinkingKinds[Math.Clamp(index, 0, ThinkingKinds.Length - 1)];

    /// <summary>Budget kinds take a numeric token budget; every other kind
    /// takes a qualitative effort level. Mirrors the composer split
    /// (IsEffortComboVisible / IsBudgetSliderVisible) so the settings form
    /// never shows both sections at once.</summary>
    public static bool IsBudgetKindIndex(int index) =>
        ThinkingKindForIndex(index) is "AnthropicBudget" or "GeminiBudget" or "QwenThinkingBudget";

    public bool IsBudgetThinkingKind => IsBudgetKindIndex(ThinkingKindIndex);

    public bool IsEffortThinkingKind => !IsBudgetThinkingKind;

    /// <summary>强度档位区： effort 格式且勾选了推理强度才出现。</summary>
    public bool IsEffortOptionsVisible => IsEffortThinkingKind && ReasoningEffort;

    /// <summary>预算区： budget 格式且勾选了推理强度才出现。</summary>
    public bool IsBudgetOptionsVisible => IsBudgetThinkingKind && ReasoningEffort;

    partial void OnThinkingKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsBudgetThinkingKind));
        OnPropertyChanged(nameof(IsEffortThinkingKind));
        OnPropertyChanged(nameof(IsEffortOptionsVisible));
        OnPropertyChanged(nameof(IsBudgetOptionsVisible));
    }

    partial void OnReasoningEffortChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEffortOptionsVisible));
        OnPropertyChanged(nameof(IsBudgetOptionsVisible));
    }

    /// <summary>Known effort levels, ordered low → high. Union of the
    /// OpenAI / Anthropic-adaptive / Gemini-level vocabularies so presets
    /// work regardless of thinking kind; custom names append after these.</summary>
    public static readonly string[] KnownEffortLevels =
        ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    /// <summary>档位为空（新模型或老数据）时的默认选中：low、medium、high、xhigh。</summary>
    public static readonly string[] DefaultEffortLevels = ["low", "medium", "high", "xhigh"];

    public static readonly BudgetPreset[] BudgetPresets =
    [
        new("节省", 1024, 4096, 8192),
        new("均衡", 1024, 10000, 32000),
        new("深度", 4096, 20000, 32000),
    ];

    /// <summary>Capsule options for the档位 editor: every preset plus any
    /// custom names already stored. Rebuilt by <see cref="RefreshEffortLevelOptions"/>.</summary>
    public ObservableCollection<EffortLevelItem> EffortLevelOptions { get; } = [];

    /// <summary>Currently selected level names, in display order. Feeds the
    /// 默认强度 dropdown so the default is always one of the档位.</summary>
    public ObservableCollection<string> SelectedLevelNames { get; } = [];

    public IReadOnlyList<BudgetPreset> BudgetPresetOptions => BudgetPresets;

    [ObservableProperty] private string _customEffortInput = string.Empty;

    private bool _suspendEffortSync;

    /// <summary>Rebuilds capsule options from <see cref="EffortLevelsText"/>:
    /// presets in canonical low → high order, then stored custom names in
    /// entry order. Call after loading a row; edits afterwards go through
    /// the options and write back to <see cref="EffortLevelsText"/>.</summary>
    public void RefreshEffortLevelOptions()
    {
        _suspendEffortSync = true;
        try
        {
            var selected = ThinkingEffortLevels.Normalize(
                    EffortLevelsText.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0)
                selected.UnionWith(DefaultEffortLevels);
            // 默认强度 can only be picked from the选中档位, so an entry whose stored
            // default is not among them (hand-edited config, or levels trimmed in an
            // older build) selects that档位 rather than losing the default.
            if (!string.IsNullOrWhiteSpace(DefaultEffort))
                selected.Add(DefaultEffort.Trim().ToLowerInvariant());
            EffortLevelOptions.Clear();
            foreach (var preset in KnownEffortLevels)
                EffortLevelOptions.Add(new EffortLevelItem(this, preset, false,
                    selected.Contains(preset)));
            foreach (var custom in selected.Where(name =>
                         !KnownEffortLevels.Contains(name, StringComparer.OrdinalIgnoreCase)))
                EffortLevelOptions.Add(new EffortLevelItem(this, custom, true, true));
        }
        finally
        {
            _suspendEffortSync = false;
        }
        SyncEffortLevelsFromOptions();
    }

    internal void SyncEffortLevelsFromOptions()
    {
        if (_suspendEffortSync) return;
        var selected = EffortLevelOptions.Where(item => item.IsSelected).Select(item => item.Name).ToList();
        EffortLevelsText = string.Join(", ", selected);

        // 默认强度 is a ComboBox over SelectedLevelNames: clearing that list makes
        // it drop its selection and write null straight back into DefaultEffort,
        // so rebuilding the list wholesale wipes the default on every capsule
        // toggle. Patch the list in place, then re-pin the default.
        var previous = DefaultEffort;
        for (var i = SelectedLevelNames.Count - 1; i >= 0; i--)
            if (!selected.Contains(SelectedLevelNames[i], StringComparer.Ordinal))
                SelectedLevelNames.RemoveAt(i);
        for (var i = 0; i < selected.Count; i++)
        {
            var index = SelectedLevelNames.IndexOf(selected[i]);
            if (index < 0) SelectedLevelNames.Insert(i, selected[i]);
            else if (index != i) SelectedLevelNames.Move(index, i);
        }
        DefaultEffort = ResolveDefaultEffort(previous, selected);
    }

    /// <summary>Keeps the current default while it is still selected, otherwise
    /// falls back to high (the row default) and finally to whatever档位 is left.
    /// Always returns the name as stored in the list — SelectedItem matches by
    /// string equality, so a case difference would read as "no selection".</summary>
    private static string ResolveDefaultEffort(string? previous, List<string> selected) =>
        selected.FirstOrDefault(name => string.Equals(name, previous, StringComparison.OrdinalIgnoreCase))
        ?? selected.FirstOrDefault(name => string.Equals(name, "high", StringComparison.OrdinalIgnoreCase))
        ?? selected.FirstOrDefault()
        ?? string.Empty;

    /// <summary>Adds free-typed names as custom档位 (comma/space separated
    /// input may carry several). Names already present are selected instead.</summary>
    public void AddCustomEffortLevel()
    {
        var tokens = ThinkingEffortLevels.Normalize(
            CustomEffortInput.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries));
        if (tokens.Length == 0) return;
        foreach (var token in tokens)
        {
            var existing = EffortLevelOptions.FirstOrDefault(item =>
                string.Equals(item.Name, token, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.IsSelected = true;
                continue;
            }
            EffortLevelOptions.Add(new EffortLevelItem(this, token,
                !KnownEffortLevels.Contains(token, StringComparer.OrdinalIgnoreCase), true));
        }
        CustomEffortInput = string.Empty;
        SyncEffortLevelsFromOptions();
    }

    public void RemoveEffortLevel(EffortLevelItem item)
    {
        EffortLevelOptions.Remove(item);
        SyncEffortLevelsFromOptions();
    }

    public void ApplyBudgetPreset(BudgetPreset preset)
    {
        BudgetMinText = preset.Min.ToString();
        BudgetDefaultText = preset.Default.ToString();
        BudgetMaxText = preset.Max.ToString();
    }
}

public sealed partial class EffortLevelItem : ObservableObject
{
    private readonly ModelRow _owner;

    public EffortLevelItem(ModelRow owner, string name, bool isCustom, bool isSelected)
    {
        _owner = owner;
        Name = name;
        IsCustom = isCustom;
        _isSelected = isSelected;
    }

    public ModelRow Owner => _owner;
    public string Name { get; }
    public bool IsCustom { get; }
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.SyncEffortLevelsFromOptions();
}

public sealed record BudgetPreset(string Name, int Min, int Default, int Max)
{
    public string Description => $"最小 {Min} · 默认 {Default} · 最大 {Max}";
}

public sealed partial class HeaderRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
}

public sealed partial class BodyRow : ObservableObject
{
    private static readonly string[] Types = ["string", "number", "boolean", "json"];

    public BodyRow(ModelRow owner, string key = "", string type = "string", string value = "")
    {
        Owner = owner;
        _key = key;
        _typeIndex = Math.Max(0, Array.FindIndex(Types, item => string.Equals(item, type, StringComparison.OrdinalIgnoreCase)));
        _value = value;
    }

    public ModelRow Owner { get; }
    [ObservableProperty] private string _key;
    [ObservableProperty] private int _typeIndex;
    [ObservableProperty] private string _value;
    public string Type => Types[Math.Clamp(TypeIndex, 0, Types.Length - 1)];
}
