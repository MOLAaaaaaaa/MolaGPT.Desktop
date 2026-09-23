using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
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
    [ObservableProperty] private string _quotaText = "账号额度";
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
    public Func<string, Task<bool>>? EnsureConversationDetailAsync { get; set; }

    private readonly BackgroundStreamService? _backgroundStreams;
    private readonly MolaGptProxyProvider? _molaGptProxy;
    private int _quotaRefreshVersion;

    public MainViewModel(
        ConversationListViewModel conversationList,
        ChatViewModel chat,
        ComposerViewModel composer,
        SettingsViewModel settings,
        PersonaListViewModel personas,
        BackgroundStreamService? backgroundStreams = null,
        MolaGptProxyProvider? molaGptProxy = null)
    {
        _conversationList = conversationList;
        _chat = chat;
        _composer = composer;
        _settings = settings;
        _personas = personas;
        _backgroundStreams = backgroundStreams;
        _molaGptProxy = molaGptProxy;
        _chat.AutoCollapseThinking = _settings.AutoCollapseThinking;
        _chat.CodeFenceKinds = _settings.CodeFenceKinds;

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
            // A turn that died is just as over as one that answered. Without this
            // the row keeps spinning forever on the failure path.
            _backgroundStreams.TaskFailed += (_, e) =>
                _conversationList.SetGenerating(e.ConversationId, false);
        }

        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.ActiveModel) or nameof(ChatViewModel.ActiveProvider))
                RefreshActivePromptState();
        };
        _settings.Providers.CollectionChanged += (_, _) => RefreshActivePromptState();

        WireArtifacts();

        // Quota chip visibility follows the active mode (Chat/Work = MolaGPT
        // account, BYOK never) and the account login state. Refresh whenever
        // either moves. The text is pulled from status.php and scoped to the
        // active model so Chat and Work show the same shared account quota.
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.CurrentMode)
                or nameof(ChatViewModel.ActiveModel)
                or nameof(ChatViewModel.ActiveProvider))
            {
                OnPropertyChanged(nameof(IsQuotaChipVisible));
                OnPropertyChanged(nameof(IsCloudSyncChipVisible));
                OnPropertyChanged(nameof(IsSpendChipVisible));
                _ = RefreshQuotaAsync();
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
            if (e.PropertyName is nameof(SettingsViewModel.IsLoggedIn))
            {
                OnPropertyChanged(nameof(IsQuotaChipVisible));
                _ = RefreshQuotaAsync();
            }
        };
        _composer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ComposerViewModel.IsSending) && !Composer.IsSending)
                _ = RefreshQuotaAsync();
        };

        RefreshActivePromptState();
        _ = RefreshQuotaAsync();
    }

    /// <summary>Quota chip shows only for MolaGPT-account modes (Chat / Work) and
    /// only after the user has signed in. BYOK uses the user's own key and has no
    /// shared quota to display.</summary>
    public bool IsQuotaChipVisible =>
        Chat.CurrentMode.IsMolaGptAccount() && Settings.IsLoggedIn;

    public bool IsCloudSyncChipVisible =>
        CloudSyncStatusVisible && Chat.CurrentMode == AppMode.Chat;

    /// <summary>Only where the money is the user's: the official proxy bills in
    /// credits, which the quota chip already shows and which must not be added
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

    public async Task RefreshQuotaAsync(CancellationToken ct = default)
    {
        var version = ++_quotaRefreshVersion;
        if (!IsQuotaChipVisible || _molaGptProxy is null || Chat.ActiveModel is null)
        {
            QuotaText = "账号额度";
            return;
        }

        try
        {
            var status = await _molaGptProxy.FetchStatusAsync(ct);
            if (version != _quotaRefreshVersion) return;
            if (status is null)
            {
                Settings.IsLoggedIn = false;
                QuotaText = "账号额度";
                return;
            }
            QuotaText = BuildQuotaText(status, Chat.ActiveModel.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MolaGptAuthExpiredException)
        {
            if (version == _quotaRefreshVersion)
            {
                Settings.IsLoggedIn = false;
                QuotaText = "账号额度";
            }
        }
        catch
        {
            if (version == _quotaRefreshVersion)
                QuotaText = "账号额度 · 暂不可用";
        }
    }

    private static string BuildQuotaText(MolaGptStatus? status, string modelId)
    {
        if (status is null || string.IsNullOrWhiteSpace(modelId))
            return "账号额度";

        var used = status.Usage.GetValueOrDefault(modelId, 0);
        status.Limits.TryGetValue(modelId, out var limit);
        status.ModelStatus.TryGetValue(modelId, out var modelStatus);

        // Credit pool takes precedence: once the server switches it on, every
        // model's daily_limit is -1 and the legacy branch below would claim
        // "无限" while the user is one turn away from being blocked.
        if (status.Credits is { } credits && !status.Unlimited)
            return BuildCreditQuotaText(credits, modelStatus);

        var unlimited = status.Unlimited
            || limit?.DailyRequests == -1
            || modelStatus?.Remaining == -1;
        if (unlimited)
            return $"今日 {used}/无限 · 账号共用";

        var effectiveLimit = EffectiveLimit(limit?.DailyRequests, modelStatus?.Remaining, used);
        if (effectiveLimit > 0)
            return $"今日 {used}/{effectiveLimit} · 账号共用";

        if (modelStatus?.Remaining is { } remaining)
            return $"剩余 {remaining} · 账号共用";

        return "账号额度";
    }

    /// <summary>
    /// Chip text for the shared credit pool. Leads with "还能用几次" rather than
    /// a percentage — the chip is next to the composer, where the actionable
    /// question is whether this next turn will go through.
    /// </summary>
    private static string BuildCreditQuotaText(MolaGptCredits credits, MolaGptModelStatus? modelStatus)
    {
        if (modelStatus?.CreditMultiplier is null)
            return modelStatus is null
                ? $"额度剩余 {credits.RemainingPercent}% · 账号共用"
                : "该模型暂不可用";

        if (credits.Exhausted)
            return $"额度已耗尽 · {credits.RecoveryLabel}";

        var uses = credits.EstimatedUses(modelStatus.CreditMultiplier) ?? 0;
        return uses switch
        {
            int.MaxValue => "不消耗额度 · 账号共用",
            <= 0 => "剩余额度不足以再发一次",
            _ => $"约 {uses} 次 · 账号共用"
        };
    }

    private static int EffectiveLimit(int? declaredLimit, int? remaining, int used)
    {
        if (declaredLimit is -1 or null)
            return remaining is null ? 0 : Math.Max(0, remaining.Value + used);
        if (remaining is null)
            return declaredLimit.Value;
        return Math.Min(declaredLimit.Value, remaining.Value + used);
    }

    /// <summary>The drawer is offered whenever the conversation has anything in
    /// it. Fences exist in every mode; working-directory files only in BYOK,
    /// which <see cref="ChatViewModel.RefreshArtifacts"/> already accounts for.</summary>
    public bool IsArtifactPanelAvailable => Chat.HasArtifacts;

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
        // Working-directory files open the drawer, as they did before fences
        // joined it: on conversation load and after a python run or upload.
        Chat.ArtifactsRefreshed += (_, hasFiles) =>
        {
            if (hasFiles) ArtifactPanelVisible = true;
            EnsureSelectionAlive();
        };
        Chat.ArtifactWorkspace.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsArtifactPanelAvailable));
            OnPropertyChanged(nameof(IsArtifactHandleVisible));
            EnsureSelectionAlive();
        };
        Chat.ArtifactWorkspace.LiveArtifactChanged += OnLiveArtifactChanged;
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
            if (e.PropertyName != nameof(ChatViewModel.ConversationId)) return;
            // Another conversation's artifacts are not this one's.
            SelectedArtifact = null;
            ArtifactCanvasVisible = false;
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
        if (!Chat.HasArtifacts) ArtifactPanelVisible = false;
    }

    public void OpenArtifact(ArtifactItemViewModel artifact, int versionIndex = -1)
    {
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
        ArtifactCanvasMaximized = false;
        ArtifactCanvasVisible = false;
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

    [RelayCommand]
    private void NewConversation()
    {
        if (Composer.IsSending)
            Composer.DetachToBackground();

        ConversationList.ClearSelection();
        IsImageWorkbenchVisible = false;
        Chat.StartDraftConversation();
    }

    /// <summary>Switch the sidebar mode slider to BYOK / Chat / Work. Routes to each
    /// mode's representative provider. When the target MolaGPT-account provider
    /// isn't registered (user not signed in) it opens the login dialog instead.</summary>
    [RelayCommand]
    private void SwitchMode(string? mode)
    {
        var target = mode?.ToLowerInvariant() == "chat" ? AppMode.Chat : AppMode.Work;
        var fromMode = Chat.CurrentMode;

        // "MolaGPT Work" is the unified local-agent capability. Clicking it while
        // already in an agent mode (Work or BYOK) is a no-op — the specific wallet
        // (MolaGPT account vs custom API key) is chosen in the model selector.
        if (target == AppMode.Work && fromMode.IsLocalAgent())
        {
            // …unless the workbench is up, in which case leaving it is the whole
            // point of the click. Neither segment is lit there, so both have to
            // be a real exit; otherwise the one that happens to match
            // Chat.CurrentMode silently does nothing, and which one that is
            // depends on what was loaded before the workbench opened.
            IsImageWorkbenchVisible = false;
            return;
        }

        if (Chat.SwitchToMode(target, out var needsLogin))
        {
            // Outside the boundary check for the same reason: clicking 「Chat」
            // from the workbench while the chat behind it was already in Chat
            // crosses nothing, and used to leave the user staring at the
            // workbench wondering why the button was dead.
            IsImageWorkbenchVisible = false;
            if (fromMode.CrossesChatBoundary(target))
            {
                ConversationList.ClearSelection();
                Chat.StartDraftConversation();
            }
            return;
        }

        // Falls through to login / settings without closing the workbench: a
        // click that ends in a dialog did not switch anything, so it should not
        // throw away where the user was.

        // Target not ready: surface login when that's the gap (Chat / shared Work
        // need a signed-in account), otherwise open Settings to add a provider.
        if (needsLogin)
            LoginRequested?.Invoke();
        else
            SettingsRequested?.Invoke();
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
        if (Composer.IsSending)
            Composer.DetachToBackground();

        ConversationList.ClearSelection();
        IsImageWorkbenchVisible = true;
        ImageWorkbenchRequested?.Invoke(null);
    }

    public void CloseImageWorkbench() => IsImageWorkbenchVisible = false;

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
