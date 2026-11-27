using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MolaGPT.App.Infrastructure;
using MolaGPT.App.Remote;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Chat.Tools.Browser;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Desktop.Services;
using MolaGPT.ViewModels;
using MolaGPT.ViewModels.Agents;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.App.Views;

public partial class SettingsWindow : MolaContentWindow
{
    private static readonly string[] ProviderTypes = ["openai-compat", "anthropic", "gemini", "openai-response"];
    private static readonly double[] FontScaleLevels = [0.8, 1.0, 1.2, 1.4];

    private readonly SettingsViewModel _settings;
    private readonly MolaGptAuthService? _auth;
    private readonly CloudSyncService? _cloudSync;
    private readonly ConversationListViewModel? _conversations;
    private readonly AgentBridgeStatusViewModel? _agentStatus;
    private readonly PersonaListViewModel _personas;
    private readonly PersonalizationViewModel? _personalization;
    private readonly McpClientManager? _mcp;
    private readonly ImageGenerationTool? _imageGenerationTool;
    private readonly PythonRuntimeManager? _pythonRuntime;
    private readonly PythonRuntimeSetup? _pythonSetup;
    private readonly PiSidecarRuntimeManager? _piSidecar;
    private readonly NotificationCenter? _notifications;

    private const string PythonRuntimeNotificationKey = "python-runtime";
    private readonly SkillsViewModel _skills;
    private readonly BrowserActivityLog? _browserActivity;
    private readonly Func<HttpClient> _byokHttpFactory;
    private readonly ProviderConnection _providerConnection;
    private readonly ProviderRegistry? _providerRegistry;
    private readonly ProviderRestorer _providerRestorer;
    private readonly MolaAccountSearchClient _accountSearch;
    private readonly PiByokProviderFactory? _piByokProviderFactory;
    private readonly AgentRuntimeSetup _agentRuntime;
    private readonly BrowserBridgeStatusViewModel _browserBridge;
    /// <summary>Nav item → page, read from each item's <c>Tag</c> (the page's
    /// name). Group headings carry no tag and so never resolve to a page.</summary>
    private readonly Dictionary<ListBoxItem, StackPanel> _navPages = [];
    private readonly bool _accountEntryWasNew;
    private Rect _accountEntryViewport;
    private readonly ObservableCollection<ModelRow> _providerModels = [];
    private readonly ObservableCollection<HeaderRow> _providerHeaders = [];
    /// <summary>Built on first use and kept for the window's life, so editing
    /// several providers in one sitting parses the 4.7 MB catalogue once.</summary>
    private ModelsDevCatalog? _modelsDevCatalog;
    /// <summary>The last lookup and the providers it covered, held so the per-model
    /// picker can be opened without re-matching everything against the catalogue.</summary>
    private (PricingMatch Match, HashSet<string> ProviderIds)? _pendingPricing;
    private readonly List<DetectedModelRow> _detectedModels = [];
    private ProviderEntry? _editingProvider;
    private PersonaItemViewModel? _editingPersona;
    private readonly PersonaItemViewModel _emptyPersona = new();
    private McpServerEntry? _editingMcpServer;
    private bool _editingPersonaIsDraft;
    private bool _loadingPersonaForm;
    private bool _loadingProviderForm;
    private bool _applyingProviderPreset;
    private bool _editingProviderBuiltinSearch;
    private bool _updatingSearchControls;
    private string _editingProviderPurpose = "chat";

    /// <summary>A completed scan doubles as the cache. Opening the window fires
    /// <c>Opened</c> and <c>Activated</c> back to back and every refocus
    /// refreshes again, but the 15k-file walk only has to happen once per open.</summary>
    private Task<PythonRuntimeStorageUsage>? _storageScan;

    /// <summary>Bumped by every runtime refresh and by every operation that
    /// writes its own text into the runtime rows, so a scan that lands late
    /// cannot overwrite whatever replaced it.</summary>
    private int _runtimeStatusGeneration;

    /// <summary>Set while a download / reset / probe owns the runtime rows.
    /// Refreshes now paint synchronously, so without this a stray window
    /// activation would wipe out a download's progress text instantly.</summary>
    private bool _sandboxOperationRunning;

    private static readonly ProviderPresetRow[] ProviderPresets =
    [
        new("openrouter", "OpenRouter", "openai-compat", "https://openrouter.ai/api/", "v1/models"),
        // DeepSeek 的对话走 Responses 协议：服务端搜索 (deepseek_web_search) 只在
        // 根路径 /responses 上生效，/v1/responses 这个别名会把工具悄悄丢掉。
        new("deepseek", "DeepSeek", "openai-response", "https://api.deepseek.com/", "v1/models",
            ApiPath: "responses"),
        new("bailian", "阿里云百炼", "openai-compat", "https://dashscope.aliyuncs.com/compatible-mode/", "v1/models"),
        new("bailian-response", "阿里云百炼 (Responses API)", "openai-response", "https://dashscope.aliyuncs.com/compatible-mode/",
            "v1/models", ApiPath: "v1/responses"),
        new("moonshot", "Moonshot (Kimi)", "openai-compat", "https://api.moonshot.cn/", "v1/models"),
        new("openai", "OpenAI", "openai-compat", OpenAiBaseUrl, "v1/models"),
        new("openai-response", "OpenAI (Responses API)", "openai-response", OpenAiBaseUrl,
            "v1/models", ApiPath: "v1/responses"),
        new("anthropic", "Anthropic (Claude)", "anthropic", AnthropicBaseUrl, "v1/models"),
        new("gemini-compat", "Google Gemini（OpenAI 兼容）", "openai-compat", GeminiCompatBaseUrl, "models",
            ApiPath: "chat/completions"),
        new("gemini", "Google Gemini（原生）", "gemini", GeminiNativeBaseUrl, "models",
            ApiPath: "models/{model}:streamGenerateContent"),
        new("custom-openai", "自定义（OpenAI 兼容）", "openai-compat", OpenAiBaseUrl, "v1/models"),
        new("openrouter-images", "OpenRouter 图像", "openai-compat", "https://openrouter.ai/api/", "v1/images/models",
            "image", "v1/images", ImageFormat: "openai-images", ImageChatPath: "v1/chat/completions"),
        new("openai-images", "OpenAI 图像", "openai-compat", OpenAiBaseUrl, "v1/models",
            "image", "v1/images/generations", "v1/images/edits", "openai-images"),
        new("volcengine-images", "火山方舟图像", "openai-compat", "https://ark.cn-beijing.volces.com/api/v3/", "models",
            "image", "images/generations", ImageFormat: "openai-images", ImageChatPath: "chat/completions"),
        new("siliconflow-images", "SiliconFlow 图像", "openai-compat", "https://api.siliconflow.cn/", "v1/models",
            "image", "v1/images/generations", ImageFormat: "openai-images"),
        new("xai-images", "xAI 图像", "openai-compat", "https://api.x.ai/", "v1/models",
            "image", "v1/images/generations", "v1/images/edits", "openai-images"),
        new("gemini-images", "Google Gemini 图像", "gemini", GeminiNativeBaseUrl, "models",
            "image", ImageFormat: "openai-chat-image", ImageChatPath: "models/{model}:generateContent")
    ];

    // Endpoint defaults for the provider presets and the connection test. These
    // used to live on the direct provider classes; with the agent runtime as the
    // only engine, the settings page is the last thing that needs them.
    private const string OpenAiBaseUrl = "https://api.openai.com/";
    private const string AnthropicBaseUrl = "https://api.anthropic.com/";

    /// <summary>Google's two API roots. The compatible API uses OpenAI request and
    /// response shapes; the native API carries Gemini-specific tool-call fields.</summary>
    private const string GeminiCompatBaseUrl = GeminiModelPolicy.OpenAiBaseUrl;
    private const string GeminiNativeBaseUrl = GeminiModelPolicy.NativeBaseUrl;

    private readonly RemoteWorkSettingsViewModel _remoteWork;

    internal SettingsWindow(
        SettingsViewModel settings,
        MolaGptAuthService auth,
        CloudSyncService cloudSync,
        ConversationListViewModel conversations,
        AgentBridgeStatusViewModel agentStatus,
        PersonaListViewModel personas,
        McpClientManager mcp,
        ImageGenerationTool imageGenerationTool,
        PythonRuntimeManager pythonRuntime,
        PythonRuntimeSetup pythonSetup,
        PiSidecarRuntimeManager piSidecar,
        AgentRuntimeSetup agentRuntime,
        NotificationCenter notifications,
        SkillsViewModel skills,
        BrowserActivityLog browserActivity,
        WebBridgeClient webBridge,
        Func<HttpClient> byokHttpFactory,
        ProviderConnection providerConnection,
        ProviderRegistry providerRegistry,
        ProviderRestorer providerRestorer,
        PiByokProviderFactory piByokProviderFactory,
        PersonalizationViewModel personalization,
        MemoryPageViewModel memoryPage,
        PersonalDataService personalData,
        MolaAccountSearchClient accountSearch,
        RemoteWorkSettingsViewModel remoteWork,
        StorageService storage)
    {
        _accountSearch = accountSearch;
        _settings = settings;
        // Re-read settings and grants once, before any deferred page uses them.
        _settings.Reload();
        _accountEntryWasNew = !settings.AccountEntrySeen;
        _auth = auth;
        _cloudSync = cloudSync;
        _conversations = conversations;
        _agentStatus = agentStatus;
        _personas = personas;
        _mcp = mcp;
        _imageGenerationTool = imageGenerationTool;
        _pythonRuntime = pythonRuntime;
        _pythonSetup = pythonSetup;
        _piSidecar = piSidecar;
        _agentRuntime = agentRuntime;
        _notifications = notifications;
        _skills = skills;
        _browserActivity = browserActivity;
        _byokHttpFactory = byokHttpFactory;
        _providerConnection = providerConnection;
        _providerRegistry = providerRegistry;
        _providerRestorer = providerRestorer;
        _piByokProviderFactory = piByokProviderFactory;
        _personalization = personalization;
        _personalData = personalData;
        _remoteWork = remoteWork;
        _memoryPage = memoryPage;
        _storage = storage;
        _browserBridge = new BrowserBridgeStatusViewModel(webBridge);

        InitializeComponent();
        InitializeSearch();

        foreach (var item in PART_Nav.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is string name && this.FindControl<StackPanel>(name) is { } page)
                _navPages[item] = page;
        }

        PAGE_Agent.DataContext = _agentStatus;
        PAGE_Network.DataContext = _settings.NetworkProxy;
        PAGE_Personas.DataContext = _personas;

        DataContext = _settings;

        PART_Nav.SelectedItem = _settings.ShowAccountFeatures ? PART_AccountNav : PART_AppearanceNav;
        PART_Nav.SelectionChanged += (_, _) => ShowSelectedPage();
        RefreshAccountUi();

        Closing += (_, _) =>
        {
            PersistEditingPersona();
            // Edits wait a moment before they apply; closing must not drop the last one.
            _settings.NetworkProxy?.Flush();
        };

        Opened += (_, _) =>
        {
            // A contextual route can select and build its page before Show.
            if (_deferredPageNames.Count == 0) ShowSelectedPage();
        };
        Activated += (_, _) =>
        {
            MarkAccountEntrySeen();
            _ = RefreshRuntimeStatusAsync();
        };

