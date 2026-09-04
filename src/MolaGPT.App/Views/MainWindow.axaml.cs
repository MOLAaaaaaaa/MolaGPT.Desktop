using System.ComponentModel;
using System.Net.Http;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MolaGPT.App.Infrastructure;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Agents;

namespace MolaGPT.App.Views;

public partial class MainWindow : MolaWindow
{
    private readonly MainViewModel _main;
    private readonly ChatViewModel _chat;
    private readonly ConversationListViewModel _conversations;
    private readonly ComposerViewModel _composer;
    private readonly SettingsViewModel _settings;
    private readonly UpdateCheckService _updateCheck;
    private readonly ProviderRegistry _providers;
    private readonly MolaGptAuthService _auth;
    private readonly MolaGptProxyProvider _proxy;
    private readonly MolaGptLocalToolsRegistrar _localToolsRegistrar;
    private readonly CloudSyncService _cloudSync;
    private readonly AgentBridgeStatusViewModel _agentStatus;
    private readonly McpHttpClient _mcpHttpClient;
    private readonly ImageGenerationTool _imageGenerationTool;
    private readonly AttachmentStore _attachmentStore;
    private readonly ConversationRepository _conversationRepository;
    private readonly MessageRepository _messageRepository;
    private readonly PythonRuntimeManager _pythonRuntime;
    private readonly PiSidecarRuntimeManager _piSidecar;
    private readonly PiWorkSidecarLocator _piSidecarLocator;
    private readonly NotificationCenter _notifications;
    private readonly SkillsViewModel _skills;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChatToolHost _toolHost;
    private readonly PiByokProviderFactory _piByokProviderFactory;

    private bool _sidebarCollapsed;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private ImageGenerationWorkbenchView? _imageWorkbench;
    private Task<bool>? _agentRuntimeSetupTask;
    private bool _agentRuntimeActivated;

    private const string AgentRuntimeNotificationKey = "pi-sidecar";

    /// <summary>Shared so that flipping sides twice in a row replaces the banner
    /// instead of stacking two.</summary>
    private const string ModeSwitchNotificationKey = "mode-switch";

    /// <summary>The in-app banner stack. <see cref="NotificationRouter"/> drives it.</summary>
    public NotificationHost Notifications => PART_Notifications;

