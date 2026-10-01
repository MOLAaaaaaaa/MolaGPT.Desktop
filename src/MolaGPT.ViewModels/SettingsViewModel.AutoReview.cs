using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Chat.Agents.Pi;

namespace MolaGPT.ViewModels;

/// <summary>
/// Automatic review of Python runs: a model the user picked answers the
/// approvals the user would otherwise be asked, and hands back whatever it does
/// not approve. Read by the reviewer straight from the settings table, so a
/// change applies to the next run without anything being rebuilt.
/// </summary>
public sealed partial class SettingsViewModel
{
    public const string PythonAutoReviewKey = "python_auto_review_enabled";
    public const string PythonReviewProviderIdKey = "python_review_provider_id";
    public const string PythonReviewModelIdKey = "python_review_model_id";

    [ObservableProperty] private bool _pythonAutoReviewEnabled;

    private string? _reviewProviderId;
    private string? _reviewModelId;

    /// <summary>No 「跟随当前对话」 entry: the reviewer should not change with
    /// whichever model the conversation happens to use, least of all to the one
    /// whose code it is judging.</summary>
    public ObservableCollection<TitleProviderModelOption> ReviewProviderModels { get; } = new();

    [ObservableProperty] private TitleProviderModelOption? _selectedReviewProviderModel;

    private void LoadAutoReviewSettings()
    {
        if (_settingsRepo is null) return;
        if (bool.TryParse(_settingsRepo.Get(PythonAutoReviewKey), out var enabled)) PythonAutoReviewEnabled = enabled;
        _reviewProviderId = _settingsRepo.Get(PythonReviewProviderIdKey);
        _reviewModelId = _settingsRepo.Get(PythonReviewModelIdKey);
    }

    public void RefreshReviewProviderModels()
    {
        var wasLoading = _loadingSettings;
        _loadingSettings = true;
        try
        {
            ReviewProviderModels.Clear();
            if (_providerRegistry is not null)
            {
                foreach (var provider in _providerRegistry.Providers.OfType<PiWorkProvider>())
                {
                    foreach (var model in provider.Models)
                        ReviewProviderModels.Add(new TitleProviderModelOption(
                            provider.Id, model.Id, $"{provider.DisplayName} / {model.DisplayName}"));
                }
            }

            var selected = ReviewProviderModels.FirstOrDefault(option =>
                string.Equals(option.ProviderId, _reviewProviderId, StringComparison.Ordinal)
                && string.Equals(option.ModelId, _reviewModelId, StringComparison.Ordinal));
            // Kept rather than forgotten: providers register after startup, and a
            // model that is merely not loaded yet must not lose its selection.
            if (selected is null && !string.IsNullOrWhiteSpace(_reviewModelId))
            {
                selected = new TitleProviderModelOption(
                    _reviewProviderId, _reviewModelId,
                    $"{_reviewProviderId} / {_reviewModelId}（不可用）");
                ReviewProviderModels.Add(selected);
            }
            SelectedReviewProviderModel = selected;
        }
        finally
        {
            _loadingSettings = wasLoading;
        }
    }

    partial void OnPythonAutoReviewEnabledChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(PythonAutoReviewKey, value.ToString());
    }

    partial void OnSelectedReviewProviderModelChanged(TitleProviderModelOption? value)
    {
        if (_loadingSettings) return;
        // Rebuilding the list can briefly report null; a real choice is never null.
        if (value is null) return;
        _reviewProviderId = value.ProviderId;
        _reviewModelId = value.ModelId;
        SetOrRemove(PythonReviewProviderIdKey, _reviewProviderId);
        SetOrRemove(PythonReviewModelIdKey, _reviewModelId);
    }
}
