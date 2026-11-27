using System.Collections.Specialized;
using System.ComponentModel;
using System.Net.Http;
using Avalonia;
using Avalonia.Input.Platform;      // ClipboardExtensions.SetTextAsync
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MolaGPT.App.Infrastructure;
using MolaGPT.App.Rendering;
using MolaGPT.App.Remote;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

public partial class MainWindow : MolaWindow
{
    private readonly MainViewModel _main;
    private readonly ChatViewModel _chat;
    private readonly ConversationListViewModel _conversations;
    private readonly ComposerViewModel _composer;
    private readonly SettingsViewModel _settings;
    private readonly UpdateCheckService _updateCheck;
    private readonly MolaGptAuthService _auth;
    private readonly CloudSyncService _cloudSync;
    private readonly ImageWorkbenchTasks _imageTasks;
    private readonly PythonRuntimeSetup _pythonSetup;
    private readonly AgentRuntimeSetup _agentRuntime;
    private readonly NotificationCenter _notifications;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<SettingsWindow> _createSettingsWindow;
    private readonly Func<LoginWindow> _createLoginWindow;
    private readonly RemoteWorkSettingsViewModel _remoteWork;
    private readonly SponsorsService _sponsors;
    private readonly MembershipService _membership;
    private bool _openingMembership;

    private bool _sidebarCollapsed;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private SupportWindow? _supportWindow;
    private ImageGenerationWorkbenchView? _imageWorkbench;
    internal string? DisplayedImageTaskId => _main.IsImageWorkbenchVisible ? _imageWorkbench?.ConversationId : null;
    private int _compactionTokensBeforeAtStart;
    private readonly Transitions _artifactPanelTransitions = new();
    private bool _artifactResizeActive;
    private Point _artifactResizeStart;
    private double _artifactResizeStartWidth;
    private double _artifactCanvasWidth = 480;

    /// <summary>Shared so that flipping sides twice in a row replaces the banner
    /// instead of stacking two.</summary>
    private const string ModeSwitchNotificationKey = "mode-switch";

    /// <summary>Progress on the way in, outcome on the way out, one key so the
    /// second replaces the first.</summary>
    private const string CompactionNotificationKey = "context-compaction";

    /// <summary>The in-app banner stack. <see cref="NotificationRouter"/> drives it.</summary>
    public NotificationHost Notifications => PART_Notifications;

    /// <summary>For controls built in code inside the transcript, which have no
    /// injection path of their own; they reach it through their TopLevel.</summary>
    internal NotificationCenter NotificationCenter => _notifications;