    public MainWindow(
        MainViewModel main,
        ChatViewModel chat,
        ConversationListViewModel conversations,
        ComposerViewModel composer,
        ProviderRegistry providers,
        SettingsViewModel settings,
        UpdateCheckService updateCheck,
        MolaGptAuthService auth,
        MolaGptProxyProvider proxy,
        MolaGptLocalToolsRegistrar localToolsRegistrar,
        CloudSyncService cloudSync,
        AgentBridgeStatusViewModel agentStatus,
        McpHttpClient mcpHttpClient,
        ImageGenerationTool imageGenerationTool,
        AttachmentStore attachmentStore,
        ConversationRepository conversationRepository,
        MessageRepository messageRepository,
        PythonRuntimeManager pythonRuntime,
        PiSidecarRuntimeManager piSidecar,
        PiWorkSidecarLocator piSidecarLocator,
        NotificationCenter notifications,
        SkillsViewModel skills,
        IHttpClientFactory httpClientFactory,
        IChatToolHost toolHost,
        PiByokProviderFactory piByokProviderFactory)
    {
        _main = main;
        _chat = chat;
        _conversations = conversations;
        _composer = composer;
        _settings = settings;
        _updateCheck = updateCheck;
        _providers = providers;
        _auth = auth;
        _proxy = proxy;
        _localToolsRegistrar = localToolsRegistrar;
        _cloudSync = cloudSync;
        _agentStatus = agentStatus;
        _mcpHttpClient = mcpHttpClient;
        _imageGenerationTool = imageGenerationTool;
        _attachmentStore = attachmentStore;
        _conversationRepository = conversationRepository;
        _messageRepository = messageRepository;
        _pythonRuntime = pythonRuntime;
        _piSidecar = piSidecar;
        _piSidecarLocator = piSidecarLocator;
        _notifications = notifications;
        _skills = skills;
        _httpClientFactory = httpClientFactory;
        _toolHost = toolHost;
        _piByokProviderFactory = piByokProviderFactory;
        _composer.EnsureAgentRuntimeAsync = EnsureAgentRuntimeForSendAsync;

        InitializeComponent();
        DataContext = _main;
        ApplyFontScale(_settings.FontScale);
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        PART_Header.AttachProviders(providers);
        PART_Header.AttachMain(_main);
        PART_TitleBar.SettingsRequested += (_, _) => OpenSettings();
        PART_TitleBar.AboutRequested += (_, _) => OpenAbout();
        PART_TitleBar.ThemeToggleRequested += (_, _) => ToggleTheme();
        PART_TitleBar.LoginRequested += async (_, _) => await OpenAccountAsync();
        PART_TitleBar.AgentStatusRequested += (_, _) => OpenAgentSettings();

        PART_Sidebar.DataContext = _conversations;
        PART_Transcript.DataContext = _chat;
        PART_Transcript.AttachAttachmentStore(_attachmentStore);
        PART_Composer.DataContext = _composer;
        PART_Composer.PersonaSettingsRequested += (_, startNew) => OpenPersonaSettings(startNew);
        _main.SystemPromptRequested = () => _ = OpenSystemPromptAsync();
        _main.ImageWorkbenchRequested = conversationId => OpenImageWorkbench(conversationId);
        _main.WorkSetupRequested = () =>
        {
            if (!_settings.PythonToolEnabled
                || string.IsNullOrWhiteSpace(_settings.PythonToolExecutablePath))
            {
                OpenSandboxSettings();
            }
        };

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
        PART_ArtifactCard.Transitions = slide;
        PART_ArtifactGap.Transitions = slide;
        _main.PropertyChanged += OnMainPropertyChanged;
        SyncArtifactPanel();

        PART_Sidebar.CollapseRequested += (_, _) => SetSidebarCollapsed(true);
        PART_Header.ExpandSidebarRequested += (_, _) => SetSidebarCollapsed(false);
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
            _conversations.ClearSelection();
            _chat.StartDraftConversation();
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

        // The only recoverable error the view model raises is "switch model",
        // and the picker lives in the header — so the banner's button opens it
        // rather than duplicating the list.
        PART_Transcript.ErrorActionRequested += (_, _) => PART_Header.OpenModelSelector();

        _chat.PropertyChanged += OnChatPropertyChanged;
        _auth.LoggedOut += OnLoggedOut;
        SyncChrome();
        RefreshAccountState();

        PART_TitleBar.CloseRequested += (_, _) => Close();

        Opened += (_, _) => PART_Composer.FocusInput();
        Closed += (_, _) =>
        {
            _auth.LoggedOut -= OnLoggedOut;
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
        };
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FontScale))
            ApplyFontScale(_settings.FontScale);
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

    /// <summary>Panel width, matching the WPF build's ArtifactPanelWidth.</summary>
    private const double ArtifactPanelWidth = 300;
    private const double ArtifactPanelGap = 16;

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ArtifactPanelVisible)
            or nameof(MainViewModel.IsArtifactPanelAvailable))
        {
            SyncArtifactPanel();
        }

        // The mode segments read this flag, and entering or leaving the
        // workbench does not touch ChatViewModel — so without this the capsule
        // would only catch up on the next unrelated chat change.
        if (e.PropertyName is nameof(MainViewModel.IsImageWorkbenchVisible))
        {
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
        PART_ArtifactCard.Width = open ? ArtifactPanelWidth : 0;
        PART_ArtifactGap.Width = open ? ArtifactPanelGap : 0;
    }

    private async void SwitchMode(string mode)
    {
        var target = string.Equals(mode, "chat", StringComparison.Ordinal)
            ? AppMode.Chat
            : AppMode.Work;
        var fromMode = _chat.CurrentMode;

        if (target == AppMode.Work && !await EnsureAgentRuntimeAsync()) return;

        // Clicking "Work" while already in an agent mode is a no-op, matching
        // MainViewModel.SwitchMode.
        if (target == AppMode.Work && _chat.CurrentMode.IsLocalAgent()) return;

        if (!_chat.SwitchToMode(target, out var needsLogin))
        {
            if (!needsLogin)
            {
                OpenSettings();
                return;
            }

            if (!await OpenLoginAsync(this)) return;
            if (!_chat.SwitchToMode(target, out _)) return;
        }

        if (fromMode.CrossesChatBoundary(target))
        {
            _conversations.ClearSelection();
            _chat.StartDraftConversation();
        }
        _imageWorkbench?.NotifyHiddenWhileGenerating();
        _main.IsImageWorkbenchVisible = false;
        SyncChrome();
        if (target == AppMode.Work) _main.WorkSetupRequested?.Invoke();
    }

    private async Task<bool> EnsureAgentRuntimeAsync()
    {
        if (_piSidecarLocator.TryResolve() is not null)
        {
            // "No agent provider registered yet" is the trigger, but it is also the
            // *outcome* when the account has no Work models and no BYOK row is
            // configured — so without this latch every send would re-register every
            // row and re-fetch the account's model list. Cleared by
            // DeactivateAgentRuntime, which is what settings calls after a change
            // that could produce a different answer.
            if (!_agentRuntimeActivated
                && !_providers.Providers.Any(provider => provider.Kind != ProviderKind.MolaGptProxy))
            {
                _agentRuntimeActivated = true;
                await ActivateAgentRuntimeAsync();
            }
            return true;
        }
        if (_agentRuntimeSetupTask is { } running) return await running;

        var setup = DownloadAgentRuntimeAsync();
        _agentRuntimeSetupTask = setup;
        try
        {
            return await setup;
        }
        finally
        {
            if (ReferenceEquals(_agentRuntimeSetupTask, setup))
                _agentRuntimeSetupTask = null;
        }
    }

    private async Task<bool> EnsureAgentRuntimeForSendAsync()
    {
        if (!await EnsureAgentRuntimeAsync()) return false;
        if (_chat.ActiveProvider is not null && _chat.ActiveModel is not null) return true;

        OpenSettings();
        return false;
    }

    private async Task<bool> DownloadAgentRuntimeAsync()
    {
        var updating = _piSidecar.GetInstalled() is not null;
        if (!await Confirm.AskAsync(
                this,
                updating ? "更新 Agent 运行环境？" : "下载 Agent 运行环境？",
                "使用 Work 与 BYOK 前需要此环境。",
                updating ? "更新" : "下载"))
        {
            return false;
        }

        _notifications.Progress(AgentRuntimeNotificationKey, "正在准备 Agent 运行环境");
        try
        {
            var progress = new Progress<SandboxProgress>(item =>
                _notifications.Progress(
                    AgentRuntimeNotificationKey,
                    "正在准备 Agent 运行环境",
                    item.Message,
                    item.Fraction > 0 ? item.Fraction : null));
            var installed = await _piSidecar.DownloadAndInstallAsync(progress);
            await ActivateAgentRuntimeAsync();
            _notifications.Success("Agent 运行环境已就绪", installed.Version, AgentRuntimeNotificationKey);
            return _piSidecarLocator.TryResolve() is not null;
        }
        catch (Exception ex)
        {
            _notifications.Error("Agent 运行环境下载失败", ex.Message, AgentRuntimeNotificationKey);
            return false;
        }
    }

    private async Task ActivateAgentRuntimeAsync()
    {
        _agentRuntimeActivated = true;
        foreach (var entry in _settings.Providers)
        {
            try
            {
                ProviderRestorer.ApplyEntry(
                    entry,
                    _providers,
                    () => _httpClientFactory.CreateClient(HttpClientNames.Byok),
                    _toolHost,
                    _piByokProviderFactory);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("pi-byok", $"注册 {entry.Name} 失败：{ex.Message}");
            }
        }

        try { await _localToolsRegistrar.RefreshAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("pi-work", "刷新 Work 失败：" + ex.Message); }
    }

    private void DeactivateAgentRuntime()
    {
        _agentRuntimeActivated = false;
        _localToolsRegistrar.Deactivate();
        foreach (var entry in _settings.Providers)
            ProviderRestorer.RemoveEntry(entry.Id, _providers, _piByokProviderFactory);
    }

    private async Task OpenAccountAsync()
    {
        if (string.IsNullOrEmpty(_auth.CurrentJwt))
        {
            await OpenLoginAsync(this);
            return;
        }

        var account = new AccountWindow(_auth, _proxy);
        await account.ShowDialog<bool>(this);
        RefreshAccountState();
    }

    private async Task<bool> OpenLoginAsync(Window owner)
    {
        if (!string.IsNullOrEmpty(_auth.CurrentJwt)) return true;

        var login = new LoginWindow(_auth, _proxy, _providers, _localToolsRegistrar);
        var success = await login.ShowDialog<bool>(owner);
        if (success) CompleteAccountLogin();
        return success;
    }

    internal void CompleteAccountLogin()
    {
        RefreshAccountState();
        if (!_settings.IsLoggedIn) return;

        _ = _main.RefreshQuotaAsync();
        _ = SyncAfterLoginAsync();
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

    private void OnLoggedOut(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            RefreshAccountState();
        else
            Dispatcher.UIThread.Post(RefreshAccountState);
    }

    private void RefreshAccountState()
    {
        var loggedIn = !string.IsNullOrEmpty(_auth.CurrentJwt);
        _settings.IsLoggedIn = loggedIn;
        _settings.MolaGptUsername = loggedIn ? _auth.CurrentUsername : null;
        PART_TitleBar.SetAccountState(loggedIn, _auth.CurrentUsername);
        _settingsWindow?.RefreshAccountUi();
    }

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
    internal void OpenSettings()
    {
        OpenSettings(openAgent: false);
    }

    private void OpenPersonaSettings(bool startNew)
    {
        OpenSettings();
        _settingsWindow?.OpenPersonaPage(startNew);
    }

    private void OpenSandboxSettings()
    {
        OpenSettings();
        _settingsWindow?.OpenSandboxPage();
    }

    private async Task OpenSystemPromptAsync()
    {
        var window = new SystemPromptWindow();
        await window.ShowForAsync(_chat, this);
        PART_Header.RefreshSecondaryUi();
    }

    private void OpenImageWorkbench(string? conversationId)
    {
        if (_imageWorkbench is { } existing
            && string.Equals(existing.ConversationId, conversationId, StringComparison.Ordinal))
        {
            _main.IsImageWorkbenchVisible = true;
            PART_Header.RefreshSecondaryUi();
            return;
        }

        // Replacing a workbench that is mid-generation orphans it: the run still
        // finishes and still writes to the gallery, but its view is gone. Say so
        // before it disappears — the same banner the close button raises, on the
        // same key, so it collapses into one entry rather than stacking. The new
        // sidebar button makes this one click away, where the old entry was three.
        _imageWorkbench?.NotifyHiddenWhileGenerating();

        var workbench = new ImageGenerationWorkbenchView(
            _settings,
            _imageGenerationTool,
            _attachmentStore,
            _conversationRepository,
            _messageRepository,
            conversationId,
            (title, modelId) => _conversations.CreateImageWorkbenchConversation(title, modelId),
            (id, generating) => _conversations.SetGenerating(id, generating),
            _notifications);
        workbench.CloseRequested += (_, _) =>
        {
            workbench.NotifyHiddenWhileGenerating();
            _main.CloseImageWorkbench();
            PART_Header.RefreshSecondaryUi();
            _ = _conversations.ReloadAsync();
        };
        workbench.OpenSettingsRequested += (_, _) => OpenSettings();
        _imageWorkbench = workbench;
        PART_ImageWorkbenchHost.Content = workbench;
        _main.IsImageWorkbenchVisible = true;
        PART_Header.RefreshSecondaryUi();
    }

    private void OpenAgentSettings()
    {
        OpenSettings(openAgent: true);
    }

    private void OpenSettings(bool openAgent)
    {
        if (_settingsWindow is { } existing)
        {
            if (openAgent) existing.OpenAgentPage();
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(
            _settings, _auth, _cloudSync, _conversations, _agentStatus, _main.Personas, _mcpHttpClient,
            _imageGenerationTool, _pythonRuntime, _piSidecar, _notifications, _skills,
            () => _httpClientFactory.CreateClient(HttpClientNames.Byok), _providers, _toolHost, _piByokProviderFactory,
            ActivateAgentRuntimeAsync, DeactivateAgentRuntime);
        window.AccountRequested += async (_, _) =>
        {
            if (await OpenLoginAsync(window)) window.RefreshAccountUi();
        };
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        if (openAgent) window.OpenAgentPage();
        window.Show(this);
    }

    private void OpenAbout()
    {
        if (_aboutWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        _aboutWindow = new AboutWindow(_updateCheck);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show(this);
    }

    private void NewConversation()
    {
        _main.NewConversationCommand.Execute(null);
        PART_Composer.FocusInput();
    }

    private void OnArtifactClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: ArtifactItemViewModel artifact }
            && _main.RevealArtifactCommand.CanExecute(artifact))
        {
            _main.RevealArtifactCommand.Execute(artifact);
        }
    }
}
