using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] private string _contextWindowText = string.Empty;
    [ObservableProperty] private int _thinkingKindIndex;
    [ObservableProperty] private string _budgetMinText = string.Empty;
    [ObservableProperty] private string _budgetMaxText = string.Empty;
    [ObservableProperty] private string _budgetDefaultText = string.Empty;
    [ObservableProperty] private string _defaultEffort = string.Empty;
    [ObservableProperty] private string _effortLevelsText = string.Empty;
    [ObservableProperty] private string _systemPrompt = string.Empty;
    [ObservableProperty] private bool _imageEdit;
    [ObservableProperty] private bool _isImageProvider;

    public ObservableCollection<BodyRow> CustomBodyRows { get; } = [];

    /// <summary>The record this row was loaded from, so fields the dialog does
    /// not show survive a round trip.</summary>
    public ProviderModelEntry? Source { get; init; }

    public static int ThinkingKindIndexFor(string? value)
    {
        var index = Array.FindIndex(ThinkingKinds, item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, index);
    }

    public static string ThinkingKindForIndex(int index) =>
        ThinkingKinds[Math.Clamp(index, 0, ThinkingKinds.Length - 1)];
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
