using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Chat.Tools.PythonExecution;

namespace MolaGPT.ViewModels;

/// <summary>
/// The review model for all local tools. Reviewers read the selection from the
/// settings table on every call.
/// </summary>
public sealed partial class SettingsViewModel
{
    public const string ReviewProviderIdKey = "python_review_provider_id";
    public const string ReviewModelIdKey = "python_review_model_id";

    private string? _reviewProviderId;
    private string? _reviewModelId;

    /// <summary>No 「跟随当前对话」 entry: the reviewer should not change with
    /// whichever model the conversation happens to use, least of all to the one
    /// whose code it is judging.</summary>
    public ObservableCollection<TitleProviderModelOption> ReviewProviderModels { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewModelMissing))]
    private TitleProviderModelOption? _selectedReviewProviderModel;

    public bool IsReviewModelMissing => SelectedReviewProviderModel is not { } selected
        || _providerRegistry is null
        || PythonAutoReviewer.ResolveTarget(_providerRegistry, selected.ProviderId, selected.ModelId) is null;

    private void LoadAutoReviewSettings()
    {
        if (_settingsRepo is null) return;
        _reviewProviderId = _settingsRepo.Get(ReviewProviderIdKey);
        _reviewModelId = _settingsRepo.Get(ReviewModelIdKey);
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

        // Whether the saved model resolves depends on the registry, not only on which
        // option is selected, so an unchanged selection can still flip.
        OnPropertyChanged(nameof(IsReviewModelMissing));
    }

    partial void OnSelectedReviewProviderModelChanged(TitleProviderModelOption? value)
    {
        if (_loadingSettings) return;
        // Rebuilding the list can briefly report null; a real choice is never null.
        if (value is null) return;
        _reviewProviderId = value.ProviderId;
        _reviewModelId = value.ModelId;
        SetOrRemove(ReviewProviderIdKey, _reviewProviderId);
        SetOrRemove(ReviewModelIdKey, _reviewModelId);
    }
}
