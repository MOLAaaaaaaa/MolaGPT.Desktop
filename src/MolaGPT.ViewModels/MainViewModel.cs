using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Models;
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

    /// <summary>Opens the Agent status window (headless bridge sessions). Set by App.xaml.cs.</summary>
    public Action? AgentStatusRequested { get; set; }

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
    public Func<string, CancellationToken, Task<bool>>? EnsureConversationDetailAsync { get; set; }

    private readonly BackgroundStreamService? _backgroundStreams;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountSecurityStatus))]
    private string? _accountSecurityStatus;
    public bool HasAccountSecurityStatus => !string.IsNullOrWhiteSpace(AccountSecurityStatus);

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
        _chat.AutoCollapseThinking = _settings.AutoCollapseThinking;
        _chat.CodeFenceKinds = _settings.CodeFenceKinds;

        _conversationList.ConversationSelected += async (_, id) => await SelectConversationAsync(id);
        _conversationList.ConversationsDeleted += (_, ids) =>
        {
            // A deleted conversation's tasks have nowhere to report to.
            foreach (var id in ids)
            {
                Composer.StopConversation(id);
                Chat.Tasks?.StopConversation(id);
            }
            if (!string.IsNullOrEmpty(Chat.ConversationId) && ids.Contains(Chat.ConversationId))
            {
                Composer.DetachToBackground();
                Chat.StartDraftConversation();
            }
        };

        Chat.ConversationTouched += (_, e) =>
        {
            _conversationList.UpsertItem(e.Id, e.Title, e.UpdatedAt, e.ProviderId, e.PersonaLabel);
            if (!IsImageWorkbenchVisible && string.Equals(Chat.ConversationId, e.Id, StringComparison.Ordinal)
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
                RefreshGenerating(e.ConversationId);
            // A turn that died is just as over as one that answered. Without this
            // the row keeps spinning forever on the failure path.
            _backgroundStreams.TaskFailed += (_, e) =>
                RefreshGenerating(e.ConversationId);
            _backgroundStreams.TaskEnded += (_, conversationId) => RefreshGenerating(conversationId);

            void RefreshGenerating(string id) =>
                _conversationList.SetGenerating(id, _backgroundStreams.HasTask(id));
        }

        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ActiveModel) or nameof(ChatViewModel.ActiveProvider))
                RefreshActivePromptState();
        };

        WireArtifacts();

        // The plan under the context gauge follows the active model: Work draws
        // on the MolaGPT account's credits.
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.CurrentMode)
                or nameof(ChatViewModel.ActiveModel)
                or nameof(ChatViewModel.ActiveProvider))
            {
                OnPropertyChanged(nameof(IsCloudSyncChipVisible));
                OnPropertyChanged(nameof(IsSpendChipVisible));
                _ = RefreshUsageAsync();
            }
            if (e.PropertyName is nameof(ChatViewModel.Spend))
            {
                OnPropertyChanged(nameof(IsSpendChipVisible));
                OnPropertyChanged(nameof(SpendChipText));
                OnPropertyChanged(nameof(SpendChipTooltip));
            }
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.AutoCollapseThinking))
                _chat.AutoCollapseThinking = _settings.AutoCollapseThinking;
            if (e.PropertyName is nameof(SettingsViewModel.CodeFenceKinds))
                _chat.CodeFenceKinds = _settings.CodeFenceKinds;
            if (e.PropertyName == nameof(SettingsViewModel.AccountSessionGeneration))
            {
                UpdateCloudSyncStatus("Idle", "云同步待机", DateTimeOffset.Now);
                _ = RefreshUsageAsync();
            }
            if (e.PropertyName == nameof(SettingsViewModel.AccountStatus))
                UpdateAccountUsage();
        };
        // A turn may spend MolaGPT credits.
        _composer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ComposerViewModel.IsSending) && !Composer.IsSending)
                _ = RefreshUsageAsync(force: true);
        };
        _chat.ContextGauge.Usage.RefreshRequested = () => RefreshUsageAsync();

        RefreshActivePromptState();
        _ = RefreshUsageAsync();
    }

    public bool IsCloudSyncChipVisible =>
        CloudSyncStatusVisible && Chat.CurrentMode == AppMode.Chat;

    /// <summary>Only where the money is the user's: the official proxy bills in
    /// credits, which the context gauge already shows and which must not be added
    /// to a dollar figure.</summary>
    public bool IsSpendChipVisible =>
        Chat.Spend.HasSpend && Chat.CurrentMode.IsLocalAgent();

    public string SpendChipText => MessageViewModel.FormatCost(Chat.Spend.CostUsd);

    /// <summary>
    /// Spelling out the basis is not optional: the total counts retried turns and
    /// branches the user regenerated away from, so it is legitimately larger than
    /// adding up the answers still on screen. Without this line it reads as a bug.
    /// </summary>
    public string SpendChipTooltip
    {
        get
        {
            var lines = new List<string> { $"本会话花费 {SpendChipText}" };
            lines.AddRange(Chat.Spend.ByModel
                .Take(6)
                .Select(item => $"{item.Model}　{MessageViewModel.FormatCost(item.CostUsd)}（{item.Turns} 次）"));
            lines.Add("含重试与未采用的分支");
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// The override edits the system prompt of the *chat* conversation. The
    /// image workbench has no system prompt — the button there would open an
    /// editor writing to whichever chat happened to be loaded behind it.
    /// </summary>
    public bool IsSystemPromptButtonVisible =>
        ConversationSystemPromptVisible && !IsImageWorkbenchVisible;

    /// <summary>Branch the conversation at <paramref name="message"/> into one of its
    /// own and open it. Branching at a user message puts that message back in the
    /// input box, since the branch ends just before it.</summary>
    public async Task BranchToNewConversationAsync(MessageViewModel message)
    {
        var branch = await Chat.BranchToNewConversationAsync(message);
        if (branch is null) return;
        ConversationList.UpsertItem(branch.Id, branch.Title, DateTimeOffset.UtcNow, branch.ProviderId, branch.PersonaLabel);
        ConversationList.SelectById(branch.Id);
        if (!string.IsNullOrWhiteSpace(branch.Draft)) Composer.Text = branch.Draft!;
    }

    /// <summary>
    /// Brings the plan usage under the context gauge up to date with the active
    /// model. A failed read keeps the last figures rather than blanking them; a
    /// different plan replaces them straight away.
    /// </summary>
    /// <param name="force">Read again even if the last reading is recent, because
    /// something has just been spent.</param>
    public async Task RefreshUsageAsync(bool force = false, CancellationToken ct = default)
    {
        UpdateAccountUsage();
        if (!Settings.IsLoggedIn || !MolaGptProviderIds.IsMolaGptAccount(Chat.ActiveProvider?.Id)) return;
        try { await Settings.RefreshAccountStatusAsync(force, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
    }

    private void UpdateAccountUsage()
    {
        var status = Settings.AccountStatus;
        var providerId = Chat.ActiveProvider?.Id;
        var work = providerId == MolaGptProviderIds.LocalTools;
        AccountSecurityStatus = MolaGptProviderIds.IsMolaGptAccount(providerId)
            ? (work ? status?.WorkSecurityStatus : status?.SecurityStatus) : null;
        Composer.CanResumeRestrictedWork = work && status?.CanResumeWork == true;
        if (Chat.CurrentMode == AppMode.Work && status is not null)
            Chat.ContextGauge.Usage.ShowCredits(status, Chat.ActiveModel?.Id ?? string.Empty);
        else
            Chat.ContextGauge.Usage.Clear();
    }

    /// <summary>The drawer is offered whenever the conversation has anything in
    /// it. Fences exist in every mode; working-directory files only in BYOK,
    /// which <see cref="ChatViewModel.RefreshArtifacts"/> already accounts for.</summary>
    public bool IsArtifactPanelAvailable => Chat.HasArtifacts || Chat.HasSubagents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtifactPanelSelected))]
    [NotifyPropertyChangedFor(nameof(IsSubagentListVisible))]
    [NotifyPropertyChangedFor(nameof(IsSubagentDetailVisible))]
    private bool _subagentPanelSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubagentListVisible))]
    [NotifyPropertyChangedFor(nameof(IsSubagentDetailVisible))]
    private SubagentViewModel? _selectedSubagent;

    public bool IsArtifactPanelSelected => !SubagentPanelSelected;
    public bool IsSubagentListVisible => SubagentPanelSelected && SelectedSubagent is null;
    public bool IsSubagentDetailVisible => SubagentPanelSelected && SelectedSubagent is not null;

    partial void OnSubagentPanelSelectedChanged(bool value)
    {
        if (value) ArtifactCanvasVisible = false;
    }

    /// <summary>Entry the canvas is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedArtifact))]
    [NotifyPropertyChangedFor(nameof(CanRevealSelectedArtifact))]
    [NotifyPropertyChangedFor(nameof(CanCopySelectedArtifact))]
    [NotifyPropertyChangedFor(nameof(CanShowSelectedArtifactSource))]
    [NotifyPropertyChangedFor(nameof(CanReviseSelectedArtifact))]
    private ArtifactItemViewModel? _selectedArtifact;

    public bool HasSelectedArtifact => SelectedArtifact is not null;
    public bool CanRevealSelectedArtifact => SelectedArtifact?.CanReveal == true;
    public bool CanCopySelectedArtifact => SelectedArtifact?.CanCopy == true;
    public bool CanShowSelectedArtifactSource => SelectedArtifact?.CanShowSource == true;
    public bool CanReviseSelectedArtifact => SelectedArtifact?.CanRevise == true;

    /// <summary>Canvas vs list on the shared right rail.</summary>
    [ObservableProperty] private bool _artifactCanvasVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtifactHandleVisible))]
    private bool _artifactCanvasMaximized;

    public bool IsArtifactHandleVisible => IsArtifactPanelAvailable && !ArtifactCanvasMaximized;

    /// <summary>Peek source instead of the live render.</summary>
    [ObservableProperty] private bool _artifactSourceMode;

    /// <summary>
    /// Where the selection came from, so it can be found again when its entry
    /// is regrouped — a fence that starts untitled and then reveals its file
    /// name on the first line moves into that file's entry mid-stream.
    /// </summary>
    private (MessageViewModel Message, int Ordinal)? _selectionAnchor;

    /// <summary>The user closed the drawer during this turn: stop opening it for
    /// the rest of the turn. Cleared when the next message is sent.</summary>
    private bool _autoOpenDismissed;

    partial void OnSelectedArtifactChanged(ArtifactItemViewModel? value)
    {
        _selectionAnchor = value?.CurrentVersion is { } version ? (version.Message, version.Ordinal) : null;
    }

    partial void OnArtifactCanvasVisibleChanged(bool value)
    {
        if (!value) ArtifactCanvasMaximized = false;
    }

    partial void OnArtifactPanelVisibleChanged(bool value)
    {
        if (!value) ArtifactCanvasMaximized = false;
    }

    private void WireArtifacts()
    {
        Chat.ArtifactsRefreshed += (_, hasNewFiles) =>
        {
            if (hasNewFiles && !_autoOpenDismissed) ArtifactPanelVisible = true;
            EnsureSelectionAlive();
        };
        Chat.ArtifactWorkspace.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsArtifactPanelAvailable));
            OnPropertyChanged(nameof(IsArtifactHandleVisible));
            EnsureSelectionAlive();
        };
        Chat.ArtifactWorkspace.LiveArtifactChanged += OnLiveArtifactChanged;
        Chat.Subagents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsArtifactPanelAvailable));
            OnPropertyChanged(nameof(IsArtifactHandleVisible));
            if (SelectedSubagent is { } selected && !Chat.Subagents.Contains(selected))
                SelectedSubagent = null;
            if (!Chat.HasSubagents && SubagentPanelSelected)
            {
                SubagentPanelSelected = false;
                SelectedSubagent = null;
            }
        };
        Chat.TaskChanged += task =>
        {
            if (task.ConversationId != Chat.ConversationId || !task.IsRunning
                || task.Kind != AgentTaskKinds.Agent || ArtifactPanelVisible || _autoOpenDismissed)
                return;
            SubagentPanelSelected = true;
            ArtifactPanelVisible = true;
        };
        Chat.SubagentInspectRequested += agentId =>
        {
            var agent = Chat.Subagents.FirstOrDefault(item => item.Id == agentId);
            if (agent is not null) OpenSubagent(agent);
        };
        Chat.ArtifactActionRequested += (_, e) =>
        {
            switch (e.Action)
            {
                case ArtifactAction.Open:
                    OpenArtifact(e.Item, e.VersionIndex);
                    break;
                case ArtifactAction.Revise:
                    Composer.AddArtifactReference(e.Item, e.VersionIndex);
                    break;
            }
        };
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ConversationId) or nameof(ChatViewModel.ActiveProvider))
                OnPropertyChanged(nameof(CanOpenWorkspaceFolder));
            if (e.PropertyName != nameof(ChatViewModel.ConversationId)) return;
            // Another conversation's artifacts are not this one's.
            SelectedArtifact = null;
            ArtifactCanvasVisible = false;
            SelectedSubagent = null;
            SubagentPanelSelected = false;
            ArtifactPanelVisible = false;
            _autoOpenDismissed = false;
        };
        Composer.MessageSubmitted += () => _autoOpenDismissed = false;
    }

    /// <summary>
    /// Follows the answer being written: a new HTML / SVG / Mermaid fence opens
    /// on the canvas as it starts, unless the user has closed the drawer during
    /// this turn or turned the behaviour off — or reads that format as code in
    /// the answer, where a canvas opening by itself would be the old chip
    /// behaviour coming back through the side door.
    /// </summary>
    private void OnLiveArtifactChanged(object? sender, LiveArtifactEventArgs e)
    {
        if (!e.IsNewFence || _autoOpenDismissed || !Settings.CanvasAutoOpen) return;
        if (e.Item.RenderKind is not (Presentation.Artifacts.ArtifactRenderKind.Html
            or Presentation.Artifacts.ArtifactRenderKind.Svg
            or Presentation.Artifacts.ArtifactRenderKind.Mermaid)) return;
        if (Chat.CodeFenceKinds.Contains(e.Item.RenderKind)) return;
        OpenArtifact(e.Item, e.Item.Versions.Count - 1);
    }

    private void EnsureSelectionAlive()
    {
        if (SelectedArtifact is null) return;
        if (Chat.Artifacts.Contains(SelectedArtifact)) return;

        if (_selectionAnchor is { } anchor
            && Chat.ArtifactWorkspace.FindItem(anchor.Message, anchor.Ordinal) is { } replacement)
        {
            replacement.SelectVersion(replacement.IndexOfVersion(anchor.Message, anchor.Ordinal));
            SelectedArtifact = replacement;
            return;
        }

        SelectedArtifact = null;
        ArtifactCanvasVisible = false;
        if (!IsArtifactPanelAvailable) ArtifactPanelVisible = false;
    }

    public void OpenArtifact(ArtifactItemViewModel artifact, int versionIndex = -1)
    {
        SubagentPanelSelected = false;
        if (versionIndex >= 0) artifact.SelectVersion(versionIndex);
        SelectedArtifact = artifact;
        _selectionAnchor = artifact.CurrentVersion is { } version ? (version.Message, version.Ordinal) : null;
        ArtifactSourceMode = false;
        ArtifactCanvasVisible = true;
        ArtifactPanelVisible = true;
    }

    [RelayCommand]
    private void OpenArtifactInCanvas(ArtifactItemViewModel? artifact)
    {
        if (artifact is not null) OpenArtifact(artifact);
    }

    [RelayCommand]
    private void ToggleArtifactSourceMode() => ArtifactSourceMode = !ArtifactSourceMode;

    [RelayCommand]
    private void ShowArtifactList()
    {
        SubagentPanelSelected = false;
        ArtifactCanvasMaximized = false;
        ArtifactCanvasVisible = false;
        ArtifactPanelVisible = true;
    }

    [RelayCommand]
    private void ShowSubagentList()
    {
        SubagentPanelSelected = true;
        SelectedSubagent = null;
        ArtifactPanelVisible = true;
    }

    public void OpenSubagent(SubagentViewModel agent)
    {
        SubagentPanelSelected = true;
        SelectedSubagent = agent;
        ArtifactPanelVisible = true;
    }

    [RelayCommand]
    private void ShowArtifactCanvas()
    {
        var target = SelectedArtifact ?? Chat.Artifacts.FirstOrDefault();
        if (target is not null) OpenArtifact(target);
    }

    [RelayCommand]
    private void ToggleArtifactCanvasMaximized() =>
        ArtifactCanvasMaximized = !ArtifactCanvasMaximized;

    [RelayCommand]
    private void SelectPreviousArtifact() => SelectArtifactAtOffset(-1);

    [RelayCommand]
    private void SelectNextArtifact() => SelectArtifactAtOffset(1);

    private void SelectArtifactAtOffset(int offset)
    {
        if (Chat.Artifacts.Count == 0) return;
        var index = SelectedArtifact is null ? -1 : Chat.Artifacts.IndexOf(SelectedArtifact);
        if (index < 0) index = 0;
        index = (index + offset + Chat.Artifacts.Count) % Chat.Artifacts.Count;
        OpenArtifact(Chat.Artifacts[index]);
    }

    [RelayCommand]
    private void SelectPreviousVersion() => StepVersion(-1);

    [RelayCommand]
    private void SelectNextVersion() => StepVersion(1);

    private void StepVersion(int offset)
    {
        if (SelectedArtifact is not { } artifact) return;
        artifact.SelectVersion(artifact.CurrentVersionIndex + offset);
        _selectionAnchor = artifact.CurrentVersion is { } version ? (version.Message, version.Ordinal) : null;
        ArtifactSourceMode = false;
    }

    /// <summary>Puts a reference to the artifact in the composer. The next turn
    /// carries it to the model; the source itself only travels when the model
    /// cannot already see it.</summary>
    [RelayCommand]
    private void ReviseArtifact(ArtifactItemViewModel? artifact)
    {
        artifact ??= SelectedArtifact;
        if (artifact is null) return;
        Composer.AddArtifactReference(artifact, artifact.CurrentVersionIndex);
    }

    /// <summary>The page on the canvas threw. Hand the error and the page to the
    /// model in one step.</summary>
    public void ReportArtifactError(string message)
    {
        if (SelectedArtifact is not { } artifact) return;
        var trimmed = message.Length > 600 ? message[..600] + "…" : message;
        Composer.AddArtifactReference(artifact, artifact.CurrentVersionIndex,
            $"页面运行时报错：\n{trimmed}\n请找出原因并修复。");
    }

    /// <summary>Raised with the text to place on the OS clipboard (view-layer handles it).</summary>
    public event EventHandler<string>? CopyTextRequested;

    [RelayCommand]
    private void CopyArtifact(ArtifactItemViewModel? artifact)
    {
        artifact ??= SelectedArtifact;
        if (artifact is null) return;
        var text = artifact.Content;
        if (text is null && !string.IsNullOrEmpty(artifact.FullPath) && System.IO.File.Exists(artifact.FullPath))
        {
            try
            {
                var info = new System.IO.FileInfo(artifact.FullPath);
                if (info.Length <= 2 * 1024 * 1024) text = System.IO.File.ReadAllText(artifact.FullPath);
            }
            catch
            {
                return;
            }
        }

        if (text is null) return;
        CopyTextRequested?.Invoke(this, text);
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void ToggleArtifactPanel()
    {
        ArtifactPanelVisible = !ArtifactPanelVisible;
        if (!ArtifactPanelVisible) _autoOpenDismissed = true;
    }

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

    /// <summary>Account chats run their tools on the server and have no local
    /// working directory; a draft has no id to name one by.</summary>
    public bool CanOpenWorkspaceFolder =>
        !string.IsNullOrEmpty(Chat.ConversationId) && Chat.ActiveProvider?.Kind != ProviderKind.MolaGptProxy;

    /// <summary>Opens the conversation's working directory — where its tools
    /// write and its uploads are copied. Created if nothing has been written
    /// yet, so files can be put there for the next turn.</summary>
    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        if (!CanOpenWorkspaceFolder) return;
        try
        {
            var directory = MolaGPT.Core.Chat.Tools.PythonExecution.PythonExecutionTool.GetSessionDirectory(Chat.ConversationId);
            System.IO.Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer refusing to open is not worth a dialog.
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

    private async Task SelectConversationAsync(string id)
    {
        var needsReload = IsImageWorkbenchVisible || Chat.IsConversationLoading
            || _backgroundStreams?.HasTask(id) == true;
        if (Composer.IsSending && Chat.ConversationId != id)
            Composer.DetachToBackground();
        if (ConversationList.FindItem(id)?.IsImageTask == true)
        {
            Chat.BeginNavigation();
            IsImageWorkbenchVisible = true;
            ImageWorkbenchRequested?.Invoke(id);
            return;
        }
        IsImageWorkbenchVisible = false;
        // The first send in a draft only synchronizes the sidebar selection.
        if (Chat.ConversationId == id && (!needsReload || Composer.IsSending)) return;

        var navigation = Chat.BeginNavigation(loading: true);
        var background = _backgroundStreams?.GetTask(id);
        try
        {
            Task Load() => Chat.LoadConversationAsync(id, _backgroundStreams?.HasTask(id) == true, navigation);
            await Load();
            if (navigation.IsCancellationRequested) return;
            if (background is { IsInFlight: false })
                await Load();
            else if (background is null && _backgroundStreams?.HasTask(id) != true
                && EnsureConversationDetailAsync is { } fetch
                && await fetch(id, navigation))
                await Load();
            if (navigation.IsCancellationRequested) return;
            Composer.ReattachFromBackground(id, navigation);
            if (!navigation.IsCancellationRequested) Composer.RestoreUnsentInjections(id);
        }
        catch (OperationCanceledException) when (navigation.IsCancellationRequested) { }
        finally
        {
            Chat.CompleteNavigation(navigation);
        }
    }

    public bool TrySwitchMode(AppMode mode)
    {
        // The Work segment represents the local agent, independent of its billing provider.
        if (mode == AppMode.Work && Chat.ActiveProvider is not null && Chat.CurrentMode.IsLocalAgent())
        {
            CloseImageWorkbench();
            return true;
        }
        if (Chat.ResolveModeTarget(mode) is not { } target) return false;
        ApplyChatSelection(target.Provider, target.Model);
        return true;
    }

    public bool TrySelectChatModel(string providerId, string modelId, out bool startedDraft)
    {
        startedDraft = false;
        if (Chat.ResolveUserModel(providerId, modelId) is not { } target) return false;
        startedDraft = ApplyChatSelection(target.Provider, target.Model);
        return true;
    }

    private bool ApplyChatSelection(IChatProvider provider, ProviderModel model)
    {
        var crossing = Chat.CurrentMode.CrossesChatBoundary(provider.ToAppMode()) || !Chat.CanSwitchToProvider(provider);
        if (crossing) NewConversation();
        Chat.ApplyModelChoice(provider, model);
        CloseImageWorkbench();
        return crossing;
    }

    public void LeaveHiddenAccountProviders()
    {
        if (!MolaGptProviderIds.IsMolaGptAccount(Chat.ActiveProvider?.Id) && !Chat.HasAccountConversation) return;
        // Hiding account entries also leaves a stored Work conversation, even
        // though a normal Work ↔ BYOK model change stays in that conversation.
        if (Chat.HasAccountConversation) NewConversation();
        if (Chat.ResolveModeTarget(AppMode.Byok) is { } target)
            Chat.ApplyModelChoice(target.Provider, target.Model);
        else
        {
            Chat.ActiveProvider = null;
            Chat.ActiveModel = null;
        }
    }

    /// <summary>
    /// Opens a fresh, unsaved image task. Selection is cleared for the same
    /// reason <see cref="NewConversation"/> clears it: the entry point now sits
    /// in the sidebar, so leaving the previous chat's row highlighted would
    /// point at something the main pane is no longer showing. The task's own row
    /// appears once the first prompt creates it.
    /// </summary>
    public void OpenImageWorkbenchTask()
    {
        Chat.BeginNavigation();
        if (Composer.IsSending)
            Composer.DetachToBackground();

        ConversationList.ClearSelection();
        IsImageWorkbenchVisible = true;
        ImageWorkbenchRequested?.Invoke(null);
    }

    public void CloseImageWorkbench()
    {
        if (!IsImageWorkbenchVisible) return;
        if (Chat.ConversationId is { } id) ConversationList.SelectById(id);
        else ConversationList.ClearSelection();
        IsImageWorkbenchVisible = false;
    }

    [RelayCommand]
    private void OpenLogin() => LoginRequested?.Invoke();

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void OpenAgentStatus() => AgentStatusRequested?.Invoke();

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

    public void ReportUpdateDownloadProgress(double? progress)
    {
        if (progress is not { } value)
        {
            UpdateChipDetail = string.Empty;
            UpdateTooltip = "正在下载更新";
            return;
        }
        var percent = Math.Clamp((int)(value * 100), 0, 100);
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

    /// <summary>
    /// Drives the header chip, which is now purely a "syncing right now" light.
    /// Outcomes (成功 / 失败) are events and belong to the notification banners —
    /// left on the chip, a success message parked in the header forever, because
    /// nothing ever published an idle state to clear it.
    /// </summary>
    public void UpdateCloudSyncStatus(string kind, string message, DateTimeOffset timestamp)
    {
        CloudSyncStatusKind = string.IsNullOrWhiteSpace(kind) ? "Idle" : kind;
        CloudSyncStatusText = string.IsNullOrWhiteSpace(message) ? "云同步待机" : message;
        CloudSyncStatusToolTip = $"{CloudSyncStatusText} · {timestamp:HH:mm:ss}";
        CloudSyncStatusVisible = CloudSyncStatusKind is "Syncing";
    }

    partial void OnCloudSyncStatusVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsCloudSyncChipVisible));

    partial void OnConversationSystemPromptVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsSystemPromptButtonVisible));

    partial void OnIsImageWorkbenchVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsSystemPromptButtonVisible));

    private void RefreshActivePromptState() =>
        ConversationSystemPromptVisible = Chat.ActiveProvider is not null
            && Chat.ActiveProvider.Kind != ProviderKind.MolaGptProxy;
}