        PropertyChangedEventHandler settingsChanged = (_, args) =>
        {
            if (args.PropertyName is nameof(SettingsViewModel.PythonToolEnabled)
                or nameof(SettingsViewModel.FileToolsEnabled))
            {
                if (HasPageContent(PAGE_Skills)) PART_SkillsUnreachable.IsVisible = !CanReachSkills();
            }
            else if (args.PropertyName == nameof(SettingsViewModel.ShowStatusDots) && HasPageContent(PAGE_Appearance))
            {
                PART_AccountEntryNewBadge.IsVisible = _accountEntryWasNew && _settings.ShowStatusDots;
            }
            else if (args.PropertyName == nameof(SettingsViewModel.PythonToolExecutablePath))
                RefreshPythonBrowseButton();
            else if (args.PropertyName == nameof(SettingsViewModel.AccountSessionGeneration))
                RefreshAccountUi();
            else if (args.PropertyName == nameof(SettingsViewModel.WebSearchProvider))
                UpdateSearchStatusHint();
        };
        EventHandler searchChanged = (_, _) => UpdateSearchStatusHint();
        EventHandler providersChanged = (_, _) =>
        {
            RefreshProviders();
            RefreshSpecializedModelChoices();
        };
        _settings.PropertyChanged += settingsChanged;
        _settings.SearchConfigurationChanged += searchChanged;
        _settings.ProviderConfigurationsChanged += providersChanged;
        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= settingsChanged;
            _settings.SearchConfigurationChanged -= searchChanged;
            _settings.ProviderConfigurationsChanged -= providersChanged;
        };
        KeyDown += OnSettingsKeyDown;
        AddHandler(Button.ClickEvent, OnSectionHeaderClicked, RoutingStrategies.Bubble);
    }

    /// <summary>Every fold in this window sits near the bottom of a scrolling
    /// page, so opening one usually reveals fields below the fold: the chevron
    /// flips and, from where the user is looking, nothing else happens.
    ///
    /// The header's click is the signal, not <c>Expander.Expanded</c>: this app
    /// replaces the Expander template (Theme/Controls.axaml drives IsExpanded
    /// from PART_toggle), and Avalonia's own expand events never fire through
    /// it. Watching IsExpanded instead would catch the folds the code opens
    /// while loading a persona, and scrolling the page out from under someone
    /// who clicked nothing is worse than not scrolling at all.</summary>
    private void OnSectionHeaderClicked(object? sender, RoutedEventArgs e)
    {
        // ToggleButton flips IsChecked before it raises Click, and the template
        // binds that to IsExpanded, so this already reads the new state.
        if (e.Source is ToggleButton { TemplatedParent: Expander { IsExpanded: true } expander })
            _ = FollowRevealAsync(expander);
    }

    /// <summary>The fold's body animates from zero height (see
    /// <see cref="Rendering.RevealPresenter"/>), so a single BringIntoView at
    /// click time asks for a strip a few pixels tall and the page barely moves.
    /// Following the panel while it opens is what puts the new fields on
    /// screen — and it reads as scrolling rather than as a jump.</summary>
    private static async Task FollowRevealAsync(Expander expander)
    {
        // RevealPresenter.Duration is 200ms; a couple of frames of margin covers
        // the settling measure pass.
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(260);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(16);
            // Folded again mid-animation, or the window closed under us.
            if (!expander.IsExpanded || !expander.IsAttachedToVisualTree()) return;
            expander.BringIntoView();
        }
    }

    public event EventHandler? AccountRequested;
    public event EventHandler? SupportRequested;

    private void MarkAccountEntrySeen()
    {
        if (!_settings.AccountEntrySeen && IsActive && PAGE_Appearance.IsVisible && HasPageContent(PAGE_Appearance)
            && PART_AccountEntryCard.Bounds.Width > 0 && PART_AccountEntryCard.Bounds.Height > 0
            && _accountEntryViewport.Contains(new Rect(PART_AccountEntryCard.Bounds.Size)))
            _settings.AccountEntrySeen = true;
    }

    private void SelectSettingsPage(ListBoxItem item)
    {
        if (ReferenceEquals(PART_Nav.SelectedItem, item)) ShowSelectedPage();
        else PART_Nav.SelectedItem = item;
    }

    internal void OpenAgentPage() => SelectSettingsPage(PART_AgentNav);

    internal void OpenSandboxPage() => SelectSettingsPage(PART_SandboxNav);

    internal void OpenBrowserPage() => SelectSettingsPage(PART_BrowserNav);

    private void RefreshBrowserActivity()
    {
        var entries = _browserActivity?.Recent() ?? Array.Empty<BrowserActivityEntry>();
        var ok = this.FindResource("Brush.Text.Secondary") as Avalonia.Media.IBrush
                 ?? Avalonia.Media.Brushes.Gray;
        var bad = this.FindResource("Brush.Error") as Avalonia.Media.IBrush
                  ?? Avalonia.Media.Brushes.IndianRed;

        PART_BrowserActivity.ItemsSource = entries
            .Select(entry => new BrowserActivityRow(
                entry.At.ToString("MM-dd HH:mm"),
                BrowserControlTool.DisplayNameFor(entry.Action),
                entry.Note is { Length: > 0 } note
                    ? (entry.Host is { Length: > 0 } h ? $"{h} · {note}" : note)
                    : entry.Host ?? string.Empty,
                entry.Success ? ok : bad))
            .ToArray();
        PART_BrowserActivityEmpty.IsVisible = entries.Count == 0;
    }

    private void OnClearBrowserActivity(object? sender, RoutedEventArgs e)
    {
        _browserActivity?.Clear();
        RefreshBrowserActivity();
    }

    internal void OpenApprovalPage() => SelectSettingsPage(PART_ApprovalNav);

    internal void OpenProvidersPage() => SelectSettingsPage(PART_ProvidersNav);

    internal void OpenSearchPage() => SelectSettingsPage(PART_SearchNav);

    internal void OpenProviderTools(string? providerId)
    {
        OpenProvidersPage();
        if (string.IsNullOrWhiteSpace(providerId)) return;
        if (_settings.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal)) is not { } entry)
            return;
        EditProvider(entry, entry.Purpose);
        if (PART_ProviderToolsSection.IsVisible)
            _ = RevealSearchHitAsync(PART_ProviderToolsCard, PART_ProviderBuiltinSearchTitle, false);
    }

    internal void OpenPersonaPage(bool startNew, string? personaId = null)
    {
        SelectSettingsPage(PART_PersonasNav);
        if (startNew) OnNewPersona(this, new RoutedEventArgs());
        else if (_personas.Find(personaId) is { } persona)
        {
            PART_PersonaSearch.Clear();
            RefreshPersonaLibrary();
            PART_PersonaList.SelectedItem = persona;
        }
    }

    private async void OnSyncNowClick(object? sender, RoutedEventArgs e)
    {
        if (_cloudSync is null || !_settings.IsLoggedIn) return;
        if (_cloudSync.IsSyncing)
        {
            PART_CloudSyncStatus.Text = "云同步正在进行中。";
            return;
        }

        var generation = _settings.AccountSessionGeneration;
        PART_SyncNow.IsEnabled = false;
        PART_SyncNow.Content = "同步中...";
        PART_CloudSyncStatus.Text = "正在同步对话...";
        try
        {
            var progress = new Progress<string>(message =>
            {
                if (generation == _settings.AccountSessionGeneration) PART_CloudSyncStatus.Text = message;
            });
            var result = await _cloudSync.SyncAsync(progress);
            if (_conversations is not null) await _conversations.ReloadAsync();
            if (generation == _settings.AccountSessionGeneration)
                PART_CloudSyncStatus.Text = $"已同步：上传 {result.Uploaded} · 更新 {result.Downloaded} · 删除 {result.Deleted}";
        }
        catch (Exception ex)
        {
            if (generation == _settings.AccountSessionGeneration)
                PART_CloudSyncStatus.Text = $"同步失败：{ex.Message}";
        }
        finally
        {
            PART_SyncNow.Content = "立即同步";
            PART_SyncNow.IsEnabled = _settings.IsLoggedIn;
        }
    }

    private async void OnSyncConversationsClick(object? sender, RoutedEventArgs e)
    {
        if (_cloudSync is null || !_settings.IsLoggedIn) return;
        var generation = _settings.AccountSessionGeneration;
        try
        {
            await _cloudSync.UpdateCloudSyncSettingAsync(_settings.SyncConversations);
            if (generation != _settings.AccountSessionGeneration) return;
            PART_CloudSyncStatus.Text = _settings.SyncConversations
                ? "已开启对话云同步"
                : "已关闭对话云同步";
        }
        catch (Exception ex)
        {
            if (generation == _settings.AccountSessionGeneration)
                PART_CloudSyncStatus.Text = $"同步设置更新失败：{ex.Message}";
        }
    }

    private async void OnTracksToggleClick(object? sender, RoutedEventArgs e)
    {
        var error = await TracksToggleHelper.SyncAsync(
            _settings.TracksEnabled, _settings, _auth, _cloudSync);
        PART_TracksSummary.Text = error ?? string.Empty;
        PART_TracksSummary.IsVisible = error is not null;
        if (error is not null)
            _notifications?.Error("个性化设置同步失败", error, "personalization");
    }

    private async void OnManageTracksClick(object? sender, RoutedEventArgs e)
    {
        if (_personalization is null || !_settings.IsLoggedIn) return;
        var window = new PersonalizationWindow(
            _personalization, _settings, _auth, _cloudSync, _notifications);
        await window.ShowDialog(this);
    }

    private void EnsurePageContent(StackPanel page)
    {
        if (page.Children.Count != 0
            || !Resources.TryGetResource(page.Name + ".Content", ActualThemeVariant, out var resource)
            || resource is not IControlTemplate template) return;

        if (template.Build(this) is not { } content) return;
        if (page == PAGE_Agent && content.NameScope.Find("PART_RemoteWorkSection") is StackPanel remoteSection)
        {
            remoteSection.DataContext = _remoteWork;
            remoteSection.IsVisible = true;
        }
        _deferredPageNames.Add(page, content.NameScope);
        page.Children.Add(content.Result);
        if (page == PAGE_Providers) InitializeProviderPage();
        if (page == PAGE_Personas) InitializePersonaPage();
        if (page == PAGE_System) InitializeSystemPage();
        if (page == PAGE_PostProcessing) InitializeResponsePostProcessing();
        if (page == PAGE_Memory) InitializeMemoryPage();
        if (page == PAGE_Search) InitializeSearchPage();
        if (page == PAGE_ImageGeneration) InitializeImageGenerationPage();
        if (page == PAGE_Vision) RefreshSpecializedModelChoices();
        if (page == PAGE_Sandbox) InitializeSandboxPage();
        if (page == PAGE_Browser) InitializeBrowserPage();
        if (page == PAGE_Approval) InitializeApprovalPage();
        if (page == PAGE_Mcp) InitializeMcpPage();
        if (page == PAGE_Skills) InitializeSkillsPage();
        if (page == PAGE_Account) InitializeAccountPage();
        if (page == PAGE_Appearance) InitializeAppearancePage();
    }

    private void ShowSelectedPage()
    {
        // A group heading has no page; leave the current one up.
        if (PART_Nav.SelectedItem is not ListBoxItem item || !_navPages.TryGetValue(item, out var page)) return;

        EnsurePageContent(page);
        foreach (var other in _navPages.Values) other.IsVisible = other == page;
        // The rail scrolls at the default height. A page opened from elsewhere —
        // a search hit, 「去设置」, the Agent entry — should show where it sits.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => RevealNavItem(item), Avalonia.Threading.DispatcherPriority.Loaded);
        PART_ContentScroll.Offset = default;
        if (page != PAGE_Providers) CloseProviderEditor();
        if (page == PAGE_Providers) RefreshProviders();
        if (page == PAGE_Search) UpdateSearchStatusHint();
        if (page == PAGE_Account) RefreshAccountUi();
        if (page == PAGE_Storage) _ = RefreshStorageAsync();
        if (page == PAGE_Sandbox) _ = RefreshRuntimeStatusAsync();
        if (page == PAGE_Agent && _agentStatus is not null) _ = _agentStatus.LoadAsync();
        if (page == PAGE_Memory)
        {
            _memoryPage?.Refresh();
            // The rail's red dot means 整理模型 is unset, and that row lives in the
            // folded 记忆设置 — arriving on the page should put it in sight.
            if (_settings.MemoryNeedsModel) PART_MemorySettings.IsExpanded = true;
        }
        // Opening the page is the question "does this work" — answer it without
        // making the user press 检测 first.
        if (page == PAGE_Browser)
        {
            _ = _browserBridge.CheckAsync();
            RefreshBrowserActivity();
        }
    }

    /// <summary>Scrolls the rail just far enough to show <paramref name="item"/>.
    /// Not BringIntoView: the ListBox's own ScrollViewer takes that request,
    /// and it cannot scroll — it is unbounded inside PART_NavScroll.</summary>
    private void RevealNavItem(ListBoxItem item)
    {
        // The rail may have only just come back from behind the search results.
        PART_NavScroll.UpdateLayout();
        if (PART_NavScroll.Content is not Visual content
            || item.TranslatePoint(default, content) is not { } at) return;

        const double margin = 8;
        var offset = PART_NavScroll.Offset.Y;
        var viewport = PART_NavScroll.Viewport.Height;
        if (at.Y - margin < offset)
            offset = at.Y - margin;
        else if (at.Y + item.Bounds.Height + margin > offset + viewport)
            offset = at.Y + item.Bounds.Height + margin - viewport;
        PART_NavScroll.Offset = new Vector(0, Math.Max(0, offset));
    }

    // ---- choice lists ------------------------------------------------------

    private void BuildThemeChoices()
    {
        var order = new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark };

        PART_Theme.SelectedIndex = Array.IndexOf(order, _settings.ThemeMode) is var i && i >= 0 ? i : 0;
        PART_Theme.SelectionChanged += (_, _) =>
        {
            if (PART_Theme.SelectedIndex >= 0) _settings.ThemeMode = order[PART_Theme.SelectedIndex];
        };
    }

    private void BuildCornerStyleChoices()
    {
        var order = new[] { CornerStyle.Standard, CornerStyle.Small, CornerStyle.Square };

        PART_CornerStyle.SelectedIndex = Math.Max(0, Array.IndexOf(order, _settings.CornerStyle));
        PART_CornerStyle.SelectionChanged += (_, _) =>
        {
            if (PART_CornerStyle.SelectedIndex >= 0) _settings.CornerStyle = order[PART_CornerStyle.SelectedIndex];
        };
    }

    private void BuildSearchProviderChoices()
    {
        UpdateSearchStatusHint();

        PART_SearchProvider.SelectionChanged += (_, _) =>
        {
            if (_updatingSearchControls) return;
            if (PART_SearchProvider.SelectedItem is ComboBoxItem { Tag: string provider })
            {
                _settings.WebSearchProvider = provider;
            }
        };
    }

    private void BuildPermissionChoices()
    {
        var order = new[] { ToolPermissionMode.Approval, ToolPermissionMode.FullAccess };
        var labels = new[] { "跟随审批模式", "完全权限" };

        Bind(PART_ImagePermission, _settings.ImageGenerationPermissionMode,
            mode => _settings.ImageGenerationPermissionMode = mode);
        Bind(PART_VisionPermission, _settings.VisionPermissionMode,
            mode => _settings.VisionPermissionMode = mode);
        Bind(PART_McpPermission, _settings.McpPermissionMode,
            mode => _settings.McpPermissionMode = mode);
        Bind(PART_PythonPermission, _settings.PythonExecutionPermissionMode,
            mode => _settings.PythonExecutionPermissionMode = mode);
        Bind(PART_BrowserPermission, _settings.BrowserPermissionMode,
            mode => _settings.BrowserPermissionMode = mode);

        return;

        void Bind(ComboBox combo, ToolPermissionMode selected, Action<ToolPermissionMode> apply)
        {
            combo.ItemsSource = labels;
            combo.SelectedIndex = Math.Max(0, Array.IndexOf(order, selected));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0) apply(order[combo.SelectedIndex]);
            };
        }
    }

    private void BuildFontScaleChoices()
    {
        var scale = SettingsViewModel.NormalizeFontScale(_settings.FontScale);
        PART_FontScale.SelectedIndex = Array.IndexOf(FontScaleLevels, scale);
        PART_FontScale.SelectionChanged += (_, _) =>
        {
            if (PART_FontScale.SelectedIndex >= 0)
                _settings.FontScale = FontScaleLevels[PART_FontScale.SelectedIndex];
        };
    }

    // ---- image generation / vision ----------------------------------------

    private bool _updatingSpecializedModels;

    private void RefreshSpecializedModelChoices()
    {
        _updatingSpecializedModels = true;
        try
        {
            if (HasPageContent(PAGE_ImageGeneration))
            {
                if (!ReferenceEquals(PART_ImageGenerationModel.ItemsSource, _settings.ImageGenerationProviderModels))
                    PART_ImageGenerationModel.ItemsSource = _settings.ImageGenerationProviderModels;
                PART_ImageGenerationModel.SelectedItem = _settings.ImageGenerationProviderModels.FirstOrDefault(option =>
                    string.Equals(option.ProviderId, _settings.ImageGenerationProviderId, StringComparison.Ordinal)
                    && string.Equals(option.ModelId, _settings.ImageGenerationModelId, StringComparison.Ordinal));
                RefreshImageGenerationStatus();
            }

            if (HasPageContent(PAGE_Vision))
            {
                if (!ReferenceEquals(PART_VisionModel.ItemsSource, _settings.VisionProviderModels))
                    PART_VisionModel.ItemsSource = _settings.VisionProviderModels;
                PART_VisionModel.SelectedItem = _settings.VisionProviderModels.FirstOrDefault(option =>
                    string.Equals(option.ProviderId, _settings.VisionProxyProviderId, StringComparison.Ordinal)
                    && string.Equals(option.ModelId, _settings.VisionProxyModelId, StringComparison.Ordinal));
            }
        }
        finally
        {
            _updatingSpecializedModels = false;
        }
    }

    private void OnImageGenerationModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSpecializedModels
            || PART_ImageGenerationModel.SelectedItem is not ImageGenerationProviderModelOption option)
        {
            return;
        }

        _settings.ImageGenerationProviderId = option.ProviderId;
        _settings.ImageGenerationModelId = option.ModelId;
        RefreshImageGenerationStatus();
    }

    private void OnVisionModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSpecializedModels
            || PART_VisionModel.SelectedItem is not VisionProviderModelOption option)
        {
            return;
        }

        _settings.VisionProxyProviderId = option.ProviderId;
        _settings.VisionProxyModelId = option.ModelId;
    }

    private void OnManageProviders(object? sender, RoutedEventArgs e) => OpenProvidersPage();

    private void RefreshImageGenerationStatus()
    {
        PART_ImageGenerationStatus.Text = _settings.ImageGenerationProviderModels.Count == 0
            ? "请先在「模型服务」中添加图像服务。"
            : _settings.IsImageGenerationConfigured
                ? "已启用，BYOK 对话可调用该图像服务。"
                : "选择图像服务与模型后，即可在 BYOK 对话中创建图片。";
    }

    private async void OnTestImageGeneration(object? sender, RoutedEventArgs e)
    {
        if (_imageGenerationTool is null) return;

        PART_TestImageGeneration.IsEnabled = false;
        PART_TestImageGeneration.Content = "测试中...";
        PART_ImageGenerationStatus.Text = "正在发送一次测试生成...";
        try
        {
            var images = await _imageGenerationTool.GenerateAsync(
                _settings.BuildImageGenerationOptions(),
                "a single black dot on a white background",
                CancellationToken.None);
            PART_ImageGenerationStatus.Text = images.Count > 0
                ? "连接成功，图像服务可用。"
                : "连接成功，但未返回图片。";
        }
        catch (Exception ex)
        {
            PART_ImageGenerationStatus.Text = "连接失败：" + ex.Message;
        }
        finally
        {
            PART_TestImageGeneration.Content = "测试连接";
            PART_TestImageGeneration.IsEnabled = true;
        }
    }

    private void UpdateSearchStatusHint()
    {
        if (!HasPageContent(PAGE_Search)) return;
        _updatingSearchControls = true;
        try

        {
            var provider = SettingsViewModel.NormalizeWebSearchProvider(_settings.WebSearchProvider);
            PART_SearchProvider.SelectedItem = PART_SearchProvider.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, provider));
            var maxResults = provider == "molagpt" ? 6 : 10;
            PART_SearchMaxResults.Maximum = maxResults;
            PART_SearchProviderHint.Text = provider switch
            {
                "molagpt" => "登录后可用，使用账户额度。",
                "duckduckgo" => "免费使用，无需 API Key。",
                "deepseek" => "按成功请求计费。",
                _ => "需要 API Key。"
            };
            PART_SearchApiKey.PlaceholderText = provider == "duckduckgo" ? "无需 API Key" : "请输入 API Key";

            if (provider == "deepseek")
            {
                var official = _settings.GetOfficialDeepSeekProviders();
                var linked = _settings.GetLinkedDeepSeekSearchProvider();
                if (linked is not null)
                {
                    PART_DeepSeekDetectBar.IsVisible = false;
                    PART_DeepSeekLinkedRow.IsVisible = true;
                    PART_SearchKeyRows.IsVisible = false;
                    PART_DeepSeekLinkedMask.Text = MaskSecret(linked.ApiKey);
                    var multiple = official.Count > 1;
                    PART_DeepSeekLinkedName.IsVisible = !multiple;
                    PART_DeepSeekLinkedName.Text = linked.Name;
                    ToolTip.SetTip(PART_DeepSeekLinkedName, linked.Name);
                    PART_DeepSeekLinkedCombo.IsVisible = multiple;
                    PART_DeepSeekLinkedCombo.ItemsSource = official;
                    PART_DeepSeekLinkedCombo.SelectedItem = linked;
                }
                else
                {
                    PART_DeepSeekLinkedRow.IsVisible = false;
                    PART_SearchKeyRows.IsVisible = true;
                    PART_DeepSeekDetectBar.IsVisible = official.Count > 0;
                    if (official.Count > 0)
                    {
                        PART_DeepSeekDetectText.Text = official.Count > 1
                            ? $"检测到 {official.Count} 个 DeepSeek 配置。"
                            : "检测到模型服务中的 DeepSeek 配置。";
                        PART_DeepSeekDetectCombo.IsVisible = official.Count > 1;
                        PART_DeepSeekDetectCombo.ItemsSource = official;
                        if (PART_DeepSeekDetectCombo.SelectedItem is not ProviderEntry current
                            || !official.Any(item => string.Equals(item.Id, current.Id, StringComparison.Ordinal)))
                        {
                            PART_DeepSeekDetectCombo.SelectedItem = official[0];
                        }
                    }
                }
            }
            else
            {
                PART_DeepSeekDetectBar.IsVisible = false;
                PART_DeepSeekLinkedRow.IsVisible = false;
                PART_SearchKeyRows.IsVisible = provider != "molagpt";
            }

            var chatProviders = _settings.Providers
                .Where(entry => !ProviderPurpose.IsImage(entry.Purpose))
                .ToArray();
            var hasBuiltinSearchVendor = chatProviders.Any(entry =>
                BuiltinWebSearch.DetectVendor(entry.BaseUrl) is not null);
            var hasActiveBuiltinSearch = chatProviders.Any(entry =>
                entry.Enabled && BuiltinWebSearch.IsEnabledOnProvider(entry.Type, entry.BaseUrl, entry.BuiltinSearch));
            PART_BuiltinSearchTipRow.IsVisible = hasBuiltinSearchVendor;
            if (hasActiveBuiltinSearch)
            {
                PART_BuiltinSearchTipText.Text = "已在模型服务中开启内置搜索。";
                PART_OpenBuiltinSearchProviders.Content = "管理";
            }
            else if (hasBuiltinSearchVendor)
            {
                PART_BuiltinSearchTipText.Text = "部分模型支持内置搜索。";
                PART_OpenBuiltinSearchProviders.Content = "前往模型服务";
            }

            var deepSeekConfigured = _settings.IsWebSearchProviderConfigured("deepseek");
            var tavilyConfigured = _settings.IsWebSearchProviderConfigured("tavily");
            var exaConfigured = _settings.IsWebSearchProviderConfigured("exa");
            PART_WebFetchDeepSeekItem.IsEnabled = deepSeekConfigured;
            PART_WebFetchTavilyItem.IsEnabled = tavilyConfigured;
            PART_WebFetchExaItem.IsEnabled = exaConfigured;
            var fetchMode = _settings.EffectiveWebFetchProvider;
            PART_WebFetchMode.SelectedItem = PART_WebFetchMode.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, fetchMode));
            var allFetchConfigured = deepSeekConfigured && tavilyConfigured && exaConfigured;
            PART_WebFetchHint.Text = fetchMode switch
            {
                "deepseek" => "使用 DeepSeek Search 读取网页，按成功请求计费。",
                "tavily" => "使用 Tavily 提取网页正文，按服务商额度计费。",
                "exa" => "使用 Exa 提取网页正文，按服务商额度计费。",
                _ => allFetchConfigured
                    ? "本机读取不产生额外费用。"
                    : "本机读取不产生额外费用。其他方式需先配置对应搜索服务的 API Key。"
            };

            PART_SearchStatus.Text = provider switch
            {
                "molagpt" => _accountSearch is { IsSignedIn: true } account
                    ? $"当前登录账号：{account.Username}"
                    : "请登录 MolaGPT 账号。",
                _ => string.Empty
            };
        }
        finally
        {
            _updatingSearchControls = false;
        }
    }

    private static string MaskSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return string.Empty;
        var trimmed = secret.Trim();
        if (trimmed.Length <= 8) return "••••";
        var dash = trimmed.IndexOf('-');
        var prefixLength = dash is > 0 and <= 7 ? dash + 1 : 3;
        return trimmed[..prefixLength] + "••••" + trimmed[^4..];
    }

    private void OnUseDetectedDeepSeek(object? sender, RoutedEventArgs e)
    {
        var official = _settings.GetOfficialDeepSeekProviders();
        if (official.Count == 0) return;
        var selected = PART_DeepSeekDetectCombo.SelectedItem as ProviderEntry ?? official[0];
        _settings.DeepSeekSearchLinkedProviderId = selected.Id;
    }

    private void OnUnlinkDeepSeekSearch(object? sender, RoutedEventArgs e)
    {
        _settings.DeepSeekSearchLinkedProviderId = null;
    }

    private void OnDeepSeekLinkedComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSearchControls || PART_DeepSeekLinkedCombo.SelectedItem is not ProviderEntry entry) return;
        _settings.DeepSeekSearchLinkedProviderId = entry.Id;
    }

    private void OnWebFetchModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSearchControls) return;
        if (PART_WebFetchMode.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        _settings.WebFetchProvider = mode == "local" || _settings.IsWebSearchProviderConfigured(mode)
            ? mode
            : "local";
        UpdateSearchStatusHint();
    }

    private async Task TestMolaSearchAsync()
    {
        if (!_accountSearch.IsSignedIn)
        {
            PART_SearchStatus.Text = "请登录 MolaGPT 账号。";
            return;
        }

        try
        {
            await _accountSearch.SearchAsync(
                [new MolaSearchQuery("MolaGPT", "general", null, null, false)], 1, 200, CancellationToken.None);
            PART_SearchStatus.Text = "搜索可用。";
        }
        catch (MolaSearchException ex)
        {
            PART_SearchStatus.Text = ex.Message;
        }
    }

    private async void OnTestSearch(object? sender, RoutedEventArgs e)
    {
        var provider = SettingsViewModel.NormalizeWebSearchProvider(_settings.WebSearchProvider);
        var molaSearch = provider == "molagpt";

        PART_TestSearch.IsEnabled = false;
        PART_TestSearch.Content = "测试中…";
        PART_SearchStatus.Text = "正在测试搜索…";
        try
        {
            if (molaSearch)
            {
                await TestMolaSearchAsync();
                return;
            }

            var options = new LocalToolOptions(
                Network: true,
                WebPage: false,
                SearchProvider: provider,
                SearchApiKey: _settings.WebSearchApiKeyFor(provider),
                SearchBaseUrl: _settings.WebSearchBaseUrlFor(provider),
                SearchMaxResults: Math.Clamp(_settings.WebSearchMaxResults, 1, 10),
                WebPageMaxCharacters: Math.Clamp(_settings.WebPageMaxCharacters, 1000, 30000));
            using var http = _byokHttpFactory();
            var result = await LocalToolRegistry.ExecuteAsync(
                "search_web", "{\"query\":\"MolaGPT\"}", options, http, CancellationToken.None);
            using var document = JsonDocument.Parse(result);
            var ok = document.RootElement.TryGetProperty("success", out var success)
                     && success.ValueKind == JsonValueKind.True;
            if (ok && document.RootElement.TryGetProperty("queries", out var queries)
                && queries.ValueKind == JsonValueKind.Array && queries.GetArrayLength() > 0)
            {
                var query = queries[0];
                PART_SearchStatus.Text = query.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    ? "搜索失败：" + error.GetString()
                    : "搜索可用。";
            }
            else
                PART_SearchStatus.Text = "搜索不可用，请检查 API Key 或接口地址。";
        }
        catch (Exception ex)
        {
            PART_SearchStatus.Text = "搜索失败：" + ex.Message;
        }
        finally
        {
            PART_TestSearch.Content = "测试搜索";
            PART_TestSearch.IsEnabled = true;
        }
    }

    // ---- providers ---------------------------------------------------------

    private void RefreshProviders()
    {
        if (!HasPageContent(PAGE_Providers)) return;
        var chatProviders = _settings.Providers
            .Where(provider => !ProviderPurpose.IsImage(provider.Purpose))
            .Select(provider => new ProviderCardRow(provider, ChatProviderInactiveReason(provider)))
            .ToArray();
        // Image rows never go through the agent runtime, so they have no registry
        // state to disagree with and are listed as they always were.
        var imageProviders = _settings.Providers
            .Where(provider => ProviderPurpose.IsImage(provider.Purpose))
            .Select(provider => new ProviderCardRow(provider, null))
            .ToArray();

        PART_ChatProviderList.ItemsSource = chatProviders;
        PART_ImageProviderList.ItemsSource = imageProviders;
        PART_NoChatProviders.IsVisible = chatProviders.Length == 0;
        PART_NoImageProviders.IsVisible = imageProviders.Length == 0;
    }

    /// <summary>Why this saved row is not in the picker, or null when it is.</summary>
    private string? ChatProviderInactiveReason(ProviderEntry entry)
    {
        // No registry to compare against (design-time): claiming either state would
        // be a guess, and "inactive" is the one that would worry people wrongly.
        if (_providerRegistry is null) return null;
        if (!entry.Enabled) return "已停用";
        if (entry.Type == ChatGptType && ChatGpt is { IsSignedIn: false }) return "未登录";
        if (_providerRegistry.GetById(entry.Id) is not null)
            return null;

        return _piByokProviderFactory?.IsRuntimeAvailable == false ? "待更新运行环境" : "未生效";
    }

    private void OnEditProvider(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        if (_settings.Providers.FirstOrDefault(p => p.Id == id) is { } entry)
            EditProvider(entry, entry.Purpose);
    }

    private void EditProvider(ProviderEntry? existing, string purpose)
    {
        _editingProvider = existing;
        _editingProviderPurpose = ProviderPurpose.IsImage(existing?.Purpose ?? purpose) ? "image" : "chat";
        _editingProviderBuiltinSearch = existing?.BuiltinSearch == true;
        _providerModels.Clear();
        _providerHeaders.Clear();
        CloseDetectedModels();

        _loadingProviderForm = true;
        try
        {
            PART_ProviderChatGptSource.IsChecked = existing?.Type == ChatGptType && _editingProviderPurpose != "image";
            PART_ProviderApiSource.IsChecked = !EditingChatGpt;
            SetProviderPresetItems();
            if (existing is null)
            {
                ApplyProviderPreset((ProviderPresetRow)PART_ProviderPreset.SelectedItem!);
            }
            else
            {
                SelectProviderPreset(FindProviderPreset(existing));
                PART_ProviderType.SelectedIndex = ProviderTypeIndex(existing.Type);
                PART_ProviderName.Text = existing.Name;
                var legacyGemini = GeminiModelPolicy.IsLegacyNativePreset(existing.Type, existing.BaseUrl, existing.ApiPath);
                PART_ProviderBaseUrl.Text = legacyGemini ? GeminiNativeBaseUrl : existing.BaseUrl ?? string.Empty;
                PART_ProviderHttps.IsChecked = !PART_ProviderBaseUrl.Text.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
                PART_ProviderApiPath.Text = legacyGemini
                    ? "models/{model}:streamGenerateContent"
                    : string.IsNullOrWhiteSpace(existing.ApiPath)
                    ? ProviderConnection.DefaultApiPath(existing.Purpose, existing.Type, existing.BaseUrl)
                    : existing.ApiPath;
                PART_ProviderImageFormat.SelectedIndex = ImageApiFormat.IsChatImage(existing.ImageFormat) ? 1 : 0;
                PART_ProviderImageEditPath.Text = string.IsNullOrWhiteSpace(existing.ImageEditPath)
                    ? "v1/images/edits"
                    : existing.ImageEditPath;
                PART_ProviderImageChatPath.Text = existing.ImageChatPath ?? ImageApiFormat.DefaultChatPath(existing.Type);

                foreach (var model in existing.Models) _providerModels.Add(CreateModelRow(model));
                foreach (var header in existing.CustomHeaders ?? [])
                    _providerHeaders.Add(new HeaderRow { Name = header.Name, Value = header.Value });
            }

            PART_ProviderApiKey.Text = existing?.ApiKey ?? string.Empty;
            PART_ProviderApiKey.PasswordChar = '•';
        }
        finally
        {
            _loadingProviderForm = false;
        }

        PART_ProviderEditorTitle.Text = existing is null
            ? (_editingProviderPurpose == "image" ? "添加图像服务" : "添加对话服务")
            : $"编辑「{existing.Name}」";
        PART_DeleteEditingProvider.IsVisible = existing is not null;
        PART_ProviderError.IsVisible = false;
        PART_ProviderStatus.IsVisible = false;
        PART_ProviderOverview.IsVisible = false;
        PART_ProviderEditor.IsVisible = true;
        UpdateProviderPurposeUi();
        UpdateProviderEndpointPreview();
        RefreshProviderModelEmptyState();
        PART_ContentScroll.Offset = default;
    }

    private void CloseProviderEditor()
    {
        if (!HasPageContent(PAGE_Providers)) return;
        _chatGptSignIn?.Cancel();
        PART_ChatGptError.IsVisible = false;
        CloseDetectedModels();
        PART_ProviderEditor.IsVisible = false;
        PART_ProviderOverview.IsVisible = true;
        _editingProvider = null;
        PART_ContentScroll.Offset = default;
    }

    private void RefreshProviderModelEmptyState() =>
        PART_NoProviderModels.IsVisible = _providerModels.Count == 0;

    private void OnRemoveProviderModel(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ModelRow row }) _providerModels.Remove(row);
    }

    private void OnRemoveProviderHeader(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HeaderRow row }) _providerHeaders.Remove(row);
    }

    private void AddProviderModel()
    {
        var id = "new-model";
        var suffix = 1;
        while (_providerModels.Any(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)))
            id = "new-model-" + ++suffix;

        var row = CreateModelRow(new ProviderModelEntry(id, "新模型"));
        row.IsExpanded = true;
        _providerModels.Add(row);
    }

    private void OnAddProviderBody(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModelRow model } button) return;
        if (model.IsImageProvider || EditingProviderType is "gemini" or "chatgpt")
        {
            model.CustomBodyRows.Add(new BodyRow(model));
            return;
        }

        (string Key, string Value)[] presets = EditingProviderType switch
        {
            "openai-compat" =>
            [
                ("temperature", "0.7"), ("top_p", "0.9"),
                ("frequency_penalty", "0"), ("presence_penalty", "0"), ("seed", "42"),
            ],
            "anthropic" => [("temperature", "0.7"), ("top_p", "0.9"), ("top_k", "40")],
            _ => [("max_output_tokens", "4096")],
        };
        var menu = new MenuFlyout();
        var manual = new MenuItem { Header = "手动填写" };
        manual.Click += (_, _) => model.CustomBodyRows.Add(new BodyRow(model));
        menu.Items.Add(manual);
        foreach (var (key, value) in presets)
        {
            var item = new MenuItem
            {
                Header = key,
                IsEnabled = !model.CustomBodyRows.Any(row => string.Equals(row.Key.Trim(), key, StringComparison.Ordinal)),
            };
            item.Click += (_, _) => model.CustomBodyRows.Add(new BodyRow(model, key, "number", value));
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }

    private void OnRemoveProviderBody(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BodyRow row }) row.Owner.CustomBodyRows.Remove(row);
    }

    private void OnAddCustomEffortLevel(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ModelRow row }) row.AddCustomEffortLevel();
    }

    private void OnRemoveCustomEffortLevel(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: EffortLevelItem item }) return;
        item.Owner.RemoveEffortLevel(item);
    }

    private void OnCustomEffortKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is Control { DataContext: ModelRow row }) row.AddCustomEffortLevel();
        e.Handled = true;
    }

    private void OnApplyBudgetPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.DataContext is not BudgetPreset preset) return;
        if (FindOwningModelRow(control) is { } row) row.ApplyBudgetPreset(preset);
    }

    /// <summary>The preset button's DataContext is the preset itself, so the
    /// owning model row is resolved by walking up to the first ancestor that
    /// carries one (the model card keeps ModelRow as its DataContext).</summary>
    private static ModelRow? FindOwningModelRow(StyledElement element)
    {
        var current = element.Parent;
        while (current is not null)
        {
            if (current is Control { DataContext: ModelRow row }) return row;
            current = current.Parent;
        }
        return null;
    }

    private void SaveProvider()
    {
        if (!TryCollectProvider(out var result)) return;
        if (result.Models.Count == 0)
        {
            FailProviderEdit("至少要有一个填了 ID 的模型。");
            return;
        }

        ProviderApplyOutcome outcome;
        try
        {
            outcome = _providerRestorer.Save(result);
        }
        catch (Exception ex)
        {
            FailProviderEdit("更新服务失败：" + ex.Message);
            return;
        }

        _editingProvider = result;
        PART_ProviderEditorTitle.Text = $"编辑「{result.Name}」";
        PART_DeleteEditingProvider.IsVisible = true;
        ShowProviderSaveOutcome(outcome);
    }

    /// <summary>
    /// "已保存" and "可用" are two different states, and this page can only see the
    /// first one: the save writes to the database, while the model picker reads the
    /// registry. Reporting success on the strength of the write alone left users
    /// re-saving a perfectly good configuration against a picker that never
    /// changed — the save was never the thing that was broken.
    /// </summary>
    private void ShowProviderSaveOutcome(ProviderApplyOutcome outcome)
    {
        switch (outcome)
        {
            case ProviderApplyOutcome.RuntimeUnavailable:
                FailProviderEdit(
                    "设置已保存，但 Agent 运行环境需要更新，该服务暂时不会出现在模型选择器中。"
                    + "请到「代码与文件」更新后重试。");
                break;
            case ProviderApplyOutcome.Unsupported:
                FailProviderEdit(
                    "设置已保存，但 Agent 运行时无法承载这个服务，它不会出现在模型选择器中。"
                    + "请检查协议、接入地址、API Key 与模型列表。");
                break;
            default:
                ShowProviderStatus("设置已保存");
                break;
        }
    }

    private ProviderModelEntry ToProviderModel(ModelRow row)
    {
        var id = row.Id.Trim();
        var displayName = string.IsNullOrWhiteSpace(row.DisplayName) ? id : row.DisplayName.Trim();

        var source = row.Source ?? new ProviderModelEntry(id, displayName);
        var hasReasoning = row.Thinking || row.ReasoningEffort;
        var thinkingKind = hasReasoning ? ModelRow.ThinkingKindForIndex(row.ThinkingKindIndex)
            : source.Thinking || source.ThinkingParamKind == nameof(ThinkingParamKind.None) ? nameof(ThinkingParamKind.None) : null;
        var effortLevels = hasReasoning
            ? ThinkingEffortLevels.Normalize(row.EffortLevelsText.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries)).ToList()
            : [];
        var customBody = row.CustomBodyRows
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(item => new CustomBodyEntry(item.Key.Trim(), item.Type, item.Value ?? string.Empty))
            .ToList();

        var model = source with
        {
            Id = id,
            DisplayName = displayName,
            Vision = row.Vision,
            Thinking = row.Thinking,
            ReasoningEffort = row.ReasoningEffort,
            Tools = row.Tools,
            SupportsTemperature = row.SupportsTemperature,
            SupportsTopP = row.SupportsTopP,
            ContextWindow = ParseNullableInt(row.ContextWindowText),
            ThinkingParamKind = thinkingKind,
            ThinkingBudgetMin = hasReasoning && row.IsBudgetThinkingKind ? ParseNullableInt(row.BudgetMinText) : null,
            ThinkingBudgetMax = hasReasoning && row.IsBudgetThinkingKind ? ParseNullableInt(row.BudgetMaxText) : null,
            ThinkingBudgetDefault = hasReasoning && row.IsBudgetThinkingKind ? ParseNullableInt(row.BudgetDefaultText) : null,
            DefaultEffort = hasReasoning && !string.IsNullOrWhiteSpace(row.DefaultEffort) ? row.DefaultEffort.Trim().ToLowerInvariant() : null,
            ImageEdit = row.IsImageProvider && row.ImageEdit,
            ImageFormat = row.IsImageProvider && row.CanChooseImageFormat ? row.ImageFormatIndex switch
            {
                1 => ImageApiFormat.OpenAiImages,
                2 => ImageApiFormat.OpenAiChatImage,
                _ => null
            } : null,
            CustomBody = customBody.Count > 0 ? customBody : null,
            EffortLevels = effortLevels.Count > 0 ? effortLevels : null,
            Pricing = row.Pricing(),
            BuiltinSearch = row.IsBuiltinSearchVendor ? row.BuiltinSearchOverride : row.Source?.BuiltinSearch
        };
        return row.IsImageProvider ? model : ProviderModelResolver.ResolveModelCapabilities(
            model, EditingProviderType, PART_ProviderBaseUrl.Text, PART_ProviderApiPath.Text);
    }

    private bool TryCollectProvider(out ProviderEntry entry)
    {
        var name = PART_ProviderName.Text?.Trim() ?? string.Empty;
        var baseUrl = PART_ProviderBaseUrl.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            FailProviderEdit("给这个服务起个名字。");
            entry = default!;
            return false;
        }
        // A ChatGPT row has no address of its own; the account decides where it goes.
        if (EditingChatGpt)
            baseUrl = OpenAiBaseUrl;
        if (baseUrl.Length == 0)
        {
            FailProviderEdit("接入地址不能为空。");
            entry = default!;
            return false;
        }

        var normalizedBaseUrl = baseUrl.TrimEnd('/') + "/";
        try
        {
            NetworkSecurity.RequireHttpOrHttpsBaseUrl(normalizedBaseUrl, $"{name} 接入地址");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            FailProviderEdit("接入地址必须是有效的 http:// 或 https:// 地址。");
            entry = default!;
            return false;
        }

        var models = _providerModels
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(ToProviderModel)
            .ToList();
        var headers = _providerHeaders
            .Where(header => !string.IsNullOrWhiteSpace(header.Name))
            .Select(header => new CustomHeaderEntry(header.Name.Trim(), header.Value ?? string.Empty))
            .ToList();
        var image = _editingProviderPurpose == "image";
        var imageFormat = image && (EditingProviderType == "gemini" || PART_ProviderImageFormat.SelectedIndex == 1)
            ? ImageApiFormat.OpenAiChatImage
            : image ? ImageApiFormat.OpenAiImages : null;

        // The hidden fields of a ChatGPT row may still hold what was typed before
        // the connection method was switched; none of it applies.
        var chatGpt = EditingChatGpt;
        var apiPath = chatGpt || string.IsNullOrWhiteSpace(PART_ProviderApiPath.Text)
            ? null
            : PART_ProviderApiPath.Text.Trim();
        if (!image && !chatGpt && string.Equals(EditingProviderType, "gemini", StringComparison.OrdinalIgnoreCase))
        {
            apiPath = (apiPath ?? ProviderConnection.DefaultApiPath(ProviderPurpose.Chat, "gemini"))
                .Replace(":generateContent", ":streamGenerateContent", StringComparison.Ordinal);
            if (!apiPath.Contains("{model}", StringComparison.Ordinal)
                || !apiPath.Split('?', 2)[0].EndsWith(":streamGenerateContent", StringComparison.Ordinal))
            {
                FailProviderEdit("Gemini 原生对话路径须包含 {model}，并以 :streamGenerateContent 结尾。");
                entry = default!;
                return false;
            }
            PART_ProviderApiPath.Text = apiPath;
        }

        entry = new ProviderEntry(
            _editingProvider?.Id ?? Guid.NewGuid().ToString("N"),
            EditingProviderType,
            name,
            normalizedBaseUrl,
            chatGpt || string.IsNullOrEmpty(PART_ProviderApiKey.Text) ? null : PART_ProviderApiKey.Text,
            models,
            Enabled: true,
            SortOrder: _editingProvider?.SortOrder ?? _settings.Providers.Count,
            Purpose: _editingProviderPurpose,
            ApiPath: apiPath,
            ImageEditPath: image && !string.IsNullOrWhiteSpace(PART_ProviderImageEditPath.Text)
                    ? PART_ProviderImageEditPath.Text.Trim()
                    : null,
            ImageFormat: imageFormat,
            CustomHeaders: !chatGpt && headers.Count > 0 ? headers : null,
            ImageChatPath: image ? PART_ProviderImageChatPath.Text?.Trim() : null,
            BuiltinSearch: !image && !chatGpt && _editingProviderBuiltinSearch);
        return true;
    }

    private static int? ParseNullableInt(string? text) =>
        int.TryParse(text?.Trim(), out var value) ? value : null;

    private void FailProviderEdit(string message)
    {
        PART_ProviderStatus.IsVisible = false;
        PART_ProviderError.Text = message;
        PART_ProviderError.IsVisible = true;
    }

    private void ShowProviderStatus(string message)
    {
        PART_ProviderError.IsVisible = false;
        PART_ProviderStatus.Text = message;
        PART_ProviderStatus.IsVisible = true;
    }

    private async void OnDeleteProvider(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id }) return;
        if (_settings.Providers.FirstOrDefault(p => p.Id == id) is not { } entry) return;

        // Deleting a provider takes its key and its models with it, and there is
        // no undo on this one — so it asks.
        if (!await Confirm.AskAsync(this, $"删除「{entry.Name}」？", "该服务的 API Key 和模型列表会一并移除。", "删除"))
            return;

        DeleteProvider(entry);
    }

    private async void OnDeleteEditingProvider(object? sender, RoutedEventArgs e)
    {
        if (_editingProvider is not { } entry) return;
        if (!await Confirm.AskAsync(this, $"删除「{entry.Name}」？", "该服务的 API Key 和模型列表会一并移除。", "删除"))
            return;

        if (DeleteProvider(entry)) CloseProviderEditor();
    }

    private bool DeleteProvider(ProviderEntry entry)
    {
        try
        {
            _providerRestorer.Delete(entry.Id);
            return true;
        }
        catch (Exception ex)
        {
            _notifications?.Error("删除模型服务失败", ex.Message);
            return false;
        }
    }

    private ModelRow CreateModelRow(ProviderModelEntry model)
    {
        var image = _editingProviderPurpose == "image";
        if (!image)
            model = ProviderModelResolver.ResolveModelCapabilities(
                model, EditingProviderType, PART_ProviderBaseUrl.Text, PART_ProviderApiPath.Text);
        var geminiLevel = !image && model.ThinkingParamKind == nameof(ThinkingParamKind.GeminiThinkingLevel)
            && GeminiModelPolicy.IsRoute(EditingProviderType, PART_ProviderBaseUrl.Text, PART_ProviderApiPath.Text);

        var row = new ModelRow
        {
            Id = model.Id,
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? ProviderModelResolver.BeautifyModelName(model.Id) : model.DisplayName,
            Vision = model.Vision,
            Thinking = model.Thinking,
            ReasoningEffort = model.ReasoningEffort,
            Tools = model.Tools,
            SupportsTemperature = model.SupportsTemperature,
            SupportsTopP = model.SupportsTopP,
            CanEditGenerationParameters = !geminiLevel,
            ContextWindowText = model.ContextWindow?.ToString() ?? string.Empty,
            ThinkingKindIndex = ModelRow.ThinkingKindIndexFor(model.Thinking || model.ReasoningEffort
                ? model.ThinkingParamKind ?? DefaultThinkingKindForProvider() : DefaultThinkingKindForProvider()),
            BudgetMinText = model.ThinkingBudgetMin?.ToString() ?? string.Empty,
            BudgetMaxText = model.ThinkingBudgetMax?.ToString() ?? string.Empty,
            BudgetDefaultText = model.ThinkingBudgetDefault?.ToString() ?? string.Empty,
            DefaultEffort = string.IsNullOrWhiteSpace(model.DefaultEffort) ? "high" : model.DefaultEffort,
            ImageEdit = model.ImageEdit,
            ImageFormatIndex = model.ImageFormat is null ? 0 : ImageApiFormat.IsChatImage(model.ImageFormat) ? 2 : 1,
            IsImageProvider = image,
            CanChooseImageFormat = EditingProviderType != "gemini",
            EffortLevelsText = string.Join(", ", model.EffortLevels ?? []),
            Source = model,
            LoadedPricing = model.Pricing
        };
        var allowedLevels = geminiLevel
            ? GeminiModelPolicy.OfficialLevelPolicy(model.Id, EditingProviderType, PART_ProviderBaseUrl.Text,
                PART_ProviderApiPath.Text)?.EffortLevels : null;
        row.RefreshEffortLevelOptions(allowedLevels);
        row.LoadPricing(model.Pricing);
        foreach (var item in model.CustomBody ?? [])
            row.CustomBodyRows.Add(new BodyRow(row, item.Key, item.Type, item.Value));
        var builtinVendor = _editingProviderPurpose != "image" && !EditingChatGpt
            ? BuiltinWebSearch.DetectVendor(PART_ProviderBaseUrl.Text)
            : null;
        var providerBuiltinActive = builtinVendor is { } activeVendor
            && BuiltinWebSearch.IsSupportedProviderType(activeVendor, EditingProviderType)
            && _editingProviderBuiltinSearch;
        row.SetBuiltinSearchContext(
            builtinVendor,
            EditingProviderType,
            providerBuiltinActive,
            model.BuiltinSearch,
            applyInitialOverride: true);
        return row;
    }

    /// <summary>Initial format when enabling reasoning on a model without capability metadata.</summary>
    private string DefaultThinkingKindForProvider() =>
        EditingProviderType switch
        {
            "anthropic" => nameof(ThinkingParamKind.AnthropicAdaptive),
            "gemini" => nameof(ThinkingParamKind.GeminiThinkingLevel),
            _ => nameof(ThinkingParamKind.OpenAiReasoningEffort),
        };

    private void SetProviderPresetItems()
    {
        var items = ProviderPresets
            .Where(preset => ProviderPurpose.IsImage(preset.Purpose) == (_editingProviderPurpose == "image"))
            .ToArray();
        _applyingProviderPreset = true;
        try
        {
            PART_ProviderPreset.ItemsSource = items;
            PART_ProviderPreset.SelectedIndex = 0;
        }
        finally
        {
            _applyingProviderPreset = false;
        }
    }

    private void SelectProviderPreset(ProviderPresetRow? preset)
    {
        _applyingProviderPreset = true;
        try
        {
            PART_ProviderPreset.SelectedItem = preset ?? PART_ProviderPreset.Items.Cast<ProviderPresetRow>().LastOrDefault();
        }
        finally
        {
            _applyingProviderPreset = false;
        }
    }

    private static ProviderPresetRow? FindProviderPreset(ProviderEntry entry) =>
        ProviderPresets.FirstOrDefault(preset =>
            ProviderPurpose.IsImage(preset.Purpose) == ProviderPurpose.IsImage(entry.Purpose)
            && string.Equals(preset.Type, entry.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(preset.BaseUrl), NormalizeUrl(entry.BaseUrl), StringComparison.OrdinalIgnoreCase))
        ?? (GeminiModelPolicy.IsLegacyNativePreset(entry.Type, entry.BaseUrl, entry.ApiPath)
            ? ProviderPresets.FirstOrDefault(preset => preset.Id == "gemini")
            : null);

    private static string NormalizeUrl(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');

    private void OnProviderPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingProviderPreset || _loadingProviderForm || PART_ProviderPreset.SelectedItem is not ProviderPresetRow preset)
            return;

        _loadingProviderForm = true;
        try
        {
            _editingProviderBuiltinSearch = false;
            ApplyProviderPreset(preset);
            _providerModels.Clear();
            _providerHeaders.Clear();
        }
        finally
        {
            _loadingProviderForm = false;
        }
        UpdateProviderPurposeUi();
        UpdateProviderEndpointPreview();
        ShowProviderStatus($"已套用「{preset.Name}」预设。填入 API Key 后可自动获取模型。");
    }

    private void ApplyProviderPreset(ProviderPresetRow preset)
    {
        PART_ProviderName.Text = preset.Name;
        PART_ProviderType.SelectedIndex = ProviderTypeIndex(preset.Type);
        PART_ProviderBaseUrl.Text = preset.BaseUrl;
        PART_ProviderHttps.IsChecked = !preset.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        PART_ProviderApiPath.Text = string.IsNullOrWhiteSpace(preset.ApiPath)
            ? ProviderConnection.DefaultApiPath(preset.Purpose, preset.Type)
            : preset.ApiPath;
        PART_ProviderImageFormat.SelectedIndex = ImageApiFormat.IsChatImage(preset.ImageFormat) ? 1 : 0;
        PART_ProviderImageEditPath.Text = string.IsNullOrWhiteSpace(preset.ImageEditPath)
            ? "v1/images/edits"
            : preset.ImageEditPath;
        PART_ProviderImageChatPath.Text = preset.ImageChatPath ?? ImageApiFormat.DefaultChatPath(preset.Type);
    }

    private static int ProviderTypeIndex(string? type)
    {
        var normalized = string.Equals(type, "openai", StringComparison.OrdinalIgnoreCase) ? "openai-compat" : type;
        var index = Array.FindIndex(ProviderTypes, value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, index);
    }

    private string EditingProviderType => EditingChatGpt
        ? ChatGptType
        : ProviderTypes[Math.Max(0, PART_ProviderType.SelectedIndex)];

    private void OnProviderSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingProviderForm || sender is not RadioButton { IsChecked: true }) return;

        if (sender == PART_ProviderApiSource) PART_ProviderChatGptSource.IsChecked = false;

        _chatGptSignIn?.Cancel();
        PART_ChatGptError.IsVisible = false;
        PART_ProviderError.IsVisible = false;
        PART_ProviderStatus.IsVisible = false;
        CloseDetectedModels();
        _editingProviderBuiltinSearch = false;
        _providerModels.Clear();

        if (PART_ProviderPreset.SelectedItem is ProviderPresetRow preset)
        {
            var name = PART_ProviderName.Text?.Trim();
            if (EditingChatGpt && (string.IsNullOrEmpty(name) || name == preset.Name))
                PART_ProviderName.Text = "ChatGPT";
            else if (!EditingChatGpt && name == "ChatGPT")
                PART_ProviderName.Text = preset.Name;
        }
        UpdateProviderPurposeUi();
        UpdateProviderEndpointPreview();
    }

    private void UpdateProviderPurposeUi()
    {
        var image = _editingProviderPurpose == "image";
        PART_ProviderSourceField.IsVisible = !image;
        PART_ProviderPurpose.Text = image ? "图像服务" : "对话服务";
        PART_ProviderImageFields.IsVisible = image;
        PART_ProviderApiPathLabel.Text = image ? "图像路径" : "对话路径";

        for (var i = 0; i < PART_ProviderType.Items.Count; i++)
        {
            if (PART_ProviderType.Items[i] is not ComboBoxItem item) continue;
            item.IsVisible = !image || i is 0 or 2;
            item.IsEnabled = item.IsVisible;
        }
        if (image && PART_ProviderType.SelectedIndex is not (0 or 2)) PART_ProviderType.SelectedIndex = 0;

        var geminiImage = image && EditingProviderType == "gemini";
        if (geminiImage) PART_ProviderImageFormat.SelectedIndex = 1;
        PART_ProviderImageFormat.IsEnabled = !geminiImage;
        PART_ProviderImageFormatHint.Text = PART_ProviderImageFormat.SelectedIndex == 1
            ? "适用于 Nano Banana 等可输出图像的多模态模型"
            : "适用于 gpt-image、Seedream、Qwen-Image 等图像模型";
        PART_ProviderImageEditPathField.IsVisible = image && !geminiImage && !OpenRouterAttribution.IsOpenRouterHost(PART_ProviderBaseUrl.Text);
        PART_FetchEditingProviderPricing.IsVisible = !image && !EditingChatGpt;
        PART_AddDetectedModels.Content = image || EditingChatGpt ? "添加选中模型" : "添加并获取价格";
        foreach (var model in _providerModels)
        {
            model.IsImageProvider = image;
            model.CanChooseImageFormat = !geminiImage;
        }
        UpdateChatGptUi();
        if (geminiImage) PART_ProviderApiPathField.IsVisible = false;
        UpdateProviderBuiltinSearchUi();
    }

    private void OnProviderBuiltinSearchChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingProviderForm || _updatingSearchControls) return;
        _editingProviderBuiltinSearch = PART_ProviderBuiltinSearch.IsChecked == true;
        UpdateProviderBuiltinSearchUi();
    }

    private void UpdateProviderBuiltinSearchUi()
    {
        var image = _editingProviderPurpose == "image";
        var vendor = !image && !EditingChatGpt
            ? BuiltinWebSearch.DetectVendor(PART_ProviderBaseUrl.Text)
            : null;
        PART_ProviderToolsSection.IsVisible = vendor is not null;
        var protoSupported = vendor is { } v && BuiltinWebSearch.IsSupportedProviderType(v, EditingProviderType);
        if (vendor is { } detected)
        {
            PART_ProviderBuiltinSearchHint.Text = BuiltinWebSearch.DescribeToolHint(detected);
            PART_ProviderBuiltinSearchNeed.IsVisible = !protoSupported;
            PART_ProviderBuiltinSearchNeedText.Text = protoSupported
                ? string.Empty
                : BuiltinWebSearch.DescribeProtocolRequirement(detected);
            _updatingSearchControls = true;
            try
            {
                PART_ProviderBuiltinSearch.IsEnabled = protoSupported;
                PART_ProviderBuiltinSearch.IsChecked = protoSupported && _editingProviderBuiltinSearch;
            }
            finally
            {
                _updatingSearchControls = false;
            }
        }

        var providerActive = protoSupported && _editingProviderBuiltinSearch;
        foreach (var model in _providerModels)
            model.SetBuiltinSearchContext(vendor, EditingProviderType, providerActive);
    }

    private void OnProviderTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingProviderForm) return;
        var known = new[]
        {
            "v1/chat/completions", "chat/completions", "v1/messages", "v1/responses",
            "models/{model}:streamGenerateContent", "v1/images/generations", "v1/images"
        };
        var current = PART_ProviderApiPath.Text?.Trim() ?? string.Empty;
        if (current.Length == 0 || known.Contains(current, StringComparer.OrdinalIgnoreCase))
            PART_ProviderApiPath.Text = ProviderConnection.DefaultApiPath(_editingProviderPurpose, EditingProviderType, PART_ProviderBaseUrl.Text);
        var chatPath = PART_ProviderImageChatPath.Text?.Trim() ?? string.Empty;
        if (chatPath.Length == 0 || chatPath is "v1/chat/completions" or "models/{model}:generateContent")
            PART_ProviderImageChatPath.Text = ImageApiFormat.DefaultChatPath(EditingProviderType);
        UpdateProviderPurposeUi();
        UpdateProviderEndpointPreview();
    }

    private void OnProviderImageFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingProviderForm) return;
        UpdateProviderPurposeUi();
        UpdateProviderEndpointPreview();
    }

    private void OnProviderEndpointChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingProviderForm) return;
        var baseUrl = PART_ProviderBaseUrl.Text?.Trim() ?? string.Empty;
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            PART_ProviderHttps.IsChecked = false;
        else if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            PART_ProviderHttps.IsChecked = true;
        UpdateProviderBuiltinSearchUi();
        UpdateProviderEndpointPreview();
    }

    private void OnProviderHttpsChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingProviderForm) return;
        var baseUrl = PART_ProviderBaseUrl.Text ?? string.Empty;
        if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            PART_ProviderBaseUrl.Text = (PART_ProviderHttps.IsChecked == true ? "https://" : "http://") + baseUrl[8..];
        else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            PART_ProviderBaseUrl.Text = (PART_ProviderHttps.IsChecked == true ? "https://" : "http://") + baseUrl[7..];
        UpdateProviderBuiltinSearchUi();
        UpdateProviderEndpointPreview();
    }

    private void UpdateProviderEndpointPreview()
    {
        if (EditingChatGpt) return;
        var baseUrl = PART_ProviderBaseUrl.Text?.Trim() ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            PART_ProviderEndpointPreview.Text = string.Empty;
            return;
        }

        string Join(string? path, string fallback) =>
            baseUrl.TrimEnd('/') + "/" + (string.IsNullOrWhiteSpace(path) ? fallback : path.Trim()).TrimStart('/');

        if (_editingProviderPurpose != "image")
        {
            var fallback = ProviderConnection.DefaultApiPath(ProviderPurpose.Chat, EditingProviderType);
            var endpoint = Join(PART_ProviderApiPath.Text, fallback);
            PART_ProviderEndpointPreview.Text = string.Equals(EditingProviderType, "gemini", StringComparison.OrdinalIgnoreCase)
                ? "实际请求地址：" + ProviderConnection.AppendAltSse(endpoint)
                : "实际请求地址：" + endpoint;
            return;
        }

        var chat = Join(PART_ProviderImageChatPath.Text, ImageApiFormat.DefaultChatPath(EditingProviderType));
        if (EditingProviderType == "gemini") PART_ProviderEndpointPreview.Text = "请求地址：" + chat;
        else PART_ProviderEndpointPreview.Text = "图像地址：" + Join(PART_ProviderApiPath.Text, ImageApiFormat.DefaultImagesPath(baseUrl))
            + "\n对话地址：" + chat;
    }

    private async void OnDetectProviderModels(object? sender, RoutedEventArgs e)
    {
        if (!TryCollectProvider(out var entry)) return;

        PART_DetectProviderModels.IsEnabled = false;
        PART_ProviderSourceField.IsEnabled = false;
        PART_DetectProviderModels.Content = "获取中...";
        ShowProviderStatus("正在获取模型列表...");
        try
        {
            var models = await FetchProviderModelsAsync(entry);
            _detectedModels.Clear();
            var existing = _providerModels.Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var detectedVendor = !ProviderPurpose.IsImage(entry.Purpose) && entry.Type != ChatGptType
                ? BuiltinWebSearch.DetectVendor(entry.BaseUrl)
                : null;
            foreach (var model in models)
            {
                if (entry.Type == ChatGptType && model.EffortLevels is { Count: > 0 }
                    && _providerModels.FirstOrDefault(row => row.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase)) is { } row)
                {
                    row.EffortLevelsText = string.Join(", ", model.EffortLevels);
                    row.RefreshEffortLevelOptions();
                }
                if (ProviderPurpose.IsImage(entry.Purpose)
                    && _providerModels.FirstOrDefault(row => row.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase)) is { } imageRow
                    && imageRow.ImageFormatIndex == 0 && model.ImageFormat is not null)
                    imageRow.ImageFormatIndex = ImageApiFormat.IsChatImage(model.ImageFormat) ? 2 : 1;
                var alreadyExists = existing.Contains(model.Id);
                var defaultBuiltinSearch = detectedVendor is { } vendor
                    && BuiltinWebSearch.DefaultSupportsModel(vendor, model.Id, entry.Type);
                _detectedModels.Add(new DetectedModelRow(model, false, !alreadyExists,
                    alreadyExists ? "已存在" : string.Empty, defaultBuiltinSearch));
            }

            PART_DetectedModelsTitle.Text = $"检测到 {models.Count} 个模型";
            PART_DetectedModelSearch.Text = string.Empty;
            PART_SelectAllDetectedModels.IsChecked = false;
            PART_DetectedModelsPanel.IsVisible = true;
            RefreshDetectedModelFilter();
            ShowProviderStatus(models.Count == 0 ? "未获取到模型，请检查 API Key 或接入地址。" : "请选择要添加的模型。");
        }
        catch (Exception ex)
        {
            FailProviderEdit("获取失败：" + ex.Message);
        }
        finally
        {
            PART_DetectProviderModels.Content = "自动获取";
            PART_DetectProviderModels.IsEnabled = true;
            PART_ProviderSourceField.IsEnabled = true;
        }
    }

    private async void OnFetchEditingProviderPricing(object? sender, RoutedEventArgs e)
    {
        var rows = _providerModels.Where(row => !string.IsNullOrWhiteSpace(row.Id)).ToList();
        if (rows.Count == 0)
        {
            ShowProviderStatus("请先添加模型。");
            return;
        }

        PART_FetchEditingProviderPricing.IsEnabled = false;
        PART_FetchEditingProviderPricing.Content = "获取中...";
        try
        {
            var found = await FetchPricingForRowsAsync(rows);
            ShowProviderStatus(found == 0
                ? "未找到这些模型的公开价格。"
                : $"已获取 {found} 个模型的价格，可在模型中选择报价来源。");
        }
        catch (Exception ex)
        {
            FailProviderEdit("获取价格失败：" + ex.Message);
        }
        finally
        {
            PART_FetchEditingProviderPricing.Content = "获取价格";
            PART_FetchEditingProviderPricing.IsEnabled = true;
        }
    }

    private async void OnFetchModelPricing(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModelRow row } button) return;
        if (string.IsNullOrWhiteSpace(row.Id))
        {
            ShowProviderStatus("请先填写模型 ID。");
            return;
        }

        button.IsEnabled = false;
        button.Content = "获取中...";
        try
        {
            var found = await FetchPricingForRowsAsync([row]);
            ShowProviderStatus(found == 0 ? "未找到该模型的公开价格。" : "已获取模型价格。");
        }
        catch (Exception ex)
        {
            FailProviderEdit("获取价格失败：" + ex.Message);
        }
        finally
        {
            button.Content = "获取价格";
            button.IsEnabled = true;
        }
    }

    private async Task<int> FetchPricingForRowsAsync(IReadOnlyCollection<ModelRow> rows)
    {
        var catalog = _modelsDevCatalog ??= new ModelsDevCatalog(_byokHttpFactory);
        var providers = await catalog.LoadAsync(forceRefresh: false);
        var guessedProvider = ModelsDevCatalog.GuessProviderKey(PART_ProviderBaseUrl.Text);
        var found = 0;

        foreach (var row in rows)
        {
            var modelId = row.Id.Trim();
            var candidates = providers
                .Where(provider => provider.Models.TryGetValue(modelId, out _))
                .Select(provider => new PricingCandidate(
                    provider.Key,
                    provider.Name,
                    provider.Models[modelId]))
                .ToList();

            if (candidates.Count == 0)
            {
                row.SetPricingCandidates([], null);
                continue;
            }

            var current = row.Pricing();
            PricingCandidate? preferred = null;
            if (current is not { IsManual: true }
                && !string.Equals(current?.Source, ModelPricing.SourceEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                preferred = candidates.FirstOrDefault(candidate =>
                                candidate.ProviderKey.Equals(row.LoadedPricing?.ModelsDevProviderKey, StringComparison.OrdinalIgnoreCase))
                            ?? candidates.FirstOrDefault(candidate => SameRates(candidate.Pricing, current))
                            ?? candidates.FirstOrDefault(candidate =>
                                candidate.ProviderKey.Equals(guessedProvider, StringComparison.OrdinalIgnoreCase))
                            ?? candidates[0];
            }

            row.SetPricingCandidates(candidates, preferred);
            found++;
        }

        return found;
    }

    private static bool SameRates(ModelPricing left, ModelPricing? right) =>
        right is not null
        && left.Input == right.Input
        && left.Output == right.Output
        && left.CacheRead == right.CacheRead
        && left.CacheWrite == right.CacheWrite;

    /// <summary>
    /// Prices every model of every chat provider in one pass.
    ///
    /// One button rather than one per provider, because the catalogue is one
    /// download and the question it raises — which of several quoted prices applies —
    /// is the same question across all of them. Everything the catalogue can settle
    /// on its own is written immediately; only genuine disagreements are reported,
    /// and those are settled per model in <see cref="ModelPricingWindow"/>.
    /// </summary>
    private async void OnFetchAllPricing(object? sender, RoutedEventArgs e)
    {
        var catalog = _modelsDevCatalog ??= new ModelsDevCatalog(_byokHttpFactory);

        PART_FetchAllPricing.IsEnabled = false;
        PART_FetchAllPricing.Content = "获取中...";
        try
        {
            var catalogProviders = await catalog.LoadAsync(forceRefresh: false);
            if (catalogProviders.Count == 0)
            {
                ShowPricingResult("未能获取 models.dev 的定价数据。", conflicts: 0);
                return;
            }

            var targets = _settings.Providers
                .Where(provider => !ProviderPurpose.IsImage(provider.Purpose) && provider.Type != ChatGptType)
                .ToList();
            var match = ModelsDevCatalog.Match(catalogProviders,
                targets.SelectMany(provider => provider.Models).Select(model => model.Id));

            var outcome = ApplyPricing(targets, _ => match.Agreed);
            var targetIds = targets.Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal);
            _pendingPricing = (match, targetIds);
            var pendingRows = BuildPricingRows(match,
                _settings.Providers.Where(provider => targetIds.Contains(provider.Id)));
            var missing = pendingRows.Count(row => row.Candidates.Count == 0);
            var conflicts = pendingRows.Count - missing;

            var parts = new List<string>();
            if (outcome.Written > 0) parts.Add($"已自动保存 {outcome.Written} 个模型的价格");
            if (outcome.Kept > 0) parts.Add($"{outcome.Kept} 项保留手动价格");
            if (outcome.Unavailable > 0) parts.Add($"{outcome.Unavailable} 个服务暂不可用");
            if (missing > 0) parts.Add($"{missing} 项价格需手动填写");
            if (conflicts > 0) parts.Add($"还有 {conflicts} 项在多个来源之间的价格不一致");
            if (parts.Count == 0) parts.Add("模型价格已是最新");

            ShowPricingResult(string.Join("，", parts) + "。", pendingRows.Count);
        }
        catch (Exception ex)
        {
            ShowPricingResult("获取定价失败：" + ex.Message, conflicts: 0);
        }
        finally
        {
            PART_FetchAllPricing.Content = "获取模型价格";
            PART_FetchAllPricing.IsEnabled = true;
        }
    }

    /// <summary>
    /// Writes prices into the stored providers and persists them.
    ///
    /// Hand-entered prices are skipped: a user who typed a number meant it, and a
    /// refresh that silently replaced it would make the field pointless.
    /// </summary>
    /// <param name="pricesFor">The prices to write for a given provider, keyed by
    /// model id — the same map for every provider on the automatic pass, a
    /// provider-specific one once the user has resolved conflicts.</param>
    private PricingOutcome ApplyPricing(
        IReadOnlyList<ProviderEntry> targets,
        Func<ProviderEntry, IReadOnlyDictionary<string, ModelPricing>> pricesFor)
    {
        var written = 0;
        var kept = 0;
        var updates = new List<ProviderEntry>();
        foreach (var provider in targets)
        {
            var prices = pricesFor(provider);
            if (prices.Count == 0) continue;

            var models = new List<ProviderModelEntry>(provider.Models.Count);
            var touched = false;
            foreach (var model in provider.Models)
            {
                if (!prices.TryGetValue(model.Id.Trim(), out var pricing)) { models.Add(model); continue; }
                if (model.Pricing is { IsManual: true }) { models.Add(model); kept++; continue; }
                if (model.Pricing is { IsModelsDev: true } current && SameRates(current, pricing))
                {
                    models.Add(model);
                    continue;
                }
                models.Add(model with { Pricing = pricing });
                written++;
                touched = true;
            }
            if (touched) updates.Add(provider with { Models = models });
        }

        var unavailable = updates.Count == 0 ? 0 : _providerRestorer.Save(updates).Unavailable;
        return new PricingOutcome(written, kept, unavailable);
    }

    private readonly record struct PricingOutcome(int Written, int Kept, int Unavailable);

    private void ShowPricingResult(string text, int conflicts)
    {
        PART_PricingResultText.Text = text;
        PART_ResolvePricingConflicts.IsVisible = conflicts > 0;
        PART_ResolvePricingConflicts.Content = $"完善价格（{conflicts}）";
        PART_PricingResult.IsVisible = true;
    }

    /// <summary>
    /// Opens the per-model picker for the disagreements the automatic pass left.
    /// A row is created per provider that serves the model, because the same id
    /// under two endpoints can legitimately settle on two different prices.
    /// </summary>
    private async void OnResolvePricingConflicts(object? sender, RoutedEventArgs e)
    {
        if (_pendingPricing is not { } pending) return;

        var rows = BuildPricingRows(pending.Match,
            _settings.Providers.Where(provider => pending.ProviderIds.Contains(provider.Id)));
        if (rows.Count == 0) return;

        var window = new ModelPricingWindow(rows);
        await window.ShowDialog(this);
        if (window.Result is not { } picks) return;

        try
        {
            var outcome = ApplyPricing(_settings.Providers.Where(provider => picks.ContainsKey(provider.Id)).ToArray(),
                provider => picks[provider.Id]);
            _pendingPricing = null;
            var message = $"已按所选来源写入 {outcome.Written} 个模型的价格。";
            if (outcome.Unavailable > 0) message += $"其中 {outcome.Unavailable} 个服务暂不可用。";
            ShowPricingResult(message, conflicts: 0);
        }
        catch (Exception ex)
        {
            ShowPricingResult("保存定价失败：" + ex.Message, rows.Count);
        }
    }

    private static List<PricingConflictRow> BuildPricingRows(
        PricingMatch match,
        IEnumerable<ProviderEntry> providers)
    {
        var rows = new List<PricingConflictRow>();
        var unmatched = match.Unmatched.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var guess = ModelsDevCatalog.GuessProviderKey(provider.BaseUrl);
            foreach (var model in provider.Models)
            {
                if (model.Pricing is { IsManual: true }) continue;
                var modelId = model.Id.Trim();
                IReadOnlyList<ModelsDevPrice> prices;
                if (match.Conflicts.TryGetValue(modelId, out var conflictPrices))
                {
                    prices = conflictPrices;

                    if (model.Pricing is { IsModelsDev: true } saved)
                    {
                        var resolved = saved.ModelsDevProviderKey is { } source
                            ? prices.Any(price => price.ProviderKey.Equals(source, StringComparison.OrdinalIgnoreCase)
                                                  && SameRates(price.Pricing, saved))
                            : prices.Any(price => SameRates(price.Pricing, saved));
                        if (resolved) continue;
                    }
                }
                else
                {
                    // models.dev has no quote for this id. An endpoint-supplied price
                    // is already usable, so only models without any stored price need
                    // a manual row here.
                    if (!unmatched.Contains(modelId) || model.Pricing is not null) continue;
                    prices = [];
                }

                var candidates = prices
                    .Select(price => new PricingCandidate(price.ProviderKey, price.ProviderName, price.Pricing))
                    .ToList();
                var preferred = candidates.FirstOrDefault(candidate =>
                    candidate.ProviderKey.Equals(guess, StringComparison.OrdinalIgnoreCase));
                rows.Add(new PricingConflictRow(provider.Id, provider.Name, modelId, candidates, preferred));
            }
        }
        return rows;
    }

    private void RefreshDetectedModelFilter()
    {
        var query = PART_DetectedModelSearch.Text?.Trim() ?? string.Empty;
        var visible = _detectedModels
            .Where(item => query.Length == 0
                           || item.Entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || item.Entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || item.CapabilitySummary.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        PART_DetectedModels.ItemsSource = visible;
        PART_NoDetectedModels.IsVisible = visible.Count == 0;
    }

    private void OnSelectAllDetectedModels(object? sender, RoutedEventArgs e)
    {
        var selected = PART_SelectAllDetectedModels.IsChecked == true;
        foreach (var item in PART_DetectedModels.ItemsSource?.Cast<DetectedModelRow>() ?? [])
            if (item.IsEnabled) item.IsSelected = selected;
    }

    private async void OnAddDetectedModels(object? sender, RoutedEventArgs e)
    {
        var selected = _detectedModels.Where(item => item.IsEnabled && item.IsSelected).ToList();
        var added = selected.Select(item => CreateModelRow(item.Entry)).ToList();
        if (added.Count == 1) added[0].IsExpanded = true;
        foreach (var row in added) _providerModels.Add(row);

        var priced = 0;
        if (_editingProviderPurpose != "image" && !EditingChatGpt && added.Count > 0)
        {
            PART_AddDetectedModels.IsEnabled = false;
            PART_AddDetectedModels.Content = "获取价格中...";
            try
            {
                priced = await FetchPricingForRowsAsync(added);
            }
            catch (Exception ex)
            {
                CloseDetectedModels();
                FailProviderEdit("模型已添加，获取价格失败：" + ex.Message);
                return;
            }
            finally
            {
                PART_AddDetectedModels.IsEnabled = true;
                PART_AddDetectedModels.Content = "添加并获取价格";
            }
        }

        CloseDetectedModels();
        ShowProviderStatus(selected.Count == 0
            ? "未选择模型。"
            : _editingProviderPurpose == "image" || EditingChatGpt
                ? $"已添加 {selected.Count} 个模型，保存后生效。"
            : priced > 0
                ? $"已添加 {selected.Count} 个模型，并获取 {priced} 个模型的价格，保存后生效。"
                : $"已添加 {selected.Count} 个模型，未找到公开价格，保存后生效。");
    }

    private void CloseDetectedModels()
    {
        PART_DetectedModelsPanel.IsVisible = false;
        _detectedModels.Clear();
        PART_DetectedModels.ItemsSource = null;
    }

    private async void OnTestProvider(object? sender, RoutedEventArgs e)
    {
        if (!TryCollectProvider(out var entry)) return;
        PART_TestProvider.IsEnabled = false;
        PART_TestProvider.Content = "测试中...";
        ShowProviderStatus("正在测试连接...");
        try
        {
            if (ProviderPurpose.IsImage(entry.Purpose))
            {
                var options = new ImageGenerationOptions(
                    Enabled: true,
                    BaseUrl: entry.BaseUrl,
                    ApiKey: entry.ApiKey,
                    Model: entry.Models.FirstOrDefault()?.Id,
                    Size: "1024x1024",
                    Style: null,
                    AsTool: false,
                    SupportsEdit: false,
                    Format: entry.Models.FirstOrDefault()?.ImageFormat ?? entry.ImageFormat,
                    GenerationPath: entry.ApiPath,
                    EditPath: entry.ImageEditPath,
                    ProviderType: entry.Type,
                    ChatPath: entry.ImageChatPath,
                    CustomHeaders: entry.CustomHeaders?.ToDictionary(h => h.Name, h => h.Value));
                var images = await new ImageGenerationTool(_byokHttpFactory).GenerateAsync(
                    options, "a single small red dot on a white background", CancellationToken.None);
                ShowProviderStatus(images.Count > 0 ? "连接成功，图像服务可用。" : "连接成功，但未返回图片。");
                return;
            }

            await _providerConnection.TestChatAsync(entry);
            ShowProviderStatus("连接正常。");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            FailProviderEdit(ex.Message);
        }
        catch (Exception ex)
        {
            FailProviderEdit(ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            PART_TestProvider.Content = "测试连接";
            PART_TestProvider.IsEnabled = true;
        }
    }

    private Task<List<ProviderModelEntry>> FetchProviderModelsAsync(ProviderEntry entry) =>
        entry.Type == ChatGptType
            ? FetchChatGptModelsAsync()
            : _providerConnection.FetchModelsAsync(entry, FindProviderPreset(entry)?.ModelsPath);

    private void OnSettingsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            FocusSearch();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape || !HasPageContent(PAGE_Providers)) return;
        if (PART_DetectedModelsPanel.IsVisible)
        {
            CloseDetectedModels();
            e.Handled = true;
        }
        else if (PART_ProviderEditor.IsVisible)
        {
            CloseProviderEditor();
            e.Handled = true;
        }
    }

    // ---- MCP servers -------------------------------------------------------

    private void RefreshMcpServers()
    {
        if (HasPageContent(PAGE_Mcp)) PART_NoMcpServers.IsVisible = _settings.McpServers.Count == 0;
    }

    private void OnEditMcp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: McpServerEntry entry }) EditMcp(entry);
    }

    private void EditMcp(McpServerEntry? entry)
    {
        _editingMcpServer = entry;
        PART_McpEditorTitle.Text = entry is null ? "添加 MCP 服务器" : $"编辑「{entry.Name}」";
        PART_McpName.Text = entry?.Name ?? string.Empty;
        PART_McpTransport.SelectedIndex = entry?.IsStdio == true ? 1 : 0;
        PART_McpCommand.Text = entry?.Command ?? string.Empty;
        PART_McpArguments.Text = string.Join(Environment.NewLine, entry?.Arguments ?? []);
        PART_McpWorkingDirectory.Text = entry?.WorkingDirectory ?? string.Empty;
        PART_McpEnvironment.Text = string.Join(Environment.NewLine,
            (entry?.Environment ?? new Dictionary<string, string>()).Select(pair => $"{pair.Key}={pair.Value}"));
        PART_McpExposure.SelectedIndex = McpExposures.Normalize(entry?.Exposure) switch
        {
            McpExposures.Deferred => 1,
            McpExposures.Direct => 2,
            _ => 0
        };
        ShowMcpTransportFields();
        PART_McpUrl.Text = entry?.Url ?? string.Empty;
        PART_McpHeader.Text = string.IsNullOrWhiteSpace(entry?.HeaderName) ? "Authorization" : entry.HeaderName;
        PART_McpToken.Text = entry?.Token ?? string.Empty;
        PART_McpToken.PasswordChar = '•';
        PART_McpEnabled.IsChecked = entry?.Enabled ?? true;
        PART_McpStatus.Text = string.Empty;
        PART_McpError.IsVisible = false;
        PART_TestMcp.IsEnabled = _mcp is not null;
        PART_McpOverview.IsVisible = false;
        PART_McpEditor.IsVisible = true;
        PART_McpName.Focus();
    }

    private void CloseMcpEditor()
    {
        PART_McpEditor.IsVisible = false;
        PART_McpOverview.IsVisible = true;
        _editingMcpServer = null;
        RefreshMcpServers();
    }

    private void ShowMcpTransportFields()
    {
        var stdio = PART_McpTransport.SelectedIndex == 1;
        PART_McpStdioFields.IsVisible = stdio;
        PART_McpHttpFields.IsVisible = !stdio;
    }

    private bool TryBuildMcpEntry(out McpServerEntry entry)
    {
        entry = default!;
        var stdio = PART_McpTransport.SelectedIndex == 1;
        var url = PART_McpUrl.Text?.Trim() ?? string.Empty;
        string? command = null;
        IReadOnlyList<string>? arguments = null;
        IReadOnlyDictionary<string, string>? environment = null;

        if (stdio)
        {
            command = PART_McpCommand.Text?.Trim();
            if (string.IsNullOrEmpty(command))
            {
                FailMcpEdit("请填写命令。");
                return false;
            }

            arguments = Lines(PART_McpArguments.Text).ToArray();
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Lines(PART_McpEnvironment.Text))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    FailMcpEdit($"环境变量格式无效：{line}");
                    return false;
                }
                variables[line[..separator].Trim()] = line[(separator + 1)..];
            }
            environment = variables.Count > 0 ? variables : null;
        }
        else
        {
            if (url.Length == 0)
            {
                FailMcpEdit("请填写服务器地址。");
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                FailMcpEdit("地址必须以 http:// 或 https:// 开头。");
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            {
                FailMcpEdit("远程地址必须使用 https://，仅本地服务器可用 http://。");
                return false;
            }
        }

        var name = string.IsNullOrWhiteSpace(PART_McpName.Text) ? "MCP Server" : PART_McpName.Text.Trim();
        var header = string.IsNullOrWhiteSpace(PART_McpHeader.Text) ? "Authorization" : PART_McpHeader.Text.Trim();
        var token = string.IsNullOrWhiteSpace(PART_McpToken.Text) ? null : PART_McpToken.Text.Trim();
        var workingDirectory = PART_McpWorkingDirectory.Text?.Trim();
        entry = new McpServerEntry(
            _editingMcpServer?.Id ?? Guid.NewGuid().ToString("N"),
            name,
            stdio ? string.Empty : url,
            stdio ? McpTransports.Stdio : McpTransports.Http,
            header,
            stdio ? null : token,
            PART_McpEnabled.IsChecked == true,
            command,
            arguments,
            stdio && !string.IsNullOrEmpty(workingDirectory) ? workingDirectory : null,
            environment,
            PART_McpExposure.SelectedIndex switch
            {
                1 => McpExposures.Deferred,
                2 => McpExposures.Direct,
                _ => McpExposures.Codemode
            });
        return true;
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty)
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

    private void SaveMcp()
    {
        if (!TryBuildMcpEntry(out var entry)) return;
        _settings.UpsertMcpServer(entry);
        // An edited configuration reconnects on its next use; a disabled server
        // is closed now so a stdio process does not keep running.
        if (!entry.Enabled) _ = _mcp?.CloseAsync(entry.Id);
        CloseMcpEditor();
    }

    private async void OnDeleteMcp(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: McpServerEntry entry }) return;
        if (!await Confirm.AskAsync(this, $"删除「{entry.Name}」？", "该服务器的 Token 与环境变量会一并移除。", "删除"))
            return;

        _settings.DeleteMcpServer(entry);
        _ = _mcp?.CloseAsync(entry.Id);
        RefreshMcpServers();
    }

    private async void OnTestMcp(object? sender, RoutedEventArgs e)
    {
        if (_mcp is null || !TryBuildMcpEntry(out var entry)) return;

        PART_TestMcp.IsEnabled = false;
        PART_TestMcp.Content = "连接中...";
        PART_McpStatus.Text = "正在连接...";
        PART_McpError.IsVisible = false;
        try
        {
            // A stdio server started through npx or uvx may download its package first.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(entry.IsStdio ? 90 : 15));
            var options = new McpServerOptions(
                entry.Id, entry.Name, entry.Url, entry.Transport,
                entry.HeaderName, entry.Token, entry.Enabled,
                entry.Command, entry.Arguments, entry.WorkingDirectory, entry.Environment, entry.Exposure);
            var listing = await _mcp.ProbeAsync(options, cts.Token);
            PART_McpStatus.Text = $"连接成功，发现 {listing.Tools.Count} 个工具。";
        }
        catch (OperationCanceledException)
        {
            FailMcpEdit("连接超时。");
        }
        catch (Exception ex)
        {
            FailMcpEdit($"连接失败：{ex.Message}");
        }
        finally
        {
            PART_TestMcp.Content = "测试连接";
            PART_TestMcp.IsEnabled = true;
        }
    }

    private void FailMcpEdit(string message)
    {
        PART_McpStatus.Text = string.Empty;
        PART_McpError.Text = message;
        PART_McpError.IsVisible = true;
    }

    // ---- sandbox -----------------------------------------------------------

    /// <summary>
    /// Paints the runtime rows in two passes. Everything the stamp files answer
    /// goes up immediately; only the disk-usage suffix waits for the scan. That
    /// scan walks ~15k files and takes seconds even warm, and awaiting it before
    /// the first assignment left the whole card blank for as long as it ran —
    /// these six TextBlocks have no text until this method gives them one.
    /// </summary>
    private async Task RefreshRuntimeStatusAsync(bool rescanStorage = false)
    {
        if (!HasPageContent(PAGE_Sandbox)) return;
        if (_pythonRuntime is null || _piSidecar is null)
        {
            PART_SandboxStatus.Text = "运行环境不可用";
            PART_SandboxDetail.Text = string.Empty;
            PART_PythonRuntimeStatus.Text = "Python 运行环境不可用";
            PART_PythonRuntimeDetail.Text = string.Empty;
            PART_PiSidecarStatus.Text = "Agent 运行环境不可用";
            PART_PiSidecarDetail.Text = string.Empty;
            return;
        }

        if (_sandboxOperationRunning) return;
        var generation = ++_runtimeStatusGeneration;

        var python = _pythonRuntime.GetInstalledRuntime();
        var installedSidecar = _piSidecar.GetInstalled();
        var sidecar = _piSidecar.GetCompatibleInstalled();

        PART_SandboxStatus.Text = (python is not null, sidecar is not null) switch
        {
            (true, true) => "运行环境已就绪",
            (false, false) => "尚未配置运行环境",
            _ => "运行环境部分就绪"
        };
        PART_SandboxDetail.Text = (python is not null, sidecar is not null) switch
        {
            (true, true) => "Python、Work 与 BYOK 可用",
            (true, false) => "Python 可用",
            (false, true) => "Work 与 BYOK 可用",
            _ => "按需配置所需环境"
        };
        PART_ConfigureSandbox.Content = python is not null && sidecar is not null
            ? "检查更新"
            : installedSidecar is not null && sidecar is null ? "更新" : "一键配置";

        if (python is null) ClearMissingManagedPythonPath();

        if (sidecar is null)
        {
            PART_PiSidecarStatus.Text = installedSidecar is null
                ? "Agent 运行环境未下载"
                : "Agent 运行环境需要更新";
            PART_PiSidecarDetail.Text = installedSidecar is null
                ? "Work 与 BYOK 需要此环境"
                : "更新后可使用 Work 与 BYOK";
            PART_RemovePiSidecar.IsVisible = installedSidecar is not null;
        }
        else
        {
            PART_PiSidecarStatus.Text = $"Agent 运行环境 · {sidecar.Version}";
            PART_PiSidecarDetail.Text = "Work 与 BYOK 可用";
            PART_RemovePiSidecar.IsVisible = true;
        }

        var cached = _storageScan is { IsCompletedSuccessfully: true } done && !rescanStorage
            ? done.Result
            : null;
        ApplyPythonRuntimeRow(python, cached);
        PART_ResetPythonRuntime.IsVisible = python is not null || cached is { TotalBytes: > 0 };
        RefreshPythonBrowseButton();
        if (cached is not null) return;

        // A failed scan must not stay in the field, or it would rethrow on
        // every later refresh instead of being retried.
        if (rescanStorage || _storageScan is { IsFaulted: true } or { IsCanceled: true })
            _storageScan = null;
        var storage = await (_storageScan ??= Task.Run(_pythonRuntime.GetStorageUsage));
        if (generation != _runtimeStatusGeneration || _sandboxOperationRunning) return;

        ApplyPythonRuntimeRow(python, storage);
        PART_ResetPythonRuntime.IsVisible = python is not null || storage.TotalBytes > 0;
    }

    /// <summary>Writes the Python row. <paramref name="storage"/> is null while
    /// the disk scan is still running — the line reads fine without it, so the
    /// size just appears a moment later instead of holding the row hostage.</summary>
    private void ApplyPythonRuntimeRow(InstalledPythonRuntime? python, PythonRuntimeStorageUsage? storage)
    {
        var storageText = storage is null
            ? null
            : $"占用 {FormatBytes(storage.TotalBytes)} · 运行时 {FormatBytes(storage.RuntimeBytes)} · 会话依赖 {FormatBytes(storage.SessionEnvironmentBytes)}";

        var details = new List<string>();
        if (python is null)
        {
            var external = NormalizedPythonPath();
            if (!string.IsNullOrWhiteSpace(external) && File.Exists(external))
            {
                PART_PythonRuntimeStatus.Text = "外部 Python（无基础环境隔离）";
                details.Add(external);
            }
            else
            {
                PART_PythonRuntimeStatus.Text = "尚未配置专属 Python";
                details.Add("点击「一键配置」下载 MolaGPT 托管版本，或在下方选择外部解释器");
            }
        }
        else
        {
            var packages = python.Packages.Count == 0 ? string.Empty : string.Join(", ", python.Packages.Take(8));
            if (packages.Length > 0) details.Add(packages);
            if (!string.Equals(NormalizedPythonPath(), python.PythonExecutablePath, StringComparison.OrdinalIgnoreCase))
                details.Add("当前未选择此解释器");
            PART_PythonRuntimeStatus.Text = $"Python {python.Version} · {python.Runtime}";
        }

        if (storageText is not null) details.Add(storageText);
        PART_PythonRuntimeDetail.Text = string.Join(" · ", details);
    }

    private void ClearMissingManagedPythonPath()
    {
        if (_pythonRuntime is null) return;
        var configured = NormalizedPythonPath();
        if (_pythonRuntime.IsManagedInterpreterPath(configured)
            && !string.IsNullOrWhiteSpace(configured)
            && !File.Exists(configured))
        {
            _settings.PythonToolExecutablePath = string.Empty;
            _settings.PythonToolEnabled = false;
        }
    }

    private async void OnConfigureSandbox(object? sender, RoutedEventArgs e)
    {
        if (_pythonRuntime is null || _piSidecar is null) return;

        PART_ConfigureSandbox.IsEnabled = false;
        PART_ResetPythonRuntime.IsEnabled = false;
        PART_RemovePiSidecar.IsEnabled = false;
        PART_ConfigureSandbox.Content = "配置中...";
        BeginSandboxOperation();
        try
        {
            await ConfigurePythonRuntimeAsync();
            await ConfigurePiSidecarAsync();
        }
        finally
        {
            PART_ConfigureSandbox.IsEnabled = true;
            PART_ResetPythonRuntime.IsEnabled = true;
            PART_RemovePiSidecar.IsEnabled = true;
            EndSandboxOperation();
            await RefreshRuntimeStatusAsync(rescanStorage: true);
        }
    }

    /// <summary>Hands the runtime rows to a download / reset / probe: refreshes
    /// stand down, and a scan already in flight loses its right to repaint.</summary>
    private void BeginSandboxOperation()
    {
        _sandboxOperationRunning = true;
        _runtimeStatusGeneration++;
    }

    private void EndSandboxOperation()
    {
        _sandboxOperationRunning = false;
        _runtimeStatusGeneration++;
    }

    private async Task ConfigurePythonRuntimeAsync()
    {
        if (_pythonSetup is null) return;

        PART_PythonRuntimeStatus.Text = "正在准备 MolaGPT 专用 Python 环境...";
        var progress = new Progress<PythonRuntimeProgress>(item =>
            PART_PythonRuntimeStatus.Text = string.IsNullOrWhiteSpace(item.Message)
                ? $"正在配置 Python 运行时 {item.Progress:P0}"
                : item.Message);
        var result = await _pythonSetup.InstallAsync(progress);
        if (result.Error is not null)
            PART_PythonRuntimeStatus.Text = "配置失败：" + result.Error;
    }

    private async Task ConfigurePiSidecarAsync()
    {
        var progress = new Progress<SandboxProgress>(item =>
            PART_PiSidecarStatus.Text = string.IsNullOrWhiteSpace(item.Message)
                ? $"正在配置 Agent 运行环境 {item.Fraction:P0}"
                : item.Message);
        var (installed, error) = await _agentRuntime.InstallAsync(progress);
        PART_PiSidecarStatus.Text = installed is not null
            ? $"Agent 运行环境 · {installed.Version}"
            : "配置失败：" + error;
    }

    private async void OnBrowsePython(object? sender, RoutedEventArgs e)
    {
        if (_pythonRuntime is null || StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Python 解释器",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Python 解释器") { Patterns = ["python.exe", "python*.exe"] },
                new FilePickerFileType("可执行文件") { Patterns = ["*.exe"] }
            ]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { Length: > 0 } picked) return;

        PART_BrowsePython.IsEnabled = false;
        BeginSandboxOperation();
        PART_PythonRuntimeStatus.Text = "正在校验所选 Python...";
        try
        {
            var version = await ProbePythonVersionAsync(picked, CancellationToken.None);
            if (version is null)
            {
                PART_PythonRuntimeStatus.Text = "无法运行所选文件，请确认它是有效的 python.exe。";
                return;
            }

            _settings.PythonToolExecutablePath = picked;
            _settings.PythonToolEnabled = true;
            PART_PythonRuntimeStatus.Text = _pythonRuntime.IsManagedInterpreterPath(picked)
                ? $"已选择 {version}：{picked}"
                : $"已选择外部 {version}：{picked}。该解释器不受 MolaGPT 基础环境隔离保护。";
            _notifications?.Success("Python 解释器已就绪", key: PythonRuntimeNotificationKey);
        }
        catch (Exception ex)
        {
            PART_PythonRuntimeStatus.Text = "校验失败：" + ex.Message;
        }
        finally
        {
            PART_BrowsePython.IsEnabled = true;
            EndSandboxOperation();
            RefreshPythonBrowseButton();
        }
    }

    private async void OnResetPythonRuntime(object? sender, RoutedEventArgs e)
    {
        if (_pythonRuntime is null) return;
        if (!await Confirm.AskAsync(
                this,
                "重置 Python 环境？",
                "将删除一键配置的 Python、下载缓存和会话依赖缓存；用户生成的文档、表格、图片等产物会保留。",
                "重置"))
        {
            return;
        }

        BeginSandboxOperation();
        try
        {
            var managed = _pythonRuntime.IsManagedInterpreterPath(_settings.PythonToolExecutablePath);
            _pythonRuntime.DeleteRuntime();
            if (managed)
            {
                _settings.PythonToolExecutablePath = string.Empty;
                _settings.PythonToolEnabled = false;
            }
            _notifications?.Success("Python 环境已重置", key: PythonRuntimeNotificationKey);
        }
        catch (Exception ex)
        {
            PART_PythonRuntimeStatus.Text = "重置失败：" + ex.Message;
            _notifications?.Error("Python 环境重置失败", ex.Message, PythonRuntimeNotificationKey);
        }
        EndSandboxOperation();
        await RefreshRuntimeStatusAsync(rescanStorage: true);
    }

    private async void OnRemovePiSidecar(object? sender, RoutedEventArgs e)
    {
        if (_piSidecar is null) return;
        if (!await Confirm.AskAsync(
                this,
                "移除 Agent 运行环境？",
                "Work 与 BYOK 将暂时不可用。",
                "移除"))
        {
            return;
        }

        BeginSandboxOperation();
        try
        {
            _agentRuntime.Remove();
            _notifications?.Success("Agent 运行环境已移除", key: AgentRuntimeSetup.NotificationKey);
        }
        catch (Exception ex)
        {
            PART_PiSidecarStatus.Text = "移除失败：" + ex.Message;
            _notifications?.Error("Agent 运行环境移除失败", ex.Message, AgentRuntimeSetup.NotificationKey);
        }
        EndSandboxOperation();
        await RefreshRuntimeStatusAsync();
    }

    private string? NormalizedPythonPath() =>
        _settings.PythonToolExecutablePath?.Trim().Trim('"');

    /// <summary>Whether any enabled tool can open a SKILL.md — without one the
    /// 技能 page explains why skills will not run.</summary>
    private bool CanReachSkills() =>
        _settings.PythonToolEnabled || _settings.FileToolsEnabled;

    private void OnOpenSandboxFromSkills(object? sender, RoutedEventArgs e) => OpenSandboxPage();

    private void RefreshPythonBrowseButton()
    {
        if (!HasPageContent(PAGE_Sandbox)) return;
        var path = NormalizedPythonPath();
        PART_BrowsePython.Content = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? "重新选择..."
            : "浏览...";
    }

    private static async Task<string?> ProbePythonVersionAsync(string path, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        if (!process.Start()) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return null;
        }

        var output = ((await process.StandardOutput.ReadToEndAsync(ct))
            + (await process.StandardError.ReadToEndAsync(ct))).Trim();
        return process.ExitCode == 0 && output.StartsWith("Python", StringComparison.OrdinalIgnoreCase)
            ? output
            : null;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / 1024d / 1024d / 1024d:0.##} GB",
        >= 1024L * 1024L => $"{bytes / 1024d / 1024d:0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };

    // ---- skills ------------------------------------------------------------

    private void RefreshSkills()
    {
        if (!HasPageContent(PAGE_Skills)) return;
        _skills.Reload();
        // Two flat lists under two section headings, the way 模型服务 splits
        // 对话服务 from 图像服务 — it replaces the per-row 内置 / 自定义 badge,
        // and 删除 only ever appears in the half where it applies.
        var builtin = _skills.Skills.Where(skill => skill.IsBuiltin).ToArray();
        var user = _skills.Skills.Where(skill => !skill.IsBuiltin).ToArray();
        PART_BuiltinSkills.ItemsSource = builtin;
        PART_UserSkills.ItemsSource = user;
        PART_NoBuiltinSkills.IsVisible = builtin.Length == 0;
        PART_NoUserSkills.IsVisible = user.Length == 0;
    }

    private void SetSkillStatus(string? text)
    {
        PART_SkillStatus.Text = text ?? string.Empty;
        PART_SkillStatus.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async void OnImportSkill(object? sender, RoutedEventArgs e)
    {
        if (StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择技能压缩包",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("技能压缩包") { Patterns = ["*.zip"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { Length: > 0 } path) return;

        try
        {
            var name = _skills.ImportFromPath(path);
            RefreshSkills();
            SetSkillStatus($"已导入：{name}");
        }
        catch (Exception ex)
        {
            SetSkillStatus("导入失败：" + ex.Message);
        }
    }

    private void OnOpenSkillsDirectory(object? sender, RoutedEventArgs e)
    {
        _skills.EnsureUserDirectoryForImport();
        OpenWithShell(_skills.UserSkillsDirectory);
    }

    private void OnRefreshSkills(object? sender, RoutedEventArgs e)
    {
        RefreshSkills();
        SetSkillStatus(null);
    }

    /// <summary>The row's own skill, carried on the button's Tag — the rows are a
    /// plain ItemsControl now, so there is no selection to read it from.</summary>
    private static SkillItemViewModel? SkillFor(object? sender) =>
        (sender as Control)?.Tag as SkillItemViewModel;

    private void OnViewSkillMd(object? sender, RoutedEventArgs e)
    {
        if (SkillFor(sender) is not { } skill) return;
        if (!File.Exists(skill.SkillMdPath))
        {
            SetSkillStatus($"未找到「{skill.Name}」的 SKILL.md。");
            return;
        }
        RevealInExplorer(skill.SkillMdPath);
    }

    private async void OnDeleteSkill(object? sender, RoutedEventArgs e)
    {
        if (SkillFor(sender) is not { IsBuiltin: false } skill) return;
        if (!await Confirm.AskAsync(
                this,
                $"删除「{skill.Name}」？",
                "将永久删除此技能及其文件夹。",
                "删除"))
        {
            return;
        }

        try
        {
            _skills.DeleteUserSkill(skill);
            RefreshSkills();
            SetSkillStatus($"已删除：{skill.Name}");
        }
        catch (Exception ex)
        {
            SetSkillStatus("删除失败：" + ex.Message);
        }
    }

    private static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

    private static void RevealInExplorer(string path)
    {
        var start = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    // ---- personas ----------------------------------------------------------

    private void OnPersonaSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingPersonaForm) return;

        PersistEditingPersona();
        _editingPersonaIsDraft = false;
        _editingPersona = PART_PersonaList.SelectedItem as PersonaItemViewModel;
        LoadPersonaForm(_editingPersona);
    }

    private void LoadPersonaForm(PersonaItemViewModel? persona)
    {
        _loadingPersonaForm = true;
        try
        {
            var switched = !ReferenceEquals(PART_PersonaEditor.DataContext, persona ?? _emptyPersona);
            PART_PersonaEditor.IsVisible = persona is not null;
            PART_PersonaRail.IsVisible = persona is null;
            PART_PersonaEditorSurface.IsVisible = persona is not null;
            PART_PersonaBack.IsVisible = persona is not null;
            PART_PersonaLibraryActions.IsVisible = persona is null;
            PART_PersonaPageTitle.Text = persona is null ? "角色" : _editingPersonaIsDraft ? "新建角色" : "编辑角色";
            PART_PersonaFooter.IsVisible = persona is not null;
            PART_PersonaEditor.DataContext = persona ?? _emptyPersona;
            if (switched) PART_PersonaTabs.SelectedIndex = 0;

            if (persona is null)
            {
                PART_DuplicatePersona.IsEnabled = false;
                PART_DeletePersona.IsEnabled = false;
                PART_SavePersona.IsEnabled = false;
                return;
            }

            var editable = !persona.IsBuiltin;
            var isDraft = editable && _editingPersonaIsDraft;
            PART_PersonaName.IsEnabled = editable;
            PART_PersonaIcons.IsEnabled = editable;
            PART_PersonaPrompt.IsEnabled = editable;
            PART_PersonaVariables.IsVisible = editable;
            PART_PersonaDefaults.IsEnabled = editable;
            PART_PersonaSampling.IsEnabled = editable;
            PART_PersonaBuiltinHint.IsVisible = persona.IsBuiltin;
            PART_DuplicatePersona.IsEnabled = !isDraft;
            PART_DeletePersona.IsEnabled = editable && !isDraft;
            PART_SavePersona.IsEnabled = editable;
            PART_SavePersona.Content = isDraft ? "创建" : "保存";
            PART_PersonaMode.SelectedIndex = (int)persona.Mode;
            LoadRoleProfile();
        }
        finally
        {
            _loadingPersonaForm = false;
        }
    }

    private void OnNewPersona(object? sender, RoutedEventArgs e)
    {
        PersistEditingPersona();
        PART_PersonaSearch.Clear();
        _loadingPersonaForm = true;
        PART_PersonaList.SelectedItem = null;
        _loadingPersonaForm = false;

        _editingPersona = _personas.CreateBlankDraft();
        _editingPersonaIsDraft = true;
        LoadPersonaForm(_editingPersona);
        PART_PersonaName.Focus();
        PART_PersonaName.SelectAll();
    }

    private void OnDuplicatePersona(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersonaIsDraft) return;

        PersistEditingPersona();
        var copy = _personas.Duplicate(_editingPersona);
        RefreshPersonaLibrary();
        PART_PersonaList.SelectedItem = copy;
        PART_PersonaName.Focus();
        PART_PersonaName.SelectAll();
    }

    private async void OnDeletePersona(object? sender, RoutedEventArgs e)
    {
        var target = PART_PersonaList.SelectedItem as PersonaItemViewModel ?? _editingPersona;
        if (target is null) return;

        if (_editingPersonaIsDraft && ReferenceEquals(target, _editingPersona))
        {
            _editingPersona = null;
            _editingPersonaIsDraft = false;
            LoadPersonaForm(null);
            return;
        }

        if (target.IsBuiltin) return;
        if (!await Confirm.AskAsync(this, $"删除「{target.Name}」？", "这个角色会从角色选择器中移除。", "删除"))
            return;

        var next = _personas.Personas.FirstOrDefault(persona => persona.Id != target.Id);
        _loadingPersonaForm = true;
        try
        {
            PART_PersonaList.SelectedItem = null;
            _personas.Delete(target.Id);
            PART_PersonaList.SelectedItem = next;
        }
        finally
        {
            _loadingPersonaForm = false;
        }

        _editingPersona = null;
        _editingPersonaIsDraft = false;
        LoadPersonaForm(null);
        RefreshPersonaLibrary();
    }

    private void OnSavePersona(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersona.IsBuiltin) return;

        SyncPersonaForm();
        if (string.IsNullOrWhiteSpace(_editingPersona.Name)) _editingPersona.Name = "新角色";

        var saved = _editingPersona;
        _editingPersonaIsDraft = false;
        _personas.Save(saved);

        RefreshPersonaLibrary();
        _loadingPersonaForm = true;
        PART_PersonaList.SelectedItem = saved;
        _loadingPersonaForm = false;
        LoadPersonaForm(saved);
    }

    private void OnPersonaFormChanged(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _loadingPersonaForm || _editingPersonaIsDraft || _editingPersona.IsBuiltin)
            return;

        SyncPersonaForm();
        _personas.Save(_editingPersona);
    }

    private void OnPersonaIcon(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersona.IsBuiltin) return;
        if (sender is not Control { Tag: string glyph } || glyph.Length == 0) return;

        _editingPersona.Avatar = glyph;
        _editingPersona.Profile.CardAvatarChanged = true;
        _editingPersona.Profile.AvatarSourceFile = null;
        _editingPersona.PendingAvatarSource = null;
        PART_PersonaIconPicker.Flyout?.Hide();
        if (!_editingPersonaIsDraft) _personas.Save(_editingPersona);
    }

    /// <summary>更新系统提示词的前缀缓存提示。</summary>
    private void OnPersonaPromptTextChanged(object? sender, TextChangedEventArgs e)
    {
        var breaks = SystemPromptInterpolator.BreaksPrefixCache(PART_PersonaPrompt.Text);
        PART_PersonaPromptCacheHint.Text = breaks ? SystemPromptInterpolator.PrefixCacheWarning : null;
        PART_PersonaPromptCacheHint.IsVisible = breaks;
    }

    private void OnInsertPersonaVariable(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersona.IsBuiltin) return;
        if (sender is not Control { Tag: string token }) return;

        var text = PART_PersonaPrompt.Text ?? string.Empty;
        var start = Math.Min(PART_PersonaPrompt.SelectionStart, PART_PersonaPrompt.SelectionEnd);
        var end = Math.Max(PART_PersonaPrompt.SelectionStart, PART_PersonaPrompt.SelectionEnd);
        PART_PersonaPrompt.Text = text[..start] + token + text[end..];
        PART_PersonaPrompt.CaretIndex = start + token.Length;
        PART_PersonaVariables.Flyout?.Hide();
        PART_PersonaPrompt.Focus();

        if (!_editingPersonaIsDraft)
        {
            SyncPersonaForm();
            _personas.Save(_editingPersona);
        }
    }

    private void SyncPersonaForm()
    {
        if (_editingPersona is null || _editingPersona.IsBuiltin) return;

        _editingPersona.Name = PART_PersonaName.Text ?? string.Empty;
        _editingPersona.SystemPrompt = PART_PersonaPrompt.Text ?? string.Empty;
        // 一个「网络访问」写两列：旧版本读到的仍是同一个答案。
        var network = PART_PersonaNetwork.SelectedIndex == 1;
        _editingPersona.DefaultEnableNetwork = network;
        _editingPersona.DefaultEnableWebFetch = network;
        _editingPersona.DefaultThinking = PART_PersonaThinking.SelectedIndex == 1;
        _editingPersona.Profile.EnablePython = ToolChoiceValue(PART_PersonaPython.SelectedIndex);
        _editingPersona.Profile.EnableFileTools = ToolChoiceValue(PART_PersonaFileTools.SelectedIndex);
        _editingPersona.Profile.EnableImageGeneration = ToolChoiceValue(PART_PersonaImageGeneration.SelectedIndex);
        _editingPersona.Profile.EnableMcp = ToolChoiceValue(PART_PersonaMcp.SelectedIndex);
    }

    private void PersistEditingPersona()
    {
        if (_editingPersona is null || _editingPersonaIsDraft || _editingPersona.IsBuiltin) return;
        SyncPersonaForm();
        _personas.Save(_editingPersona);
    }

    // ---- browser bridge ----------------------------------------------------

    private async void OnOpenBrowserSetupGuide(object? sender, RoutedEventArgs e)
    {
        var guide = new BrowserSetupWindow { DataContext = _browserBridge };
        await guide.ShowDialog(this);
        // Closing with 「装好了，去检测」 means the answer they came for is the
        // detection result, so run it for them rather than leaving a stale card.
        if (guide.CheckRequested)
            await _browserBridge.CheckAsync();
    }

    // ---- tool grants -------------------------------------------------------

    private void OnRevokeGrant(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string key }) return;
        _settings.RevokeToolGrant(key);
    }
}

