using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using MolaGPT.Core.Chat;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class ChatHeader : UserControl
{
    private ProviderRegistry? _registry;
    private MainViewModel? _main;

    public ChatHeader()
    {
        InitializeComponent();
        PART_ExpandSidebar.Click += (_, _) => ExpandSidebarRequested?.Invoke(this, EventArgs.Empty);

        PART_ModelSearch.TextChanged += (_, _) => RebuildModelList();
        PART_ModelList.SelectionChanged += OnModelPicked;
        PART_WorkbenchSearch.TextChanged += (_, _) => RebuildWorkbenchModels();
        PART_WorkbenchModels.SelectionChanged += OnWorkbenchModelPicked;

        // The list is rebuilt each time the flyout opens rather than kept live:
        // providers change when a key is added or a sidecar starts, and a stale
        // picker is worse than a rebuild that costs a few hundred rows.
        if (FlyoutBase.GetAttachedFlyout(PART_ModelSelector) is { } flyout)
            flyout.Opened += (_, _) => OnFlyoutOpened();
        else
            PART_ModelSelector.Flyout!.Opened += (_, _) => OnFlyoutOpened();

        PART_WorkbenchSelector.Flyout!.Opened += (_, _) =>
        {
            PART_WorkbenchSearch.Text = string.Empty;
            RebuildWorkbenchModels();
            Dispatcher.UIThread.Post(() => PART_WorkbenchSearch.Focus());
        };
    }

    public event EventHandler? ExpandSidebarRequested;

    /// <summary>Raised after a model is chosen, so the shell can start a new
    /// conversation when the pick crosses the Chat ↔ local-agent boundary.</summary>
    public event EventHandler<AppMode>? ModeBoundaryCrossed;

    /// <summary>Supplied by the shell; the header does not resolve services itself.</summary>
    public void AttachProviders(ProviderRegistry registry) => _registry = registry;

    public void AttachMain(MainViewModel main)
    {
        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.Chat.PropertyChanged -= OnChatPropertyChanged;
            _main.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        }

        _main = main;
        DataContext = main;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.Chat.PropertyChanged += OnChatPropertyChanged;
        _main.Settings.PropertyChanged += OnSettingsPropertyChanged;
        SyncSecondaryUi();
    }

    /// <summary>Opens the picker from elsewhere — the recoverable-error banner's
    /// "换个模型" button routes here rather than carrying its own copy of the list.</summary>
    public void OpenModelSelector() => PART_ModelSelector.Flyout?.ShowAt(PART_ModelSelector);

    /// <summary>Shown only while the sidebar is collapsed, as in the WPF header.</summary>
    public void SetSidebarCollapsed(bool collapsed) =>
        PART_ExpandSidebar.IsVisible = collapsed;

    public void SetModeLabel(string? label) =>
        PART_ModeLabel.Text = label ?? string.Empty;

    public void RefreshSecondaryUi() => SyncSecondaryUi();

    private void OnFlyoutOpened()
    {
        PART_ModelSearch.Text = string.Empty;
        RebuildModelList();
        Dispatcher.UIThread.Post(() => PART_ModelSearch.Focus());
    }

    private void RebuildModelList()
    {
        if (_registry is null || Chat is not { } chat)
        {
            PART_ModelList.ItemsSource = null;
            return;
        }

        // Selection is cleared first: assigning a fresh list raises
        // SelectionChanged for the removal, and without the guard that reads as
        // the user picking a row.
        PART_ModelList.SelectedItem = null;
        PART_ModelList.ItemsSource = ModelSelectorRow.Build(_registry, chat, PART_ModelSearch.Text);
    }

    private void OnModelPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (Chat is not { } chat) return;
        if (PART_ModelList.SelectedItem is not ModelSelectorRow row) return;
        if (row.Provider is null || row.Model is null)
        {
            // Headers and hints are in the same list; clicking one must not
            // leave it looking selected.
            PART_ModelList.SelectedItem = null;
            return;
        }

        // Only crossing the Chat ↔ local-agent boundary needs a new conversation;
        // Work ↔ BYOK share the local agent thread and continue the current one.
        var target = row.Provider.ToAppMode();
        if (chat.CurrentMode.CrossesChatBoundary(target))
            ModeBoundaryCrossed?.Invoke(this, target);

        chat.SetActiveByIds(row.Provider.Id, row.Model.Id, ignoreConversationBoundary: true);
        PART_ModelSelector.Flyout?.Hide();
    }

    private ChatViewModel? Chat => _main?.Chat ?? DataContext as ChatViewModel;

    private void RebuildWorkbenchModels()
    {
        if (_main is null)
        {
            PART_WorkbenchModels.ItemsSource = null;
            return;
        }

        _main.Settings.RefreshImageGenerationProviderModels();
        var query = PART_WorkbenchSearch.Text?.Trim();
        PART_WorkbenchModels.SelectedItem = null;
        PART_WorkbenchModels.ItemsSource = _main.Settings.ImageGenerationProviderModels
            .Where(option => string.IsNullOrWhiteSpace(query)
                || option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.ProviderId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void OnWorkbenchModelPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_main is null) return;
        if (PART_WorkbenchModels.SelectedItem is not ImageGenerationProviderModelOption option) return;

        _main.Settings.WorkbenchImageGenerationProviderId = option.ProviderId;
        _main.Settings.WorkbenchImageGenerationModelId = option.ModelId;
        SyncSecondaryUi();
        PART_WorkbenchSelector.Flyout?.Hide();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CloudSyncStatusKind)
            or nameof(MainViewModel.IsImageWorkbenchVisible)
            or nameof(MainViewModel.ConversationSystemPromptVisible))
        {
            SyncSecondaryUi();
        }
    }

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.ConversationSystemPrompt))
            SyncSecondaryUi();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.WorkbenchImageGenerationProviderId)
            or nameof(SettingsViewModel.WorkbenchImageGenerationModelId))
        {
            SyncSecondaryUi();
        }
    }

    private void SyncSecondaryUi()
    {
        if (_main is null) return;

        PART_SystemPromptDot.IsVisible = !string.IsNullOrWhiteSpace(_main.Chat.ConversationSystemPrompt);
        PART_WorkbenchModelLabel.Text = _main.Settings.SelectedWorkbenchImageGenerationModel?.Label
            ?? _main.Settings.WorkbenchImageGenerationModelId
            ?? "图像模型";

        var brushKey = _main.CloudSyncStatusKind switch
        {
            "Syncing" => "Brush.Primary",
            "Success" => "Brush.Success",
            "Error" => "Brush.Warning",
            _ => "Brush.Text.Muted"
        };
        if (this.TryFindResource(brushKey, ActualThemeVariant, out var value) && value is IBrush brush)
            PART_CloudDot.Fill = brush;
    }
}