    internal MainWindow(
        MainViewModel main,
        ProviderRegistry providers,
        UpdateCheckService updateCheck,
        MolaGptAuthService auth,
        CloudSyncService cloudSync,
        AttachmentStore attachmentStore,
        ImageWorkbenchTasks imageTasks,
        PythonRuntimeSetup pythonSetup,
        AgentRuntimeSetup agentRuntime,
        NotificationCenter notifications,
        IHttpClientFactory httpClientFactory,
        Func<SettingsWindow> createSettingsWindow,
        Func<LoginWindow> createLoginWindow,
        RemoteWorkSettingsViewModel remoteWork,
        SponsorsService sponsors,
        MembershipService membership)
    {
        _main = main;
        _main.CopyTextRequested += OnCopyTextRequested;
        _chat = main.Chat;
        _conversations = main.ConversationList;
        _composer = main.Composer;
        _settings = main.Settings;
        _updateCheck = updateCheck;
        _auth = auth;
        _cloudSync = cloudSync;
        _imageTasks = imageTasks;
        _notifications = notifications;
        _httpClientFactory = httpClientFactory;
        _sponsors = sponsors;
        _membership = membership;
        _pythonSetup = pythonSetup;
        _agentRuntime = agentRuntime;
        _createSettingsWindow = createSettingsWindow;
        _createLoginWindow = createLoginWindow;
        _remoteWork = remoteWork;
        _composer.EnsureAgentRuntimeAsync = EnsureAgentRuntimeForSendAsync;

        InitializeComponent();
        PART_Sidebar.DataContext = _conversations;
        PART_Transcript.DataContext = _chat;
        PART_Transcript.AttachAttachmentStore(attachmentStore);
        PART_Composer.DataContext = _composer;
        // The composer floats over the transcript; whatever height it has is kept
        // clear at the transcript's bottom.
        PART_ComposerOverlay.SizeChanged += (_, e) => PART_Transcript.SetBottomReserve(
            e.NewSize.Height + PART_ComposerOverlay.Margin.Bottom - ComposerOverTranscriptPadding);
        DataContext = _main;
        ApplyFontScale(_settings.FontScale);
        StreamTailFade.Configure(_settings.StreamFadeEnabled);
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        PART_TitleBar.SetSettingsNoticeVisible(_settings.AccountEntryIsNew);
        PART_TitleBar.SetRemotePhoneConnected(_remoteWork.PhoneConnected);
        _remoteWork.PropertyChanged += OnRemoteWorkPropertyChanged;

        PART_Header.AttachProviders(providers);
        PART_Header.AttachMain(_main);
        PART_Transcript.Bind(TranscriptView.AllowMessageEditingProperty, new Avalonia.Data.Binding("CanEditHistory") { Source = _chat });
        PART_Transcript.Bind(TranscriptView.IsRoleChatProperty, new Avalonia.Data.Binding("IsRoleChat") { Source = _chat });
        PART_TitleBar.SettingsRequested += (_, _) => OpenSettings();
        PART_TitleBar.AboutRequested += (_, _) => OpenAbout();
        PART_TitleBar.ThemeToggleRequested += (_, _) => ToggleTheme();
        PART_TitleBar.LoginRequested += async (_, _) => await OpenAccountAsync();
        PART_TitleBar.AgentStatusRequested += (_, _) => OpenAgentSettings();

        PART_ArtifactCanvas.AskRequested += (_, image) =>
        {
            // A maximized canvas covers the composer the picture is going to.
            _main.ArtifactCanvasMaximized = false;
            PART_Composer.AttachImage(image.Png, image.FileName);
        };
        PART_Composer.PersonaSettingsRequested += (_, startNew) => OpenPersonaSettings(startNew);
        PART_Composer.ApprovalSettingsRequested += (_, _) => OpenApprovalSettings();
        PART_Composer.SearchSettingsRequested += (_, _) => OpenSearchSettings();
        PART_Composer.ProviderToolsRequested += (_, providerId) => OpenProviderToolsSettings(providerId);
        PART_Composer.PersonaSelectionFailed += (_, error) => _notifications.Error("角色切换失败", error, "persona-selection");
        _main.SystemPromptRequested = () => _ = OpenSystemPromptAsync();
        _main.ImageWorkbenchRequested = conversationId => OpenImageWorkbench(conversationId);

        // Width is animated on the compositor, so collapsing the sidebar stays
        // smooth even while a conversation is still materializing behind it.
        // The WPF version animated the Grid column instead, because a
        // ColumnDefinition is not a FrameworkElement and cannot carry a
        // storyboard; here the card itself is animatable.
        var slide = new Transitions
        {
            new DoubleTransition
            {
                Property = WidthProperty,
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = new CubicEaseOut()
            }
        };
        PART_SidebarCard.Transitions = slide;
        PART_SidebarGap.Transitions = slide;

        // Same treatment for the artifact drawer. It used to appear and vanish
        // by IsVisible, which is the one thing a drawer must not do — the panel
        // it is standing in for slides.
        _artifactPanelTransitions.Add(new DoubleTransition
        {
            Property = WidthProperty,
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseOut()
        });
        PART_ArtifactCard.Transitions = _artifactPanelTransitions;
        PART_ArtifactGap.Transitions = _artifactPanelTransitions;
        _main.PropertyChanged += OnMainPropertyChanged;
        PART_SubagentScroll.ScrollChanged += OnSubagentScrollChanged;
        PART_SubagentScroll.PointerWheelChanged += (_, e) =>
        {
            if (e.Delta.Y > 0) _subagentFollowBottom = false;
        };
        SyncArtifactPanel();
        PART_BodyGrid.SizeChanged += OnArtifactLayoutSizeChanged;
        PART_SidebarCard.SizeChanged += OnArtifactLayoutSizeChanged;
        PART_SidebarGap.SizeChanged += OnArtifactLayoutSizeChanged;

        PART_Sidebar.CollapseRequested += (_, _) => SetSidebarCollapsed(true);
        PART_Header.ExpandSidebarRequested += (_, _) => SetSidebarCollapsed(false);
        PART_Header.NewConversationRequested += (_, _) => NewConversation();
        PART_Sidebar.NewConversationRequested += (_, _) => NewConversation();
        PART_Sidebar.NewImageTaskRequested += (_, _) => _main.OpenImageWorkbenchTask();
        PART_TitleBar.ModeRequested += (_, mode) => SwitchMode(mode);

        // Picking a model from the other side of the Chat ↔ local-agent boundary
        // cannot continue the current thread, so the header asks for a fresh one.
        // Reported after the fact rather than warned about in the picker: written
        // on the picker's footer button, the consequence read as that button's.
        // Not raised for the title-bar segments — there the user watched the mode
        // move, and a banner restating it is noise.
        PART_Header.ModeBoundaryCrossed += (_, mode) =>
        {
            _notifications.Info(
                $"已切换到 {ModelSelectorRow.SideLabel(mode)}",
                "已为你新建一个对话",
                key: ModeSwitchNotificationKey);
        };
        PART_Transcript.HintChosen += (_, text) =>
        {
            _composer.Text = text;
            PART_Composer.FocusInput();
        };
        PART_Transcript.RetryRequested += (_, message) =>
        {
            if (_composer.RetryCommand.CanExecute(message))
                _composer.RetryCommand.Execute(message);
        };
        PART_Transcript.EditRequested += async (_, message) =>
        {
            if (!_chat.CanEditHistory) return;
            var text = await new MessageEditorWindow().ShowForAsync(message.FullContent, this);
            if (text is not null)
            {
                try
                {
                    var edited = await _chat.EditMessageAsync(message, text);
                    if (edited?.Role == ChatMessage.RoleUser)
                        await _composer.ReplyToExistingUserAsync(edited);
                }
                catch (Exception ex) { _notifications.Error("消息保存失败", ex.Message, "message-edit"); }
            }
        };
        PART_Transcript.ContinueRequested += (_, message) =>
        {
            if (_composer.ContinueCommand.CanExecute(message)) _composer.ContinueCommand.Execute(message);
        };
        PART_Transcript.ForkRequested += async (_, message) =>
        {
            try
            {
                var branch = await _chat.CreateBranchAsync(message);
                if (branch?.Role == ChatMessage.RoleUser)
                    await _composer.ReplyToExistingUserAsync(branch);
            }
            catch (Exception ex) { _notifications.Error("无法建立分支", ex.Message, "conversation-fork"); }
        };
        PART_Transcript.BranchSelectionRequested += async (message, offset) =>
        {
            try { await _chat.SelectMessageBranchAsync(message, offset); }
            catch (Exception ex) { _notifications.Error("无法切换时间线", ex.Message, "conversation-branch"); }
        };
        PART_Transcript.BranchToConversationRequested += async (_, message) =>
        {
            try { await _main.BranchToNewConversationAsync(message); }
            catch (Exception ex) { _notifications.Error("无法建立分支", ex.Message, "conversation-fork"); }
        };
        PART_Transcript.StopTaskRequested += (_, taskId) => _chat.StopTask(taskId);

        PART_Transcript.ErrorActionRequested += async (_, message) =>
        {
            switch (message.ErrorAction)
            {
                case MessageErrorAction.SwitchModel:
                    PART_Header.OpenModelSelector();
                    break;
                case MessageErrorAction.Retry:
                    if (_composer.RetryCommand.CanExecute(message)) await _composer.RetryAsync(message);
                    break;
                case MessageErrorAction.SignIn:
                    await OpenLoginAsync(this, force: true);
                    break;
                case MessageErrorAction.AccountSecurity:
                    await OpenAccountSecurityAsync();
                    break;
                case MessageErrorAction.ViewUsage:
                    await OpenAccountAsync();
                    break;
            }
        };
        void RefreshVisibleMessageErrors()
        {
            foreach (var message in _chat.Messages)
            {
                if (message.RequestError is null) continue;
                message.RefreshErrorAction(_composer.ContinueCommand.CanExecute(message),
                    _composer.RetryCommand.CanExecute(message));
            }
        }
        var errorActionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        errorActionTimer.Tick += (_, _) =>
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            RefreshVisibleMessageErrors();
        };
        errorActionTimer.Start();
        Closed += (_, _) => errorActionTimer.Stop();
        Activated += (_, _) =>
        {
            RefreshVisibleMessageErrors();
            _ = _main.RefreshUsageAsync(force: true);
        };

