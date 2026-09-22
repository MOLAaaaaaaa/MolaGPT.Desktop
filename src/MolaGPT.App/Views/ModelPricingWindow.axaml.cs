using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Models;

namespace MolaGPT.App.Views;

/// <summary>One quoted price, as it reads in the preset dropdown.</summary>
public sealed record PricingCandidate(string ProviderKey, string ProviderName, ModelPricing Pricing)
{
    /// <summary>Source and numbers together: the entries only reached this list by
    /// disagreeing about the numbers, so the source name alone would not let anyone
    /// choose between them.</summary>
    public override string ToString() =>
        $"{ProviderName}　↑{Money(Pricing.Input)} ↓{Money(Pricing.Output)}";

    private static string Money(double perMillion) =>
        "$" + perMillion.ToString(perMillion >= 1 ? "0.##" : "0.####", CultureInfo.InvariantCulture);
}

/// <summary>
/// One disputed model: a preset picker over what the catalogue quotes, and the four
/// rates as editable text. The presets are a shortcut, not the only answer — an
/// endpoint can charge something nobody published, and then typing it is the only
/// way to get the bill right.
/// </summary>
public sealed partial class PricingConflictRow : ObservableObject
{
    private bool _filling;

    public PricingConflictRow(string providerId, string providerLabel, string modelId,
        IReadOnlyList<PricingCandidate> candidates, PricingCandidate? preferred)
    {
        ProviderId = providerId;
        ProviderLabel = providerLabel;
        ModelId = modelId;
        Candidates = candidates;
        Selected = preferred ?? candidates.FirstOrDefault();
    }

    /// <summary>The BYOK provider this row belongs to, so the pick is written back to
    /// the right one when the same model id is served by several.</summary>
    public string ProviderId { get; }
    public string ProviderLabel { get; }
    public string ModelId { get; }
    public IReadOnlyList<PricingCandidate> Candidates { get; }

    [ObservableProperty] private PricingCandidate? _selected;
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _outputText = string.Empty;
    [ObservableProperty] private string _cacheReadText = string.Empty;
    [ObservableProperty] private string _cacheWriteText = string.Empty;

    partial void OnSelectedChanged(PricingCandidate? value)
    {
        if (value is null) return;
        _filling = true;
        try
        {
            InputText = Format(value.Pricing.Input);
            OutputText = Format(value.Pricing.Output);
            CacheReadText = Format(value.Pricing.CacheRead);
            CacheWriteText = Format(value.Pricing.CacheWrite);
        }
        finally { _filling = false; }
    }

    /// <summary>Typing over a prefilled number drops the preset — the row no longer
    /// claims to be that source's price, and the dropdown falls back to 「自定义」.</summary>
    private void MarkCustom()
    {
        if (!_filling) Selected = null;
    }

    partial void OnInputTextChanged(string value) => MarkCustom();
    partial void OnOutputTextChanged(string value) => MarkCustom();
    partial void OnCacheReadTextChanged(string value) => MarkCustom();
    partial void OnCacheWriteTextChanged(string value) => MarkCustom();

    /// <summary>The row's final price, or null when input and output are not both
    /// filled in — half a price cannot bill, and guessing the other half would
    /// quietly undercount.</summary>
    public ModelPricing? Result()
    {
        var input = Parse(InputText);
        var output = Parse(OutputText);
        if (input is null || output is null) return null;
        return new ModelPricing(
            input.Value, output.Value, Parse(CacheReadText), Parse(CacheWriteText),
            Selected is null ? ModelPricing.SourceManual : ModelPricing.ModelsDevSource(Selected.ProviderKey));
    }

    private static string Format(double? value) =>
        value is { } price ? price.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

    private static double? Parse(string? text) =>
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : null;
}

/// <summary>
/// Settles the model prices that models.dev reports differently across providers.
///
/// Only genuine disagreements get this far: a model quoted the same by five
/// providers was filled in without asking. What is left is a real question — which
/// of these numbers does this endpoint actually charge — and it is asked per model,
/// because the answer can differ per model.
/// </summary>
public partial class ModelPricingWindow : MolaContentWindow
{
    private readonly ObservableCollection<PricingConflictRow> _rows = [];

    /// <summary>Null until the user confirms; otherwise the prices, keyed by provider
    /// id and then model id.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, ModelPricing>>? Result { get; private set; }

    public ModelPricingWindow() : this([]) { }

    /// <param name="rows">Already carrying their preferred preset — the caller knows
    /// each row's own endpoint and can preselect better than this window could.</param>
    public ModelPricingWindow(IReadOnlyList<PricingConflictRow> rows)
    {
        InitializeComponent();

        foreach (var row in rows) _rows.Add(row);
        PART_Conflicts.ItemsSource = _rows;

        var missing = rows.Count(row => row.Candidates.Count == 0);
        var conflicts = rows.Count - missing;
        // 按行计数而不是按模型：同一个模型挂在两个服务下就是两个待定项，各自可以
        // 有不同结论，说成「N 个模型」会和列表长度对不上。
        PART_Summary.Text = (missing, conflicts) switch
        {
            (> 0, > 0) => $"{missing} 项没有公开价格，{conflicts} 项存在多个报价。请逐项选择来源或填写价格。",
            (> 0, _) => $"{missing} 项没有公开价格，请填写输入和输出价格。",
            _ => $"{conflicts} 项存在多个报价，请选择来源或自定义价格。"
        };

        PART_Cancel.Click += (_, _) => Close();
        PART_Save.Click += (_, _) => Save();
    }

    private void Save()
    {
        var byProvider = new Dictionary<string, Dictionary<string, ModelPricing>>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (row.Result() is not { } pricing) continue;
            if (!byProvider.TryGetValue(row.ProviderId, out var models))
                byProvider[row.ProviderId] = models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
            models[row.ModelId] = pricing;
        }
        Result = byProvider;
        Close();
    }
}