/// <summary>One line of the browser activity list. Pre-formatted rather than
/// bound through converters: the tone is a two-way choice, not a theme axis.</summary>
public sealed record BrowserActivityRow(string Time, string Action, string Detail, Avalonia.Media.IBrush Tone);

public sealed record PersonaIconRow(string Glyph, string Label);

/// <summary>
/// One row of the model-services list, reconciled against the registry.
///
/// This page reads saved rows and the model picker reads registered providers, and
/// nothing used to compare the two — so a machine whose runtime had gone stale
/// showed a full, healthy-looking list here and an empty picker there, with no
/// screen in the app admitting the gap. The card is the place to admit it: it is
/// where the user goes to ask why their service is missing.
/// </summary>
public sealed record ProviderCardRow(ProviderEntry Entry, string? InactiveReason)
{
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string? BaseUrl => Entry.Type == "chatgpt" ? "ChatGPT 套餐" : Entry.BaseUrl;
    public int ModelCount => Entry.Models.Count;
    public bool IsInactive => InactiveReason is not null;
    public bool HasBuiltinSearch =>
        !ProviderPurpose.IsImage(Entry.Purpose)
        && BuiltinWebSearch.IsEnabledOnProvider(Entry.Type, Entry.BaseUrl, Entry.BuiltinSearch);
}