        _chat.PropertyChanged += OnChatPropertyChanged;
        // The gauge instance outlives every conversation, so one subscription covers
        // all of them. Compaction is the one thing this app does that takes seconds,
        // rewrites the conversation, and — until now — happened entirely inside a
        // popup nobody has open at the time.
        _chat.ContextGauge.PropertyChanged += OnContextGaugePropertyChanged;
        _composer.Attachments.CollectionChanged += OnComposerAttachmentsChanged;
        SyncChrome();
        RefreshAccountState();
        ApplyAccountFeatureVisibility();

        PART_TitleBar.CloseRequested += (_, _) => Close();

        Opened += (_, _) =>
        {
            PART_Composer.FocusInput();
            _agentRuntime.OfferUpdateForIncompatibleInstall(OpenProviderSettings);
        };
        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            _chat.PropertyChanged -= OnChatPropertyChanged;
            _remoteWork.PropertyChanged -= OnRemoteWorkPropertyChanged;
            _main.SystemPromptRequested = null;
            _main.ImageWorkbenchRequested = null;
            _composer.EnsureAgentRuntimeAsync = null;
            _chat.ContextGauge.PropertyChanged -= OnContextGaugePropertyChanged;
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.CopyTextRequested -= OnCopyTextRequested;
            _composer.Attachments.CollectionChanged -= OnComposerAttachmentsChanged;
        };
    }

    /// <summary>
    /// A spreadsheet, PDF or Office file just went into the composer and nothing
    /// can run code over its original. MolaGPT Chat opens files on the server, a
    /// persona that switched Python off has already decided, and without the agent
    /// runtime there is no local side for Python to serve yet.
    /// </summary>
    private void OnComposerAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
        if (_chat.ActiveProvider?.Kind == ProviderKind.MolaGptProxy) return;
        if (_chat.ActivePersona?.Profile.EnablePython == false) return;
        if (!_agentRuntime.IsCompatible) return;
        _pythonSetup.OfferForAttachments(e.NewItems.OfType<Attachment>());
    }

    /// <summary>Apply the account entry visibility preference.</summary>
    private void ApplyAccountFeatureVisibility()
    {
        var show = _settings.ShowAccountFeatures;
        PART_TitleBar.SetAccountFeaturesVisible(show);
        _conversations.ShowAccountConversations = show;
        _chat.AccountProvidersAllowed = show;
        if (!show) _main.LeaveHiddenAccountProviders();
    }

    /// <summary>
    /// Say out loud that the conversation is being summarized.
    ///
    /// Both kinds land here: the manual button and the automatic cut mid-answer. The
    /// automatic one is the reason this exists at all — it happens without anyone
    /// asking, takes a model call to finish, and until now the only sign of it was
    /// the answer appearing to stall.
    ///
    /// <see cref="ContextGaugeViewModel.ApplyCompaction"/> sets the outcome before it
    /// lowers the flag, so the finished branch below reads the result rather than the
    /// state the run started in.
    /// </summary>
    private void OnContextGaugePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ContextGaugeViewModel.IsCompacting)) return;

        var gauge = _chat.ContextGauge;
        if (gauge.IsCompacting)
        {
            // Remembered so the end can tell "this run cut something" from "this
            // conversation compacted at some point earlier": an aborted run leaves
            // the previous run's numbers standing, and reporting those as fresh
            // would announce a compaction that did not happen.
            _compactionTokensBeforeAtStart = gauge.LastCompactionTokensBefore;
            _notifications.Progress(CompactionNotificationKey, "正在压缩上下文");
            return;
        }

        if (gauge.HasCompactionError)
        {
            _notifications.Error("上下文压缩失败", gauge.CompactionError, CompactionNotificationKey);
            return;
        }

        if (gauge.HasCompacted && gauge.LastCompactionTokensBefore != _compactionTokensBeforeAtStart)
        {
            _notifications.Success("上下文已压缩", gauge.CompactedText, CompactionNotificationKey);
            return;
        }

        // Aborted, or the agent found nothing worth summarizing. A progress banner is
        // showing and nothing is going to replace it, so retract it rather than
        // leaving "正在压缩上下文" pinned to a run that already ended.
        _notifications.Dismiss(CompactionNotificationKey);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FontScale))
            ApplyFontScale(_settings.FontScale);
        if (e.PropertyName == nameof(SettingsViewModel.StreamFadeEnabled))
            StreamTailFade.Configure(_settings.StreamFadeEnabled);
        if (e.PropertyName == nameof(SettingsViewModel.ShowAccountFeatures))
            ApplyAccountFeatureVisibility();
        if (e.PropertyName == nameof(SettingsViewModel.AccountEntryIsNew))
            PART_TitleBar.SetSettingsNoticeVisible(_settings.AccountEntryIsNew);
        PART_TitleBar.SetRemotePhoneConnected(_remoteWork.PhoneConnected);
        _remoteWork.PropertyChanged += OnRemoteWorkPropertyChanged;
        if (e.PropertyName == nameof(SettingsViewModel.AccountSessionGeneration))
            RefreshAccountState();
    }

    internal void ApplyFontScale(double value)
        => ApplyFontScale(PART_UiScale, value);

    internal static void ApplyFontScale(LayoutTransformControl host, double value)
    {
        var scale = SettingsViewModel.NormalizeFontScale(value);
        host.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.CurrentMode)
            or nameof(ChatViewModel.ActiveModel)
            or nameof(ChatViewModel.ActiveModelLabel)
            or nameof(ChatViewModel.ConversationTitle))
        {
            SyncChrome();
        }
    }

    private void SyncChrome()
    {
        Title = string.IsNullOrWhiteSpace(_chat.ConversationTitle)
            ? "MolaGPT"
            : _chat.ConversationTitle;

        // Work stays lit for BYOK: the account-vs-own-key split is chosen in the
        // model selector, not in the title bar.
        //
        // Neither is lit while the image workbench is up. Both segments say
        // 「…对话」 — they answer "where does this conversation run", and the
        // workbench is not a conversation. Lighting one used to just leak
        // whichever mode happened to be loaded behind it. Unlit, the pair reads
        // as what it now is: the way back to a conversation. SyncModeThumb
        // already handles the no-target case by hiding the capsule.
        var mode = _chat.CurrentMode;
        var inWorkbench = _main.IsImageWorkbenchVisible;
        PART_TitleBar.SetMode(
            !inWorkbench && mode == AppMode.Chat,
            !inWorkbench && mode is AppMode.Work or AppMode.Byok);
        PART_Header.SetModeLabel(_chat.ActiveModeLabel);
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        if (_sidebarCollapsed == collapsed) return;
        _sidebarCollapsed = collapsed;

        PART_SidebarCard.Width = collapsed ? 0 : 280;
        PART_SidebarGap.Width = collapsed ? 0 : 16;
        PART_Header.SetSidebarCollapsed(collapsed);
    }

    /// <summary>Compact list width. Canvas width is user-resizable.</summary>
    private const double ArtifactPanelWidth = 300;
    private const double ArtifactPanelGap = 16;

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ArtifactPanelVisible)
            or nameof(MainViewModel.IsArtifactPanelAvailable)
            or nameof(MainViewModel.ArtifactCanvasVisible)
            or nameof(MainViewModel.ArtifactCanvasMaximized))
        {
            SyncArtifactPanel();
        }

        // The mode segments read this flag, and entering or leaving the
        // workbench does not touch ChatViewModel — so without this the capsule
        // would only catch up on the next unrelated chat change.
        if (e.PropertyName is nameof(MainViewModel.IsImageWorkbenchVisible))
        {
            if (!_main.IsImageWorkbenchVisible) _imageWorkbench?.SetPresented(false);
            SyncChrome();
        }
    }

    /// <summary>
    /// Drives the drawer off its two view-model flags. Width rather than
    /// IsVisible, so the transition above has something to animate — and the
    /// availability flag closes it outright, because a panel that slid shut but
    /// left its 16px gutter behind is just a gap nobody can explain.
    /// </summary>
    private void SyncArtifactPanel()
    {
        var open = _main.ArtifactPanelVisible && _main.IsArtifactPanelAvailable;
        var canvas = open && _main.IsArtifactPanelSelected && _main.ArtifactCanvasVisible;
        var maximized = canvas && _main.ArtifactCanvasMaximized;

        PART_MainCard.IsVisible = !maximized;
        Grid.SetColumn(PART_ArtifactCard, maximized ? 2 : 4);
        Grid.SetColumnSpan(PART_ArtifactCard, maximized ? 3 : 1);
        PART_ArtifactCard.HorizontalAlignment = maximized
            ? Avalonia.Layout.HorizontalAlignment.Stretch
            : Avalonia.Layout.HorizontalAlignment.Left;

        double width;
        if (maximized)
        {
            PART_ArtifactCard.Transitions = null;
            PART_ArtifactCard.MaxWidth = double.PositiveInfinity;
            PART_ArtifactCard.Width = double.NaN;
            PART_ArtifactGap.Width = 0;
            width = PART_BodyGrid.Bounds.Width;
        }
        else
        {
            PART_ArtifactCard.Transitions = _artifactResizeActive ? null : _artifactPanelTransitions;
            width = open ? canvas ? ClampArtifactCanvasWidth(_artifactCanvasWidth) : ArtifactPanelWidth : 0;
            // Apply the shrink limit immediately, even while Width is animating.
            PART_ArtifactCard.MaxWidth = canvas ? width : double.PositiveInfinity;
            PART_ArtifactCard.Width = width;
            PART_ArtifactGap.Width = open ? ArtifactPanelGap : 0;
        }

        // The canvas hosts a native web view, and a native window paints over
        // everything Avalonia draws in its rectangle — banners included. Keep
        // them to the left of it while it is open.
        PART_NotificationPresenter.Margin = canvas && !maximized
            ? new Thickness(0, 58, 16 + width + ArtifactPanelGap + 14, 0)
            : NotificationsMargin;
    }

    private static readonly Thickness NotificationsMargin = new(0, 58, 30, 0);

    /// <summary>How far the composer may reach into the transcript's 24px bottom
    /// padding: the gap between the last row and the composer that the layout has
    /// always had.</summary>
    private const double ComposerOverTranscriptPadding = 16;

    private double ClampArtifactCanvasWidth(double width)
    {
        var available = Math.Max(0, PART_BodyGrid.Bounds.Width
            - PART_SidebarCard.Bounds.Width - PART_SidebarGap.Bounds.Width - ArtifactPanelGap);
        var maximum = Math.Max(0, Math.Min(available * 0.62, available - MinChatWidth));
        return Math.Clamp(width, Math.Min(MinCanvasWidth, maximum), maximum);
    }

    private const double MinCanvasWidth = 360;
    private const double MinChatWidth = 420;

    private void OnArtifactLayoutSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_main.ArtifactPanelVisible && _main.ArtifactCanvasVisible) SyncArtifactPanel();
    }

    private void OnArtifactResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_main.ArtifactCanvasVisible || _main.ArtifactCanvasMaximized) return;
        _artifactResizeActive = true;
        _artifactResizeStart = e.GetPosition(PART_BodyGrid);
        _artifactResizeStartWidth = PART_ArtifactCard.Bounds.Width;
        // The width transition would chase the pointer 200 ms behind it.
        PART_ArtifactCard.Transitions = null;
        PART_ArtifactGap.Transitions = null;
        e.Pointer.Capture(PART_ArtifactGap);
        e.Handled = true;
    }

    private void OnArtifactResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_artifactResizeActive) return;
        var position = e.GetPosition(PART_BodyGrid);
        _artifactCanvasWidth = ClampArtifactCanvasWidth(_artifactResizeStartWidth + _artifactResizeStart.X - position.X);
        SyncArtifactPanel();
        e.Handled = true;
    }

    private void OnArtifactResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_artifactResizeActive) return;
        _artifactResizeActive = false;
        e.Pointer.Capture(null);
        PART_ArtifactCard.Transitions = _artifactPanelTransitions;
        PART_ArtifactGap.Transitions = _artifactPanelTransitions;
        e.Handled = true;
    }

    private async void OnCopyTextRequested(object? sender, string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
        }
        catch
        {
            // Clipboard held by another process; nothing useful to report.
        }
    }

    private int _modeRequest;

    private async void SwitchMode(string mode)
    {
        var request = ++_modeRequest;
        var target = mode == "chat" ? AppMode.Chat : AppMode.Work;
        if (_main.TrySwitchMode(target)) return;

        if (target == AppMode.Work)
        {
            var conversationId = _chat.ConversationId;
            var inWorkbench = _main.IsImageWorkbenchVisible;
            var ready = await _agentRuntime.EnsureUsableAsync();
            if (request != _modeRequest || conversationId != _chat.ConversationId
                || inWorkbench != _main.IsImageWorkbenchVisible)
                return;
            if (!ready)
            {
                _agentRuntime.OfferInstall(OpenProviderSettings);
                return;
            }
            if (!_main.TrySwitchMode(target)) OpenProviderSettings();
            return;
        }

        if (_settings.IsLoggedIn)
        {
            OpenSettings();
            return;
        }
        if (await OpenLoginAsync(this) && request == _modeRequest)
            _main.TrySwitchMode(target);
    }

    private void OnRemoteWorkPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteWorkSettingsViewModel.PhoneConnected))
            PART_TitleBar.SetRemotePhoneConnected(_remoteWork.PhoneConnected);
    }

    private async Task<bool> EnsureAgentRuntimeForSendAsync()
    {
        if (!await _agentRuntime.EnsureUsableAsync())
        {
            _agentRuntime.OfferInstall(OpenProviderSettings);
            return false;
        }
        if (_chat.ActiveProvider is not null && _chat.ActiveModel is not null) return true;

        // The runtime is there and still nothing to send with: no model service.
        // Plain OpenSettings would land on the account page, which is the one page
        // that cannot help a signed-out user here.
        OpenProviderSettings();
        return false;
    }

    /// <summary>The title bar's account button: sign in, or — once signed in —
    /// the MolaGPT 账号 page, which holds usage alongside the account's
    /// settings.</summary>
    private async void OnAccountSecurityClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        await OpenAccountSecurityAsync();

    private async Task OpenAccountSecurityAsync()
    {
        try { CommunityLinks.TryOpenBrowser(await _auth.CreateAccountSecurityLinkAsync()); }
        catch (Exception) { _notifications.Error("暂时无法打开账户安全", "请稍后重试。", "account-security"); }
    }

    private async Task OpenAccountAsync()
    {
        if (string.IsNullOrEmpty(_auth.CurrentJwt))
        {
            await OpenLoginAsync(this);
            return;
        }

        OpenSettings(window => window.OpenAccountPage());
    }

    private async Task<bool> OpenLoginAsync(Window owner, bool force = false)
    {
        if (!force && !string.IsNullOrEmpty(_auth.CurrentJwt)) return true;

        var login = _createLoginWindow();
        var success = await login.ShowDialog<bool>(owner);
        if (success) CompleteAccountLogin();
        return success;
    }

    internal void CompleteAccountLogin()
    {
        if (!_auth.ReadSession().IsLoggedIn) return;

        _ = _main.RefreshUsageAsync(force: true);
        _ = SyncAfterLoginAsync();
    }

    private async Task OpenMembershipAsync(Window owner, string? productKey = null)
    {
        if (_openingMembership) return;
        _openingMembership = true;
        try
        {
            if (!await OpenLoginAsync(owner)) return;
            var url = await _membership.CreatePortalAsync(productKey);
            if (!CommunityLinks.TryOpenBrowser(url.AbsoluteUri))
                throw new InvalidOperationException("无法打开浏览器，请检查默认浏览器设置。");
        }
        catch (OperationCanceledException) when (string.IsNullOrEmpty(_auth.CurrentJwt)) { }
        catch (Exception ex)
        {
            _notifications.Error("无法打开支持页面", ex.Message, key: "membership");
        }
        finally
        {
            _openingMembership = false;
        }
    }

    private async Task SyncAfterLoginAsync()
    {
        try
        {
            await _cloudSync.RequestForegroundSyncAsync();
            await _conversations.ReloadAsync();
        }
        catch
        {
            // CloudSyncService publishes the user-facing failure state.
        }
    }

    private void RefreshAccountState() =>
        PART_TitleBar.SetAccountState(_settings.IsLoggedIn, _settings.MolaGptUsername);

    private void ToggleTheme()
    {
        _settings.ThemeMode = Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            ? ThemeMode.Light
            : ThemeMode.Dark;
    }

    /// <summary>
    /// Opens settings, reusing the window if it is already up. A second copy
    /// would show the same view model through two sets of controls, and the one
    /// the user is not looking at would be the one holding stale text.
    /// </summary>
    internal void OpenSettings() => OpenSettings(null);

    private void OpenPersonaSettings(bool startNew) =>
        OpenSettings(window => window.OpenPersonaPage(startNew, _chat.ActivePersonaId));

    private void OpenApprovalSettings() => OpenSettings(window => window.OpenApprovalPage());

    internal void OpenProviderSettings() => OpenSettings(window => window.OpenProvidersPage());

    private void OpenSearchSettings() => OpenSettings(window => window.OpenSearchPage());

    private void OpenProviderToolsSettings(string? providerId) =>
        OpenSettings(window => window.OpenProviderTools(providerId));

    private async Task OpenSystemPromptAsync()
    {
        if (!_chat.CanEditHistory) return;
        using var http = _httpClientFactory.CreateClient(HttpClientNames.Byok);
        var client = new MolaGPT.Core.Chat.OneShotCompletionClient(http);
        await new ConversationRoleWindow().ShowForAsync(_chat, client, _notifications, this);
        PART_Header.RefreshSecondaryUi();
    }

    private void OpenImageWorkbench(string? conversationId)
    {
        if (_imageWorkbench is { } existing
            && string.Equals(existing.ConversationId, conversationId, StringComparison.Ordinal))
        {
            _main.IsImageWorkbenchVisible = true;
            existing.SetPresented(true);
            PART_Header.RefreshSecondaryUi();
            return;
        }

        _imageWorkbench?.SetPresented(false);
        var workbench = new ImageGenerationWorkbenchView(_settings, _imageTasks, conversationId, _notifications);
        workbench.CloseRequested += (_, _) =>
        {
            _main.CloseImageWorkbench();
            PART_Header.RefreshSecondaryUi();
            _ = _conversations.ReloadAsync();
        };
        // Straight to 模型服务: the gear is pressed because the workbench has no
        // image service, and dropping the user on whichever page settings was
        // last left open leaves them to find it themselves.
        // Through the sidebar rather than straight to OpenImageWorkbench: the
        // list has to end up highlighting the task the workbench switched to,
        // and SelectById is the one path that does both.
        workbench.OpenTaskRequested += (_, id) => _conversations.SelectById(id);
        workbench.OpenSettingsRequested += (_, _) => OpenProviderSettings();
        _imageWorkbench = workbench;
        PART_ImageWorkbenchHost.Content = workbench;
        _main.IsImageWorkbenchVisible = true;
        workbench.SetPresented(true);
        PART_Header.RefreshSecondaryUi();
    }

    internal void OpenAgentSettings() => OpenSettings(window => window.OpenAgentPage());

    internal void OpenBrowserSettings() => OpenSettings(window => window.OpenBrowserPage());

    internal void OpenSelectionSettings()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        OpenSettings(window => window.OpenSelectionPage());
    }

    private void OpenSettings(Action<SettingsWindow>? navigate)
    {
        if (_settingsWindow is { } existing)
        {
            navigate?.Invoke(existing);
            existing.Activate();
            return;
        }

        var window = _createSettingsWindow();
        window.AccountRequested += async (_, _) => await OpenLoginAsync(window);
        window.SupportRequested += async (_, _) => await OpenMembershipAsync(window);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        navigate?.Invoke(window);
        window.Show(this);
    }

    private void OpenAbout()
    {
        if (_aboutWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        _aboutWindow = new AboutWindow(_updateCheck, _settings);
        _aboutWindow.SupportRequested += (_, _) => OpenSupport(_aboutWindow);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show(this);
    }

    internal void OpenSupport(Window? owner = null)
    {
        if (_supportWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        _supportWindow = new SupportWindow(_sponsors, _membership, _settings, OpenMembershipAsync);
        _supportWindow.Closed += (_, _) => _supportWindow = null;
        _supportWindow.Show(owner is { IsVisible: true } ? owner : this);
    }

    private void NewConversation()
    {
        _main.NewConversationCommand.Execute(null);
        PART_Composer.FocusInput();
    }

    private void OnArtifactClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: ArtifactItemViewModel artifact }
            && _main.OpenArtifactInCanvasCommand.CanExecute(artifact))
        {
            _main.OpenArtifactInCanvasCommand.Execute(artifact);
        }
    }

    private void OnArtifactMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var artifact = menu.DataContext as ArtifactItemViewModel
                       ?? menu.PlacementTarget?.DataContext as ArtifactItemViewModel;
        var actions = menu.Items.OfType<MenuItem>().ToArray();
        if (actions.Length != 2) return;
        actions[0].Tag = artifact;
        actions[1].Tag = artifact;
        actions[1].IsEnabled = artifact?.CanReveal == true;
    }

    private void OnArtifactMenuOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ArtifactItemViewModel artifact }
            && _main.OpenArtifactInCanvasCommand.CanExecute(artifact))
            _main.OpenArtifactInCanvasCommand.Execute(artifact);
    }

    private void OnArtifactMenuOpenFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ArtifactItemViewModel artifact }
            && _main.RevealArtifactCommand.CanExecute(artifact))
            _main.RevealArtifactCommand.Execute(artifact);
    }

    private bool _subagentFollowBottom = true;
    private bool _subagentScrollQueued;

    private void OnSubagentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { Tag: SubagentViewModel agent }) return;
        _main.OpenSubagent(agent);
        _subagentFollowBottom = true;
        QueueSubagentScrollToEnd();
    }

    private void OnSubagentScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y < -0.5)
            _subagentFollowBottom = false;
        if (PART_SubagentScroll.Offset.Y >= PART_SubagentScroll.Extent.Height
            - PART_SubagentScroll.Viewport.Height - 24)
            _subagentFollowBottom = true;
        if (_subagentFollowBottom && e.ExtentDelta.Y > 0.5)
            QueueSubagentScrollToEnd();
    }

    private void QueueSubagentScrollToEnd()
    {
        if (_subagentScrollQueued) return;
        _subagentScrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _subagentScrollQueued = false;
            if (_subagentFollowBottom) PART_SubagentScroll.ScrollToEnd();
        });
    }
}
