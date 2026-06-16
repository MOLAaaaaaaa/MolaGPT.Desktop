using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

/// <summary>
/// Top-level view model bound to MainWindow.DataContext. Composes the four
/// child VMs and owns chrome-level commands (sidebar toggle, settings, theme,
/// login).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private ConversationListViewModel _conversationList;
    [ObservableProperty] private ChatViewModel _chat;
    [ObservableProperty] private ComposerViewModel _composer;
    [ObservableProperty] private SettingsViewModel _settings;
    [ObservableProperty] private PersonaListViewModel _personas;
    [ObservableProperty] private bool _sidebarCollapsed;
    [ObservableProperty] private bool _artifactPanelVisible;
    [ObservableProperty] private bool _isImageWorkbenchVisible;
    [ObservableProperty] private string _windowTitle = "MolaGPT";
    [ObservableProperty] private string _cloudSyncStatusKind = "Idle";
    [ObservableProperty] private string _cloudSyncStatusText = "云同步待机";
    [ObservableProperty] private string _cloudSyncStatusToolTip = "点击立即同步";
    [ObservableProperty] private bool _cloudSyncStatusVisible;
    [ObservableProperty] private bool _conversationSystemPromptVisible;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateLatestVersion = string.Empty;
    [ObservableProperty] private string? _updateDownloadUrl;
    [ObservableProperty] private string _updateTooltip = "发现新版本";
    [ObservableProperty] private string _updateActionText = "立即下载";
    [ObservableProperty] private string? _updateInstallerSha256;
    [ObservableProperty] private string _updateState = "Available";
    [ObservableProperty] private string _updateChipLabel = "发现更新";
    [ObservableProperty] private string _updateChipDetail = string.Empty;
    private string? _updateNotes;

    /// <summary>Hooked at app startup; opens the LoginDialog. Set by App.xaml.cs to avoid View dependency here.</summary>
    public Action? LoginRequested { get; set; }

    /// <summary>Opens the SettingsWindow. Set by App.xaml.cs.</summary>
    public Action? SettingsRequested { get; set; }

    /// <summary>Opens a BYOK image workbench task. Set by App.xaml.cs.</summary>
    public Action<string?>? ImageWorkbenchRequested { get; set; }

    /// <summary>Opens the AboutWindow. Set by App.xaml.cs.</summary>
    public Action? AboutRequested { get; set; }

    private bool _openSettingsToPersonas;
    private bool _openSettingsWithNewPersona;

    /// <summary>Cycles theme: System → Light → Dark → System. Set by App.xaml.cs.</summary>
    public Action? ThemeToggleRequested { get; set; }

    /// <summary>Opens the per-conversation system prompt editor. Set by App.xaml.cs.</summary>
    public Action? SystemPromptRequested { get; set; }

    /// <summary>Runs a manual cloud sync. Set by App.xaml.cs.</summary>
    public Func<Task>? CloudSyncRequested { get; set; }

    /// <summary>Optional desktop hook: fetch cloud-only conversation details on demand.</summary>
    public Func<string, Task<bool>>? EnsureConversationDetailAsync { get; set; }

    private readonly BackgroundStreamService? _backgroundStreams;

    public MainViewModel(
        ConversationListViewModel conversationList,
        ChatViewModel chat,
        ComposerViewModel composer,
        SettingsViewModel settings,
        PersonaListViewModel personas,
        BackgroundStreamService? backgroundStreams = null)
    {
        _conversationList = conversationList;
        _chat = chat;
        _composer = composer;
        _settings = settings;
        _personas = personas;
        _backgroundStreams = backgroundStreams;

        _conversationList.ConversationSelected += async (_, id) =>
        {
            if (_conversationList.FindItem(id)?.IsImageTask == true)
            {
                if (Composer.IsSending && Chat.ConversationId != id)
                    Composer.DetachToBackground();
                IsImageWorkbenchVisible = true;
                ImageWorkbenchRequested?.Invoke(id);
                return;
            }

            IsImageWorkbenchVisible = false;

            // Draft → first send already bound Chat to this id; only sync sidebar.
            if (string.Equals(Chat.ConversationId, id, StringComparison.Ordinal))
                return;

            if (Composer.IsSending && Chat.ConversationId != id)
                Composer.DetachToBackground();

            var hasBackgroundTask = _backgroundStreams?.HasTask(id) == true;
            await Chat.LoadConversationAsync(id, loadAllMessagesImmediately: hasBackgroundTask);
            if (EnsureConversationDetailAsync is not null && await EnsureConversationDetailAsync(id))
            {
                hasBackgroundTask = _backgroundStreams?.HasTask(id) == true;
                await Chat.LoadConversationAsync(id, loadAllMessagesImmediately: hasBackgroundTask);
            }

            if (hasBackgroundTask)
            {
                _conversationList.SetGenerating(id, false);
                await Composer.ReattachFromBackgroundAsync(id);
            }
        };
        _conversationList.ConversationsDeleted += (_, ids) =>
        {
            if (!string.IsNullOrEmpty(Chat.ConversationId) && ids.Contains(Chat.ConversationId))
                Chat.StartDraftConversation();
        };

        Chat.ConversationTouched += (_, e) =>
        {
            _conversationList.UpsertItem(e.Id, e.Title, e.UpdatedAt, e.ProviderId, e.PersonaLabel);
            if (string.Equals(Chat.ConversationId, e.Id, StringComparison.Ordinal)
                && !string.Equals(_conversationList.SelectedId, e.Id, StringComparison.Ordinal))
            {
                _conversationList.SelectById(e.Id);
            }
        };

        if (_backgroundStreams is not null)
        {
            _backgroundStreams.TaskRegistered += (_, conversationId) =>
                _conversationList.SetGenerating(conversationId, true);
            _backgroundStreams.TaskCompleted += (_, e) =>
                _conversationList.SetGenerating(e.ConversationId, false);
        }

        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ActiveModel) or nameof(ChatViewModel.ActiveProvider))
                RefreshActivePromptState();
        };
        _settings.Providers.CollectionChanged += (_, _) => RefreshActivePromptState();

        // Auto-open/close the artifact panel per conversation: opening when the
        // conversation has local artifacts (BYOK only — MolaGPT-account artifacts
        // live server-side), closing otherwise. Runs on conversation load and
        // after each python run / upload.
        _chat.ArtifactsRefreshed += (_, hasArtifacts) =>
        {
            ArtifactPanelVisible = hasArtifacts && IsArtifactPanelAvailable;
            OnPropertyChanged(nameof(IsArtifactPanelAvailable));
        };
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ActiveProvider))
                OnPropertyChanged(nameof(IsArtifactPanelAvailable));
        };

        RefreshActivePromptState();
    }

    /// <summary>True when the artifact drawer button should be offered: BYOK
    /// provider (artifacts are local) and the conversation has at least one.
    /// MolaGPT-account mode hides it entirely.</summary>
    public bool IsArtifactPanelAvailable =>
        Chat.ActiveProvider?.Kind != ProviderKind.MolaGptProxy && Chat.HasArtifacts;

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void ToggleArtifactPanel() => ArtifactPanelVisible = !ArtifactPanelVisible;

    /// <summary>Opens the OS file explorer with the artifact selected (Windows
    /// <c>explorer /select,</c>). Falls back to opening the containing folder
    /// when selection isn't possible. Never throws into the UI.</summary>
    [RelayCommand]
    private void RevealArtifact(ArtifactItemViewModel? artifact)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.FullPath))
            return;

        try
        {
            if (System.IO.File.Exists(artifact.FullPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{artifact.FullPath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                var dir = System.IO.Path.GetDirectoryName(artifact.FullPath);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
                    {
                        UseShellExecute = true
                    });
            }
        }
        catch (Exception)
        {
            // Best-effort reveal; swallow shell errors so a missing/locked file
            // never crashes the UI.
        }
    }

    [RelayCommand]
    private void NewConversation()
    {
        if (Composer.IsSending)
            Composer.DetachToBackground();

        ConversationList.ClearSelection();
        IsImageWorkbenchVisible = false;
        Chat.StartDraftConversation();
    }

    public void OpenImageWorkbenchTask()
    {
        if (Composer.IsSending)
            Composer.DetachToBackground();

        IsImageWorkbenchVisible = true;
        ImageWorkbenchRequested?.Invoke(null);
    }

    public void CloseImageWorkbench() => IsImageWorkbenchVisible = false;

    [RelayCommand]
    private void OpenLogin() => LoginRequested?.Invoke();

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void OpenAbout() => AboutRequested?.Invoke();

    public void RequestPersonaSettings(bool startNewPersona)
    {
        _openSettingsToPersonas = true;
        _openSettingsWithNewPersona = startNewPersona;
    }

    public (bool OpenPersonas, bool StartNewPersona) ConsumeSettingsOpenRequest()
    {
        var request = (_openSettingsToPersonas, _openSettingsWithNewPersona);
        _openSettingsToPersonas = false;
        _openSettingsWithNewPersona = false;
        return request;
    }

    [RelayCommand]
    private void ToggleTheme() => ThemeToggleRequested?.Invoke();

    [RelayCommand]
    private void OpenSystemPrompt() => SystemPromptRequested?.Invoke();

    [RelayCommand]
    private async Task SyncCloud()
    {
        if (CloudSyncRequested is not null)
            await CloudSyncRequested();
    }

    /// <summary>
    /// Shows the update details dialog. App.xaml.cs wires this to a
    /// window that renders the release notes and offers a download
    /// button; the args are (version, notes, downloadUrl, actionText, installerSha256).
    /// </summary>
    public Action<string, string?, string?, string, string?>? UpdateActionRequested { get; set; }

    public Func<Task>? UpdateBackgroundDownloadRequested { get; set; }

    public Action? UpdateInstallReadyRequested { get; set; }

    [RelayCommand]
    private async Task OpenUpdateDownload()
    {
        switch (UpdateState)
        {
            case "Downloading":
                return;
            case "Ready":
                UpdateInstallReadyRequested?.Invoke();
                return;
            case "Error":
                if (UpdateBackgroundDownloadRequested is not null)
                    await UpdateBackgroundDownloadRequested();
                return;
            default:
                UpdateActionRequested?.Invoke(
                    UpdateLatestVersion, _updateNotes, UpdateDownloadUrl, UpdateActionText, UpdateInstallerSha256);
                return;
        }
    }

    /// <summary>
    /// Surfaces a discovered update on the title bar. Called from
    /// App.xaml.cs once the version-check service finishes.
    /// </summary>
    public void AnnounceUpdate(
        string latestVersion,
        string? downloadUrl,
        string? notes,
        string? actionText = null,
        string? installerSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(latestVersion)) return;
        UpdateLatestVersion = latestVersion;
        UpdateDownloadUrl = downloadUrl;
        _updateNotes = notes;
        UpdateActionText = string.IsNullOrWhiteSpace(actionText) ? "立即下载" : actionText;
        UpdateInstallerSha256 = installerSha256;
        UpdateState = "Available";
        UpdateChipLabel = "发现更新";
        UpdateChipDetail = $"v{latestVersion}";
        UpdateTooltip = $"发现新版本 v{latestVersion}，点击查看更新内容";
        UpdateAvailable = true;
    }

    public void BeginUpdateDownload()
    {
        UpdateState = "Downloading";
        UpdateChipLabel = "下载更新";
        UpdateChipDetail = "0%";
        UpdateTooltip = "正在下载更新";
        UpdateAvailable = true;
    }

    public void ReportUpdateDownloadProgress(double progress)
    {
        var percent = Math.Clamp((int)(progress * 100), 0, 100);
        UpdateChipDetail = $"{percent}%";
        UpdateTooltip = $"正在下载更新 {percent}%";
    }

    public void MarkUpdateReady()
    {
        UpdateState = "Ready";
        UpdateChipLabel = "安装更新";
        UpdateChipDetail = "并重启";
        UpdateTooltip = "更新已下载，点击安装并重启";
    }

    public void MarkUpdateFailed(string message)
    {
        UpdateState = "Error";
        UpdateChipLabel = "更新失败";
        UpdateChipDetail = "重试";
        UpdateTooltip = string.IsNullOrWhiteSpace(message) ? "更新下载失败，点击重试" : message;
    }

    public void UpdateCloudSyncStatus(string kind, string message, DateTimeOffset timestamp)
    {
        CloudSyncStatusKind = string.IsNullOrWhiteSpace(kind) ? "Idle" : kind;
        CloudSyncStatusText = string.IsNullOrWhiteSpace(message) ? "云同步待机" : message;
        CloudSyncStatusToolTip = $"{CloudSyncStatusText} · {timestamp:HH:mm:ss}";
        CloudSyncStatusVisible = CloudSyncStatusKind is "Syncing" or "Success" or "Error";
    }

    public void HideCloudSyncStatus()
    {
        CloudSyncStatusVisible = false;
    }

    private void RefreshActivePromptState()
    {
        ConversationSystemPromptVisible = Chat.ActiveProvider is not null
            && Chat.ActiveProvider.Kind != ProviderKind.MolaGptProxy;
        Chat.ActiveModelSystemPrompt = ConversationSystemPromptVisible
            ? Settings.GetModelSystemPrompt(Chat.ActiveProvider?.Id, Chat.ActiveModel?.Id)
            : null;
    }
}