public sealed record ProviderPresetRow(
    string Id,
    string Name,
    string Type,
    string BaseUrl,
    string ModelsPath,
    string Purpose = "chat",
    string? ApiPath = null,
    string? ImageEditPath = null,
    string? ImageFormat = null,
    string? ImageChatPath = null);

public sealed partial class DetectedModelRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public DetectedModelRow(
        ProviderModelEntry entry,
        bool isSelected,
        bool isEnabled,
        string statusText,
        bool supportsBuiltinSearch = false)
    {
        Entry = entry;
        _isSelected = isSelected;
        IsEnabled = isEnabled;
        StatusText = statusText;
        SupportsBuiltinSearch = supportsBuiltinSearch;
    }

    public ProviderModelEntry Entry { get; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isSelected;
    public bool IsEnabled { get; }
    public string StatusText { get; }
    public bool SupportsBuiltinSearch { get; }

    public string CapabilitySummary
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Vision) parts.Add("视觉");
            if (Entry.Tools) parts.Add("工具调用");
            if (Entry.Thinking) parts.Add("推理");
            if (Entry.ReasoningEffort) parts.Add("推理强度");
            if (SupportsBuiltinSearch) parts.Add("内置搜索");
            if (Entry.ImageEdit) parts.Add("图像编辑");
            if (Entry.ContextWindow is { } context) parts.Add($"上下文 {context / 1000}K");
            return parts.Count == 0 ? "标准模型" : string.Join(" · ", parts);
        }
    }
}
