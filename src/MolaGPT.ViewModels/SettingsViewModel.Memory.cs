using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Memory;

namespace MolaGPT.ViewModels;

/// <summary>
/// Local memory switches. One master switch that is off until the user turns it
/// on, and three sub-switches that are on underneath it.
///
/// The privacy gate is the master switch alone. Making the user hunt for which
/// sub-switch is still off after he deliberately turned memory on would only
/// make the feature look broken — the sub-switches exist to carve out one part
/// (say, searching old conversations) while keeping the rest.
/// </summary>
public sealed partial class SettingsViewModel
{
    private const string MemoryEnabledKey = "memory_enabled";
    private const string MemoryUseKey = "memory_use_enabled";
    private const string MemoryRecallKey = "memory_recall_enabled";
    private const string MemoryAutoLearnKey = "memory_auto_learn_enabled";
    private const string MemoryBudgetKey = "memory_budget_tokens";
    private const string MemoryModelKeySetting = "memory_model_key";
    private const string MemoryAllowSensitiveKey = "memory_allow_sensitive";

    [ObservableProperty] private bool _memoryEnabled;
    [ObservableProperty] private bool _memoryUseEnabled = true;
    [ObservableProperty] private bool _memoryRecallEnabled = true;
    [ObservableProperty] private bool _memoryAutoLearnEnabled = true;
    [ObservableProperty] private int _memoryBudgetTokens = MemoryProjector.DefaultBudgetTokens;
    [ObservableProperty] private bool _memoryAllowSensitive;

    /// <summary>
    /// "&lt;providerId&gt;::&lt;modelId&gt;" for the model that runs consolidation.
    /// Null means the user has not chosen one, and nothing runs — it deliberately
    /// does not fall back to the conversation's model: consolidation spends the
    /// user's own quota on a request he never asked for.
    /// </summary>
    [ObservableProperty] private string? _memoryModelKey;

    private void LoadMemorySettings()
    {
        if (_settingsRepo is null) return;
        if (bool.TryParse(_settingsRepo.Get(MemoryEnabledKey), out var enabled)) MemoryEnabled = enabled;
        if (bool.TryParse(_settingsRepo.Get(MemoryUseKey), out var use)) MemoryUseEnabled = use;
        if (bool.TryParse(_settingsRepo.Get(MemoryRecallKey), out var recall)) MemoryRecallEnabled = recall;
        if (bool.TryParse(_settingsRepo.Get(MemoryAutoLearnKey), out var learn)) MemoryAutoLearnEnabled = learn;
        if (bool.TryParse(_settingsRepo.Get(MemoryAllowSensitiveKey), out var sensitive)) MemoryAllowSensitive = sensitive;
        if (int.TryParse(_settingsRepo.Get(MemoryBudgetKey), out var budget)
            && MemoryProjector.BudgetOptions.Contains(budget))
        {
            MemoryBudgetTokens = budget;
        }
        var modelKey = _settingsRepo.Get(MemoryModelKeySetting);
        if (!string.IsNullOrWhiteSpace(modelKey)) MemoryModelKey = modelKey;
    }

    public ObservableCollection<MemoryProviderModelOption> MemoryProviderModels { get; } = [];

    [ObservableProperty] private MemoryProviderModelOption? _selectedMemoryProviderModel;

    /// <summary>
    /// Auto-learn is on but no model has been chosen, so nothing runs. Surfaced
    /// as a dot on the 记忆 entry in the settings rail: a feature that silently
    /// does nothing is worse than one that admits it is not set up.
    /// </summary>
    public bool MemoryNeedsModel =>
        ShowStatusDots && MemoryEnabled && MemoryAutoLearnEnabled && string.IsNullOrWhiteSpace(MemoryModelKey);

    /// <summary>
    /// No 「跟随当前对话模型」 entry, unlike title generation. Consolidation spends
    /// the user's own quota on a request he did not ask for, so it only ever
    /// runs on a model he picked on purpose.
    /// </summary>
    public void RefreshMemoryProviderModels()
    {
        var wasLoading = _loadingSettings;
        string? removedModelKey = null;
        _loadingSettings = true;
        try
        {
            MemoryProviderModels.Clear();
            foreach (var provider in Providers.Where(p => p.Enabled && !IsImagePurpose(p.Purpose)))
            {
                foreach (var model in provider.Models)
                {
                    MemoryProviderModels.Add(new MemoryProviderModelOption(
                        provider.Id, model.Id, $"{provider.Name} / {model.DisplayName}"));
                }
            }

            var selected = MemoryProviderModels.FirstOrDefault(option => option.Key == MemoryModelKey);
            // The chosen model was removed or disabled: forget it rather than leave a
            // key pointing at nothing, so the red dot comes back and says so.
            if (selected is null && !string.IsNullOrWhiteSpace(MemoryModelKey))
            {
                removedModelKey = MemoryModelKey;
                MemoryModelKey = null;
            }

            SelectedMemoryProviderModel = selected;
        }
        finally { _loadingSettings = wasLoading; }

        // The property change above was intentionally suppressed while the list
        // was rebuilt. Persist only the real "model no longer exists" case.
        if (removedModelKey is not null && _settingsRepo is not null)
            _settingsRepo.Remove(MemoryModelKeySetting);
    }

    partial void OnSelectedMemoryProviderModelChanged(MemoryProviderModelOption? value)
    {
        if (_loadingSettings) return;
        // Rebuilding ItemsSource can briefly report null after the list has been
        // repopulated. Keep the stored key while its model is still available.
        if (value is null && MemoryModelKey is not null
            && MemoryProviderModels.Any(option => option.Key == MemoryModelKey)) return;
        MemoryModelKey = value?.Key;
    }

    partial void OnMemoryEnabledChanged(bool value)
    {
        PersistMemory(MemoryEnabledKey, value.ToString());
        OnPropertyChanged(nameof(MemoryNeedsModel));
    }

    partial void OnMemoryUseEnabledChanged(bool value) => PersistMemory(MemoryUseKey, value.ToString());
    partial void OnMemoryRecallEnabledChanged(bool value) => PersistMemory(MemoryRecallKey, value.ToString());

    partial void OnMemoryAutoLearnEnabledChanged(bool value)
    {
        PersistMemory(MemoryAutoLearnKey, value.ToString());
        OnPropertyChanged(nameof(MemoryNeedsModel));
    }
    partial void OnMemoryAllowSensitiveChanged(bool value) => PersistMemory(MemoryAllowSensitiveKey, value.ToString());
    partial void OnMemoryBudgetTokensChanged(int value) =>
        PersistMemory(MemoryBudgetKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    partial void OnMemoryModelKeyChanged(string? value)
    {
        OnPropertyChanged(nameof(MemoryNeedsModel));
        if (_loadingSettings || _settingsRepo is null) return;
        if (string.IsNullOrWhiteSpace(value)) _settingsRepo.Remove(MemoryModelKeySetting);
        else _settingsRepo.Set(MemoryModelKeySetting, value);
    }

    private void PersistMemory(string key, string value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(key, value);
    }
}

/// <summary>A provider/model pair for consolidation. <see cref="Key"/> is what
/// gets stored, so the settings row survives a provider being renamed.</summary>
public sealed record MemoryProviderModelOption(string ProviderId, string ModelId, string Label)
{
    public string Key => ProviderId + "::" + ModelId;
}
