using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Providers;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;
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
    private readonly McpHttpClient? _mcpClient;
    private readonly ImageGenerationTool? _imageGenerationTool;
    private readonly PythonRuntimeManager? _pythonRuntime;
    private readonly PiSidecarRuntimeManager? _piSidecar;
    private readonly NotificationCenter? _notifications;

    private const string PythonRuntimeNotificationKey = "python-runtime";
    private const string PiSidecarNotificationKey = "pi-sidecar";
    private readonly SkillsViewModel _skills;
    private readonly Func<HttpClient>? _byokHttpFactory;
    private readonly ProviderRegistry? _providerRegistry;
    private readonly IChatToolHost? _toolHost;
    private readonly PiByokProviderFactory? _piByokProviderFactory;
    private readonly Func<Task>? _agentRuntimeInstalled;
    private readonly Action? _agentRuntimeRemoving;
    private readonly StackPanel[] _pages;
    private readonly ObservableCollection<ModelRow> _providerModels = [];
    private readonly ObservableCollection<HeaderRow> _providerHeaders = [];
    private readonly List<DetectedModelRow> _detectedModels = [];
    private ProviderEntry? _editingProvider;
    private PersonaItemViewModel? _editingPersona;
    private McpServerEntry? _editingMcpServer;
    private bool _editingPersonaIsDraft;
    private bool _loadingPersonaForm;
    private bool _loadingProviderForm;
    private bool _applyingProviderPreset;
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
        new("openrouter", "OpenRouter", "openai-compat", "https://openrouter.ai/api/", "v1/models",
            ThinkingParamKind.OpenAiReasoningEffort),
        new("deepseek", "DeepSeek", "openai-compat", "https://api.deepseek.com/", "v1/models",
            ThinkingParamKind.DeepSeekV4),
        new("moonshot", "Moonshot (Kimi)", "openai-compat", "https://api.moonshot.cn/", "v1/models",
            ThinkingParamKind.None),
        new("openai", "OpenAI", "openai-compat", OpenAiBaseUrl, "v1/models",
            ThinkingParamKind.OpenAiReasoningEffort),
        new("openai-response", "OpenAI (Responses API)", "openai-response", OpenAiBaseUrl,
            "v1/models", ThinkingParamKind.OpenAiReasoningEffort, ApiPath: "v1/responses"),
        new("anthropic", "Anthropic (Claude)", "anthropic", AnthropicBaseUrl, "v1/models",
            ThinkingParamKind.AnthropicAdaptive),
        new("gemini", "Google Gemini", "gemini", GeminiCompatBaseUrl, "models",
            ThinkingParamKind.GeminiThinkingLevel),
        new("custom-openai", "自定义（OpenAI 兼容）", "openai-compat", "https://api.openai.com/", "v1/models",
            ThinkingParamKind.OpenAiReasoningEffort),
        new("openrouter-images", "OpenRouter 图像", "openai-compat", "https://openrouter.ai/api/", "v1/models",
            ThinkingParamKind.None, "image", "v1/chat/completions", ImageFormat: "openai-chat-image"),
        new("openai-images", "OpenAI 图像", "openai-compat", OpenAiBaseUrl, "v1/models",
            ThinkingParamKind.None, "image", "v1/images/generations", "v1/images/edits", "openai-images")
    ];

    // Endpoint defaults for the provider presets and the connection test. These
    // used to live on the direct provider classes; with the agent runtime as the
    // only engine, the settings page is the last thing that needs them.
    private const string OpenAiBaseUrl = "https://api.openai.com/";
    private const string AnthropicBaseUrl = "https://api.anthropic.com/";
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>Google's OpenAI-compatibility root — right for listing models and
    /// testing the key from this page. The agent runtime drives the native API
    /// instead; see <c>PiByokProviderFactory</c> for why.</summary>
    private const string GeminiCompatBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";

    /// <summary>Nav index → page. The rail has non-selectable group headings in
    /// it, so the mapping is explicit rather than positional arithmetic.</summary>
    private static readonly int[] PageForNavIndex = [-1, 0, 1, -1, 2, 3, 4, 5, 6, 7, -1, 8, 9, 10, 11, 12];

    public SettingsWindow(
        SettingsViewModel settings,
        MolaGptAuthService? auth = null,
        CloudSyncService? cloudSync = null,
        ConversationListViewModel? conversations = null,
        AgentBridgeStatusViewModel? agentStatus = null,
        PersonaListViewModel? personas = null,
        McpHttpClient? mcpClient = null,
        ImageGenerationTool? imageGenerationTool = null,
        PythonRuntimeManager? pythonRuntime = null,
        PiSidecarRuntimeManager? piSidecar = null,
        NotificationCenter? notifications = null,
        SkillsViewModel? skills = null,
        Func<HttpClient>? byokHttpFactory = null,
        ProviderRegistry? providerRegistry = null,
        IChatToolHost? toolHost = null,
        PiByokProviderFactory? piByokProviderFactory = null,
        Func<Task>? agentRuntimeInstalled = null,
        Action? agentRuntimeRemoving = null)
    {
        _settings = settings;
        _auth = auth;
        _cloudSync = cloudSync;
        _conversations = conversations;
        _agentStatus = agentStatus;
        _personas = personas ?? new PersonaListViewModel();
        _mcpClient = mcpClient;
        _imageGenerationTool = imageGenerationTool;
        _pythonRuntime = pythonRuntime;
        _piSidecar = piSidecar;
        _notifications = notifications;
        _skills = skills ?? new SkillsViewModel();
        _byokHttpFactory = byokHttpFactory;
        _providerRegistry = providerRegistry;
        _toolHost = toolHost;
        _piByokProviderFactory = piByokProviderFactory;
        _agentRuntimeInstalled = agentRuntimeInstalled;
        _agentRuntimeRemoving = agentRuntimeRemoving;

        InitializeComponent();
        DataContext = _settings;

        _pages =
        [
            PAGE_Account, PAGE_Appearance, PAGE_Providers, PAGE_Personas, PAGE_Search, PAGE_Titles,
            PAGE_ImageGeneration, PAGE_Vision, PAGE_Sandbox, PAGE_Approval, PAGE_Mcp, PAGE_Agent, PAGE_Skills
        ];
        PAGE_Agent.DataContext = _agentStatus;
        PAGE_Personas.DataContext = _personas;

        PART_Nav.SelectionChanged += (_, _) => ShowSelectedPage();
        PART_Nav.SelectedIndex = 1;
        BuildThemeChoices();
        BuildSearchProviderChoices();
        BuildPermissionChoices();
        BuildFontScaleChoices();
        PART_TrayMinimize.IsChecked = _settings.TrayCloseBehavior == TrayCloseBehavior.MinimizeToTray;
        PART_TrayExit.IsChecked = _settings.TrayCloseBehavior == TrayCloseBehavior.Exit;
        PART_TrayMinimize.Click += (_, _) => _settings.TrayCloseBehavior = TrayCloseBehavior.MinimizeToTray;
        PART_TrayExit.Click += (_, _) => _settings.TrayCloseBehavior = TrayCloseBehavior.Exit;

        PART_ProviderModels.ItemsSource = _providerModels;
        PART_ProviderHeaders.ItemsSource = _providerHeaders;
        PART_PersonaList.ItemsSource = _personas.Personas;
        PART_PersonaIcons.ItemsSource = PersonaIconCatalog.All
            .Select(icon => new PersonaIconRow(icon.Glyph, icon.Label));
        PART_McpServers.ItemsSource = _settings.McpServers;
        PART_SkillsList.ItemsSource = _skills.Skills;
        _providerModels.CollectionChanged += (_, _) => RefreshProviderModelEmptyState();

        PART_AddProvider.Click += (_, _) => EditProvider(null, "chat");
        PART_AddImageProvider.Click += (_, _) => EditProvider(null, "image");
        PART_AddProviderModel.Click += (_, _) => AddProviderModel();
        PART_AddProviderHeader.Click += (_, _) => _providerHeaders.Add(new HeaderRow());
        PART_RevealProviderKey.Click += (_, _) =>
            PART_ProviderApiKey.PasswordChar = PART_ProviderApiKey.PasswordChar == '\0' ? '•' : '\0';
        PART_RevealSearchKey.Click += (_, _) =>
            PART_SearchApiKey.PasswordChar = PART_SearchApiKey.PasswordChar == '\0' ? '•' : '\0';
        PART_TestSearch.Click += OnTestSearch;
        PART_CancelProviderEdit.Click += (_, _) => CloseProviderEditor();
        PART_BackProvider.Click += (_, _) => CloseProviderEditor();
        PART_SaveProvider.Click += (_, _) => SaveProvider();
        PART_DeleteEditingProvider.Click += OnDeleteEditingProvider;
        PART_DetectProviderModels.Click += OnDetectProviderModels;
        PART_TestProvider.Click += OnTestProvider;
        PART_ProviderPreset.SelectionChanged += OnProviderPresetChanged;
        PART_ProviderType.SelectionChanged += OnProviderTypeChanged;
        PART_ProviderImageFormat.SelectionChanged += OnProviderImageFormatChanged;
        PART_ProviderBaseUrl.TextChanged += OnProviderEndpointChanged;
        PART_ProviderApiPath.TextChanged += OnProviderEndpointChanged;
        PART_ProviderImageEditPath.TextChanged += OnProviderEndpointChanged;
        PART_DetectedModelSearch.TextChanged += (_, _) => RefreshDetectedModelFilter();
        PART_SelectAllDetectedModels.Click += OnSelectAllDetectedModels;
        PART_CloseDetectedModels.Click += (_, _) => CloseDetectedModels();
        PART_CancelDetectedModels.Click += (_, _) => CloseDetectedModels();
        PART_AddDetectedModels.Click += OnAddDetectedModels;
        PART_AddMcp.Click += (_, _) => EditMcp(null);
        PART_CancelMcp.Click += (_, _) => CloseMcpEditor();
        PART_SaveMcp.Click += (_, _) => SaveMcp();
        PART_TestMcp.Click += OnTestMcp;
        PART_RevealMcpToken.Click += (_, _) =>
            PART_McpToken.PasswordChar = PART_McpToken.PasswordChar == '\0' ? '•' : '\0';
        PART_AccountAction.Click += OnAccountActionClick;
        PART_SyncNow.Click += OnSyncNowClick;
        PART_SyncConversations.Click += OnSyncConversationsClick;
        PART_RevokeAll.Click += (_, _) =>
        {
            _settings.RevokeAllToolGrants();
            RefreshGrants();
        };

        RefreshProviders();
        RefreshGrants();
        RefreshAccountUi();
        RefreshMcpServers();
        RefreshSpecializedModelChoices();
        RefreshSkills();
        LoadPersonaForm(null);
        if (_personas.Personas.Count > 0) PART_PersonaList.SelectedIndex = 0;
        Closing += (_, _) => PersistEditingPersona();

        Opened += (_, _) =>
        {
            // The registry can change while the window is closed — a provider
            // added from the model picker's empty state, a grant taken during a
            // tool run — so the lists are re-read on every open.
            _settings.Reload();
            _settings.ReloadToolGrants();
            RefreshProviders();
            RefreshGrants();
            RefreshAccountUi();
            RefreshMcpServers();
            RefreshSpecializedModelChoices();
            RefreshSkills();
            _ = RefreshRuntimeStatusAsync();
        };
        Activated += (_, _) => _ = RefreshRuntimeStatusAsync();

        PART_TestImageGeneration.IsEnabled = _imageGenerationTool is not null;
        PART_TestSearch.IsEnabled = _byokHttpFactory is not null;
        PART_DetectProviderModels.IsEnabled = _byokHttpFactory is not null;
        PART_TestProvider.IsEnabled = _byokHttpFactory is not null;
        PART_ConfigureSandbox.IsEnabled = _pythonRuntime is not null && _piSidecar is not null;
        PART_BrowsePython.IsEnabled = _pythonRuntime is not null;
        PART_SkillsNav.IsVisible = _settings.PythonToolEnabled;
        RefreshPythonBrowseButton();

        PropertyChangedEventHandler settingsChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.PythonToolEnabled))
                PART_SkillsNav.IsVisible = _settings.PythonToolEnabled;
            else if (args.PropertyName == nameof(SettingsViewModel.PythonToolExecutablePath))
                RefreshPythonBrowseButton();
        };
        _settings.PropertyChanged += settingsChanged;
        Closed += (_, _) => _settings.PropertyChanged -= settingsChanged;
        KeyDown += OnSettingsKeyDown;
    }

    public event EventHandler? AccountRequested;

    internal void OpenAgentPage()
    {
        PART_Nav.SelectedItem = PART_AgentNav;
        if (_agentStatus is not null) _ = _agentStatus.LoadAsync();
    }

    internal void OpenSandboxPage()
    {
        PART_Nav.SelectedItem = PART_SandboxNav;
    }

    internal void OpenPersonaPage(bool startNew)
    {
        PART_Nav.SelectedItem = PART_PersonasNav;
        ShowSelectedPage();
        if (startNew) OnNewPersona(this, new RoutedEventArgs());
    }

    internal void RefreshAccountUi()
    {
        var loggedIn = _auth is null ? _settings.IsLoggedIn : !string.IsNullOrEmpty(_auth.CurrentJwt);
        var username = _auth?.CurrentUsername ?? _settings.MolaGptUsername;

        _settings.IsLoggedIn = loggedIn;
        _settings.MolaGptUsername = loggedIn ? username : null;
        PART_AccountStatus.Text = loggedIn ? username ?? "MolaGPT 用户" : "未登录";
        PART_AccountDetail.Text = loggedIn ? "已登录账号" : string.Empty;
        PART_AccountAction.Content = loggedIn ? "退出" : "登录";
        if (!loggedIn) PART_CloudSyncStatus.Text = string.Empty;
    }

    private void OnAccountActionClick(object? sender, RoutedEventArgs e)
    {
        if (_auth is not null && !string.IsNullOrEmpty(_auth.CurrentJwt))
        {
            _auth.Logout();
            RefreshAccountUi();
            return;
        }

        AccountRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnSyncNowClick(object? sender, RoutedEventArgs e)
    {
        if (_cloudSync is null || !_settings.IsLoggedIn) return;

        PART_SyncNow.IsEnabled = false;
        PART_SyncNow.Content = "同步中...";
        PART_CloudSyncStatus.Text = "正在同步对话...";
        try
        {
            var progress = new Progress<string>(message => PART_CloudSyncStatus.Text = message);
            var result = await _cloudSync.SyncAsync(progress);
            if (_conversations is not null) await _conversations.ReloadAsync();
            PART_CloudSyncStatus.Text = $"已同步：上传 {result.Uploaded} · 更新 {result.Downloaded} · 删除 {result.Deleted}";
        }
        catch (Exception ex)
        {
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
        try
        {
            await _cloudSync.UpdateCloudSyncSettingAsync(_settings.SyncConversations);
            PART_CloudSyncStatus.Text = _settings.SyncConversations
                ? "已开启对话云同步"
                : "已关闭对话云同步";
        }
        catch (Exception ex)
        {
            PART_CloudSyncStatus.Text = $"同步设置更新失败：{ex.Message}";
        }
    }

    private void ShowSelectedPage()
    {
        var index = PART_Nav.SelectedIndex;
        if (index < 0 || index >= PageForNavIndex.Length) return;

        var page = PageForNavIndex[index];
        if (page < 0) return;   // a group heading; leave the current page up

        for (var i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == page;
        PART_ContentScroll.Offset = default;
        if (page != 2) CloseProviderEditor();
        if (page == 2) RefreshProviders();
        if (page == 11 && _agentStatus is not null) _ = _agentStatus.LoadAsync();
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

    private void BuildSearchProviderChoices()
    {
        var ids = new[] { "duckduckgo", "tavily", "exa" };
        PART_SearchProvider.ItemsSource = new[] { "DuckDuckGo（免密钥）", "Tavily", "Exa" };
        PART_SearchProvider.SelectedIndex =
            Math.Max(0, Array.IndexOf(ids, SettingsViewModel.NormalizeWebSearchProvider(_settings.WebSearchProvider)));
        UpdateSearchStatusHint();

        PART_SearchProvider.SelectionChanged += (_, _) =>
        {
            if (PART_SearchProvider.SelectedIndex >= 0)
            {
                _settings.WebSearchProvider = ids[PART_SearchProvider.SelectedIndex];
                UpdateSearchStatusHint();
            }
        };
    }

    private void BuildPermissionChoices()
    {
        var order = new[] { ToolPermissionMode.Approval, ToolPermissionMode.FullAccess };
        var labels = new[] { "审批权限", "完全权限" };

        Bind(PART_ToolPermission, _settings.LocalToolPermissionMode, mode =>
        {
            _settings.LocalToolPermissionMode = mode;
            PART_PerToolPermissions.IsEnabled = mode == ToolPermissionMode.Approval;
        });
        Bind(PART_ImagePermission, _settings.ImageGenerationPermissionMode,
            mode => _settings.ImageGenerationPermissionMode = mode);
        Bind(PART_VisionPermission, _settings.VisionPermissionMode,
            mode => _settings.VisionPermissionMode = mode);
        Bind(PART_McpPermission, _settings.McpPermissionMode,
            mode => _settings.McpPermissionMode = mode);
        Bind(PART_PythonPermission, _settings.PythonExecutionPermissionMode,
            mode => _settings.PythonExecutionPermissionMode = mode);

        PART_PerToolPermissions.IsEnabled = _settings.LocalToolPermissionMode == ToolPermissionMode.Approval;
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
            _settings.RefreshImageGenerationProviderModels();
            _settings.RefreshVisionProviderModels();

            PART_ImageGenerationModel.ItemsSource = null;
            PART_ImageGenerationModel.ItemsSource = _settings.ImageGenerationProviderModels;
            PART_ImageGenerationModel.SelectedItem = _settings.ImageGenerationProviderModels.FirstOrDefault(option =>
                string.Equals(option.ProviderId, _settings.ImageGenerationProviderId, StringComparison.Ordinal)
                && string.Equals(option.ModelId, _settings.ImageGenerationModelId, StringComparison.Ordinal));

            PART_VisionModel.ItemsSource = null;
            PART_VisionModel.ItemsSource = _settings.VisionProviderModels;
            PART_VisionModel.SelectedItem = _settings.VisionProviderModels.FirstOrDefault(option =>
                string.Equals(option.ProviderId, _settings.VisionProxyProviderId, StringComparison.Ordinal)
                && string.Equals(option.ModelId, _settings.VisionProxyModelId, StringComparison.Ordinal));

            RefreshImageGenerationStatus();
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

    private void OnManageProviders(object? sender, RoutedEventArgs e) => PART_Nav.SelectedIndex = 4;

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
        PART_SearchProviderHint.Text = "DuckDuckGo 免费无需密钥；Tavily / Exa 需 API Key。";
        PART_SearchStatus.Text = SettingsViewModel.NormalizeWebSearchProvider(_settings.WebSearchProvider) switch
        {
            "tavily" => "Tavily 使用其搜索 API；网页阅读由本机抓取。",
            "exa" => "Exa 使用其搜索 API 并请求正文摘要；网页阅读由本机抓取。",
            _ => "DuckDuckGo 无需 API Key，但稳定性与结果质量取决于页面可访问性。"
        };
    }

    private async void OnTestSearch(object? sender, RoutedEventArgs e)
    {
        if (_byokHttpFactory is null) return;

        PART_TestSearch.IsEnabled = false;
        PART_TestSearch.Content = "测试中...";
        PART_SearchStatus.Text = "正在发送一次测试搜索...";
        try
        {
            var options = new LocalToolOptions(
                Network: true,
                WebPage: false,
                SearchProvider: _settings.WebSearchProvider,
                SearchApiKey: _settings.WebSearchApiKey,
                SearchBaseUrl: _settings.WebSearchBaseUrl,
                SearchMaxResults: Math.Clamp(_settings.WebSearchMaxResults, 1, 10),
                WebPageMaxCharacters: Math.Clamp(_settings.WebPageMaxCharacters, 1000, 30000));
            using var http = _byokHttpFactory();
            var result = await LocalToolRegistry.ExecuteAsync(
                "search_web", "{\"query\":\"MolaGPT\"}", options, http, CancellationToken.None);
            using var document = JsonDocument.Parse(result);
            var ok = document.RootElement.TryGetProperty("success", out var success)
                     && success.ValueKind == JsonValueKind.True;
            PART_SearchStatus.Text = ok
                ? "连接成功，搜索服务可用。"
                : "连接失败，请检查 API Key 或接入地址。";
        }
        catch (Exception ex)
        {
            PART_SearchStatus.Text = "连接失败：" + ex.Message;
        }
        finally
        {
            PART_TestSearch.Content = "测试连接";
            PART_TestSearch.IsEnabled = true;
        }
    }

    // ---- providers ---------------------------------------------------------

    private void RefreshProviders()
    {
        var chatProviders = _settings.Providers
            .Where(provider => !SettingsViewModel.IsImagePurpose(provider.Purpose))
            .ToArray();
        var imageProviders = _settings.Providers
            .Where(provider => SettingsViewModel.IsImagePurpose(provider.Purpose))
            .ToArray();

        PART_ChatProviderList.ItemsSource = chatProviders;
        PART_ImageProviderList.ItemsSource = imageProviders;
        PART_NoChatProviders.IsVisible = chatProviders.Length == 0;
        PART_NoImageProviders.IsVisible = imageProviders.Length == 0;
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
        _editingProviderPurpose = SettingsViewModel.IsImagePurpose(existing?.Purpose ?? purpose) ? "image" : "chat";
        _providerModels.Clear();
        _providerHeaders.Clear();
        CloseDetectedModels();

        _loadingProviderForm = true;
        try
        {
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
                PART_ProviderBaseUrl.Text = existing.BaseUrl ?? string.Empty;
                PART_ProviderApiPath.Text = string.IsNullOrWhiteSpace(existing.ApiPath)
                    ? DefaultProviderApiPath(existing.Purpose, existing.ImageFormat, existing.Type)
                    : existing.ApiPath;
                PART_ProviderImageFormat.SelectedIndex = ImageApiFormat.IsChatImage(existing.ImageFormat) ? 1 : 0;
                PART_ProviderImageEditPath.Text = string.IsNullOrWhiteSpace(existing.ImageEditPath)
                    ? "v1/images/edits"
                    : existing.ImageEditPath;

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

        _providerModels.Add(CreateModelRow(new ProviderModelEntry(id, "新模型")));
    }

    private void OnAddProviderBody(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ModelRow model }) return;
        model.CustomBodyRows.Add(new BodyRow(model));
    }

    private void OnRemoveProviderBody(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BodyRow row }) row.Owner.CustomBodyRows.Remove(row);
    }

    private void SaveProvider()
    {
        if (!TryCollectProvider(out var result)) return;
        if (result.Models.Count == 0)
        {
            FailProviderEdit("至少要有一个填了 ID 的模型。");
            return;
        }

        _settings.Save(result);
        var current = _settings.Providers.FirstOrDefault(provider => provider.Id == result.Id);
        if (current is null)
            _settings.Providers.Add(result);
        else
            _settings.Providers[_settings.Providers.IndexOf(current)] = result;

        try
        {
            if (_providerRegistry is not null && _byokHttpFactory is not null)
                ProviderRestorer.ApplyEntry(result, _providerRegistry, _byokHttpFactory, _toolHost, _piByokProviderFactory);
        }
        catch (Exception ex)
        {
            FailProviderEdit("设置已保存，但运行时更新失败：" + ex.Message);
            return;
        }

        _settings.RefreshTitleProviderModels();
        _settings.RefreshVisionProviderModels();
        _settings.RefreshImageGenerationProviderModels();
        RefreshProviders();
        RefreshSpecializedModelChoices();
        _editingProvider = result;
        PART_ProviderEditorTitle.Text = $"编辑「{result.Name}」";
        PART_DeleteEditingProvider.IsVisible = true;
        ShowProviderStatus("设置已保存");
    }

    private static ProviderModelEntry ToProviderModel(ModelRow row)
    {
        var id = row.Id.Trim();
        var displayName = string.IsNullOrWhiteSpace(row.DisplayName) ? id : row.DisplayName.Trim();

        var thinkingKind = row.Thinking ? ModelRow.ThinkingKindForIndex(row.ThinkingKindIndex) : null;
        var effortLevels = row.Thinking
            ? ThinkingEffortLevels.Normalize(row.EffortLevelsText.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries)).ToList()
            : [];
        var customBody = row.CustomBodyRows
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(item => new CustomBodyEntry(item.Key.Trim(), item.Type, item.Value ?? string.Empty))
            .ToList();

        return (row.Source ?? new ProviderModelEntry(id, displayName)) with
        {
            Id = id,
            DisplayName = displayName,
            Vision = row.Vision,
            Thinking = row.Thinking,
            ReasoningEffort = row.ReasoningEffort,
            Tools = row.Tools,
            ContextWindow = ParseNullableInt(row.ContextWindowText),
            ThinkingParamKind = thinkingKind,
            ThinkingBudgetMin = row.Thinking ? ParseNullableInt(row.BudgetMinText) : null,
            ThinkingBudgetMax = row.Thinking ? ParseNullableInt(row.BudgetMaxText) : null,
            ThinkingBudgetDefault = row.Thinking ? ParseNullableInt(row.BudgetDefaultText) : null,
            DefaultEffort = row.Thinking && !string.IsNullOrWhiteSpace(row.DefaultEffort) ? row.DefaultEffort.Trim() : null,
            SystemPrompt = string.IsNullOrWhiteSpace(row.SystemPrompt) ? null : row.SystemPrompt.Trim(),
            ImageEdit = row.IsImageProvider && row.ImageEdit,
            CustomBody = customBody.Count > 0 ? customBody : null,
            EffortLevels = effortLevels.Count > 0 ? effortLevels : null
        };
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
        if (baseUrl.Length == 0)
        {
            FailProviderEdit("接入地址不能为空。");
            entry = default!;
            return false;
        }

        var normalizedBaseUrl = baseUrl.TrimEnd('/') + "/";
        try
        {
            NetworkSecurity.RequireHttpsBaseUrl(normalizedBaseUrl, $"{name} 接入地址");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            FailProviderEdit("接入地址必须是有效的 https:// 地址。");
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
        var imageFormat = image && PART_ProviderImageFormat.SelectedIndex == 1
            ? ImageApiFormat.OpenAiChatImage
            : image ? ImageApiFormat.OpenAiImages : null;

        entry = new ProviderEntry(
            _editingProvider?.Id ?? Guid.NewGuid().ToString("N"),
            ProviderTypes[Math.Max(0, PART_ProviderType.SelectedIndex)],
            name,
            normalizedBaseUrl,
            string.IsNullOrEmpty(PART_ProviderApiKey.Text) ? null : PART_ProviderApiKey.Text,
            models,
            Enabled: true,
            SortOrder: _editingProvider?.SortOrder ?? _settings.Providers.Count,
            Purpose: _editingProviderPurpose,
            ApiPath: string.IsNullOrWhiteSpace(PART_ProviderApiPath.Text) ? null : PART_ProviderApiPath.Text.Trim(),
            ImageEditPath: image && !ImageApiFormat.IsChatImage(imageFormat)
                && !string.IsNullOrWhiteSpace(PART_ProviderImageEditPath.Text)
                    ? PART_ProviderImageEditPath.Text.Trim()
                    : null,
            ImageFormat: imageFormat,
            CustomHeaders: headers.Count > 0 ? headers : null);
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

        DeleteProvider(entry);
        CloseProviderEditor();
    }

    private void DeleteProvider(ProviderEntry entry)
    {
        _settings.Delete(entry.Id);
        if (_providerRegistry is not null)
            ProviderRestorer.RemoveEntry(entry.Id, _providerRegistry, _piByokProviderFactory);
        RefreshProviders();
        RefreshSpecializedModelChoices();
    }

    private ModelRow CreateModelRow(ProviderModelEntry model)
    {
        var row = new ModelRow
        {
            Id = model.Id,
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? BeautifyModelName(model.Id) : model.DisplayName,
            Vision = model.Vision,
            Thinking = model.Thinking,
            ReasoningEffort = model.ReasoningEffort,
            Tools = model.Tools,
            ContextWindowText = model.ContextWindow?.ToString() ?? string.Empty,
            ThinkingKindIndex = ModelRow.ThinkingKindIndexFor(model.ThinkingParamKind),
            BudgetMinText = model.ThinkingBudgetMin?.ToString() ?? string.Empty,
            BudgetMaxText = model.ThinkingBudgetMax?.ToString() ?? string.Empty,
            BudgetDefaultText = model.ThinkingBudgetDefault?.ToString() ?? string.Empty,
            DefaultEffort = model.DefaultEffort ?? string.Empty,
            SystemPrompt = model.SystemPrompt ?? string.Empty,
            ImageEdit = model.ImageEdit,
            IsImageProvider = _editingProviderPurpose == "image",
            EffortLevelsText = string.Join(", ", model.EffortLevels ?? []),
            Source = model
        };
        foreach (var item in model.CustomBody ?? [])
            row.CustomBodyRows.Add(new BodyRow(row, item.Key, item.Type, item.Value));
        return row;
    }

    private void SetProviderPresetItems()
    {
        var items = ProviderPresets
            .Where(preset => SettingsViewModel.IsImagePurpose(preset.Purpose) == (_editingProviderPurpose == "image"))
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
            SettingsViewModel.IsImagePurpose(preset.Purpose) == SettingsViewModel.IsImagePurpose(entry.Purpose)
            && string.Equals(preset.Type, entry.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(preset.BaseUrl), NormalizeUrl(entry.BaseUrl), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeUrl(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');

    private void OnProviderPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingProviderPreset || _loadingProviderForm || PART_ProviderPreset.SelectedItem is not ProviderPresetRow preset)
            return;

        _loadingProviderForm = true;
        try
        {
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
        PART_ProviderApiPath.Text = string.IsNullOrWhiteSpace(preset.ApiPath)
            ? DefaultProviderApiPath(preset.Purpose, preset.ImageFormat, preset.Type)
            : preset.ApiPath;
        PART_ProviderImageFormat.SelectedIndex = ImageApiFormat.IsChatImage(preset.ImageFormat) ? 1 : 0;
        PART_ProviderImageEditPath.Text = string.IsNullOrWhiteSpace(preset.ImageEditPath)
            ? "v1/images/edits"
            : preset.ImageEditPath;
    }

    private static int ProviderTypeIndex(string? type)
    {
        var normalized = string.Equals(type, "openai", StringComparison.OrdinalIgnoreCase) ? "openai-compat" : type;
        var index = Array.FindIndex(ProviderTypes, value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, index);
    }

    private void UpdateProviderPurposeUi()
    {
        var image = _editingProviderPurpose == "image";
        PART_ProviderPurpose.Text = image ? "图像服务" : "对话服务";
        PART_ProviderImageFields.IsVisible = image;
        PART_ProviderApiPathLabel.Text = image ? "生成路径" : "对话路径";

        for (var i = 0; i < PART_ProviderType.Items.Count; i++)
        {
            if (PART_ProviderType.Items[i] is not ComboBoxItem item) continue;
            item.IsVisible = !image || i is 0 or 3;
            item.IsEnabled = item.IsVisible;
        }
        if (image && PART_ProviderType.SelectedIndex is not (0 or 3)) PART_ProviderType.SelectedIndex = 0;

        var chatImage = image && PART_ProviderImageFormat.SelectedIndex == 1;
        PART_ProviderImageEditPathField.IsVisible = image && !chatImage;
        foreach (var model in _providerModels) model.IsImageProvider = image;
    }

    private static string DefaultProviderApiPath(string? purpose, string? imageFormat, string? type)
    {
        if (SettingsViewModel.IsImagePurpose(purpose))
            return ImageApiFormat.IsChatImage(imageFormat) ? "v1/chat/completions" : "v1/images/generations";
        return type?.Trim().ToLowerInvariant() switch
        {
            "anthropic" => "v1/messages",
            "openai-response" => "v1/responses",
            _ => "v1/chat/completions"
        };
    }

    private void OnProviderTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingProviderForm) return;
        var known = new[] { "v1/chat/completions", "v1/messages", "v1/responses" };
        var current = PART_ProviderApiPath.Text?.Trim() ?? string.Empty;
        if (current.Length == 0 || known.Contains(current, StringComparer.OrdinalIgnoreCase))
            PART_ProviderApiPath.Text = DefaultProviderApiPath(
                _editingProviderPurpose,
                PART_ProviderImageFormat.SelectedIndex == 1 ? ImageApiFormat.OpenAiChatImage : ImageApiFormat.OpenAiImages,
                ProviderTypes[Math.Max(0, PART_ProviderType.SelectedIndex)]);
        UpdateProviderPurposeUi();
        UpdateProviderEndpointPreview();
    }

    private void OnProviderImageFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingProviderForm) return;
        var current = PART_ProviderApiPath.Text?.Trim() ?? string.Empty;
        var known = new[] { "v1/chat/completions", "v1/images/generations" };
        if (current.Length == 0 || known.Contains(current, StringComparer.OrdinalIgnoreCase))
            PART_ProviderApiPath.Text = PART_ProviderImageFormat.SelectedIndex == 1
                ? "v1/chat/completions"
                : "v1/images/generations";
        if (PART_ProviderImageFormat.SelectedIndex == 0 && string.IsNullOrWhiteSpace(PART_ProviderImageEditPath.Text))
            PART_ProviderImageEditPath.Text = "v1/images/edits";
        UpdateProviderPurposeUi();
        UpdateProviderEndpointPreview();
    }

    private void OnProviderEndpointChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_loadingProviderForm) UpdateProviderEndpointPreview();
    }

    private void UpdateProviderEndpointPreview()
    {
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
            var fallback = DefaultProviderApiPath(
                "chat", null, ProviderTypes[Math.Max(0, PART_ProviderType.SelectedIndex)]);
            PART_ProviderEndpointPreview.Text = "实际请求地址：" + Join(PART_ProviderApiPath.Text, fallback);
            return;
        }

        var chatImage = PART_ProviderImageFormat.SelectedIndex == 1;
        var generation = Join(PART_ProviderApiPath.Text, chatImage ? "v1/chat/completions" : "v1/images/generations");
        PART_ProviderEndpointPreview.Text = chatImage
            ? "生成 / 编辑地址：" + generation
            : "生成地址：" + generation + "\n编辑地址：" + Join(PART_ProviderImageEditPath.Text, "v1/images/edits");
    }

    private async void OnDetectProviderModels(object? sender, RoutedEventArgs e)
    {
        if (_byokHttpFactory is null || !TryCollectProvider(out var entry)) return;

        PART_DetectProviderModels.IsEnabled = false;
        PART_DetectProviderModels.Content = "获取中...";
        ShowProviderStatus("正在获取模型列表...");
        try
        {
            var models = await FetchProviderModelsAsync(entry);
            _detectedModels.Clear();
            var existing = _providerModels.Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
            {
                var alreadyExists = existing.Contains(model.Id);
                _detectedModels.Add(new DetectedModelRow(model, !alreadyExists, !alreadyExists,
                    alreadyExists ? "已存在" : string.Empty));
            }

            PART_DetectedModelsTitle.Text = $"检测到 {models.Count} 个模型";
            PART_DetectedModelSearch.Text = string.Empty;
            PART_SelectAllDetectedModels.IsChecked = _detectedModels.Any(item => item.IsEnabled);
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
        }
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

    private void OnAddDetectedModels(object? sender, RoutedEventArgs e)
    {
        var selected = _detectedModels.Where(item => item.IsEnabled && item.IsSelected).ToList();
        foreach (var item in selected) _providerModels.Add(CreateModelRow(item.Entry));
        CloseDetectedModels();
        ShowProviderStatus(selected.Count == 0 ? "未选择模型。" : $"已添加 {selected.Count} 个模型，保存后生效。");
    }

    private void CloseDetectedModels()
    {
        PART_DetectedModelsPanel.IsVisible = false;
        _detectedModels.Clear();
        PART_DetectedModels.ItemsSource = null;
    }

    private async void OnTestProvider(object? sender, RoutedEventArgs e)
    {
        if (_byokHttpFactory is null || !TryCollectProvider(out var entry)) return;
        PART_TestProvider.IsEnabled = false;
        PART_TestProvider.Content = "测试中...";
        ShowProviderStatus("正在测试连接...");
        try
        {
            if (SettingsViewModel.IsImagePurpose(entry.Purpose))
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
                    Format: entry.ImageFormat,
                    GenerationPath: entry.ApiPath,
                    EditPath: entry.ImageEditPath);
                var images = await new ImageGenerationTool(_byokHttpFactory).GenerateAsync(
                    options, "a single small red dot on a white background", CancellationToken.None);
                ShowProviderStatus(images.Count > 0 ? "连接成功，图像服务可用。" : "连接成功，但未返回图片。");
                return;
            }

            using var http = _byokHttpFactory();
            var baseUrl = NetworkSecurity.RequireHttpsBaseUrl(entry.BaseUrl ?? DefaultProviderBaseUrl(entry.Type), $"{entry.Name} 接入地址");
            var path = string.IsNullOrWhiteSpace(entry.ApiPath)
                ? DefaultProviderApiPath(entry.Purpose, entry.ImageFormat, entry.Type)
                : entry.ApiPath;
            var url = NetworkSecurity.CombineEndpoint(baseUrl, path, $"{entry.Name} 接入地址");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            var model = entry.Models.FirstOrDefault()?.Id ?? DefaultTestModel(entry.Type);
            object body = entry.Type switch
            {
                "anthropic" => new { model, max_tokens = 8, messages = new[] { new { role = "user", content = "ping" } } },
                "openai-response" => new { model, input = "ping", max_output_tokens = 16 },
                "gemini" => new { contents = new[] { new { parts = new[] { new { text = "ping" } } } } },
                _ => new { model, messages = new[] { new { role = "user", content = "ping" } }, max_tokens = 4 }
            };
            request.Content = JsonContent.Create(body);
            ApplyProviderAuthentication(request, entry);
            ApplyProviderCustomHeaders(request, entry);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
                ShowProviderStatus("连接正常。");
            else
                FailProviderEdit($"HTTP {(int)response.StatusCode}：{await response.Content.ReadAsStringAsync()}");
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

    private async Task<List<ProviderModelEntry>> FetchProviderModelsAsync(ProviderEntry entry)
    {
        var preset = FindProviderPreset(entry);
        var baseUrl = NetworkSecurity.RequireHttpsBaseUrl(entry.BaseUrl ?? DefaultProviderBaseUrl(entry.Type), $"{entry.Name} 接入地址");
        var modelsPath = preset?.ModelsPath ?? (entry.Type == "gemini" ? "models" : "v1/models");
        var url = NetworkSecurity.CombineEndpoint(baseUrl, modelsPath, $"{entry.Name} 接入地址");

        using var http = _byokHttpFactory!();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyProviderAuthentication(request, entry);
        ApplyProviderCustomHeaders(request, entry);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{body}");

        using var document = JsonDocument.Parse(body);
        var models = entry.Type == "gemini"
            ? ParseGeminiModels(document.RootElement)
            : SettingsViewModel.IsImagePurpose(entry.Purpose)
                ? ParseImageModels(document.RootElement)
                : IsOpenRouter(entry)
                    ? ParseOpenRouterModels(document.RootElement)
                    : ParseCompatibleModels(document.RootElement);

        var thinkingKind = preset?.DefaultThinkingKind ?? InferProviderThinkingKind(entry.Type);
        if (thinkingKind == ThinkingParamKind.None) return models;
        var kindName = thinkingKind.ToString();
        return models.Select(model => model.Thinking && string.IsNullOrWhiteSpace(model.ThinkingParamKind)
            ? model with { ThinkingParamKind = kindName }
            : model).ToList();
    }

    private static void ApplyProviderAuthentication(HttpRequestMessage request, ProviderEntry entry)
    {
        if (entry.Type == "anthropic")
        {
            request.Headers.TryAddWithoutValidation("x-api-key", entry.ApiKey ?? string.Empty);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else if (entry.Type == "gemini")
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", entry.ApiKey ?? string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey);
        }
    }

    private static void ApplyProviderCustomHeaders(HttpRequestMessage request, ProviderEntry entry)
    {
        var headers = CustomParamConverter.ToHeaderList(entry.CustomHeaders);
        OpenRouterAttribution.Apply(request, entry.BaseUrl, headers);
        if (headers is null) return;
        foreach (var (name, value) in headers)
            if (!string.IsNullOrWhiteSpace(name)) request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string DefaultProviderBaseUrl(string type) => type switch
    {
        "anthropic" => AnthropicBaseUrl,
        "gemini" => GeminiCompatBaseUrl,
        _ => OpenAiBaseUrl
    };

    private static string DefaultTestModel(string type) => type switch
    {
        "anthropic" => "claude-3-5-haiku-20241022",
        "gemini" => "gemini-2.5-flash",
        _ => "gpt-4o-mini"
    };

    private static ThinkingParamKind InferProviderThinkingKind(string type) => type switch
    {
        "openai" or "openai-response" => ThinkingParamKind.OpenAiReasoningEffort,
        "anthropic" => ThinkingParamKind.AnthropicAdaptive,
        "gemini" => ThinkingParamKind.GeminiThinkingLevel,
        _ => ThinkingParamKind.None
    };

    private static List<ProviderModelEntry> ParseCompatibleModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String) continue;
            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id) || !LooksLikeChatModel(id)) continue;
            var display = item.TryGetProperty("display_name", out var displayNode) && displayNode.ValueKind == JsonValueKind.String
                ? displayNode.GetString()
                : null;
            var kind = ThinkingParamKindInference.InferFromModelId(id);
            models.Add(new ProviderModelEntry(
                id,
                string.IsNullOrWhiteSpace(display) ? BeautifyModelName(id) : display!,
                Vision: LooksLikeVisionModel(id),
                Thinking: LooksLikeReasoningModel(id),
                ReasoningEffort: LooksLikeReasoningModel(id),
                Tools: LooksLikeToolModel(id),
                ThinkingParamKind: kind == ThinkingParamKind.None ? null : kind.ToString()));
        }
        return models.OrderByDescending(model => model.Tools).ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<ProviderModelEntry> ParseOpenRouterModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String
                ? idNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : BeautifyModelName(id);
            var parameters = ReadStringArray(item, "supported_parameters");
            var modalities = ReadStringArray(item, "architecture", "input_modalities");
            var context = item.TryGetProperty("context_length", out var contextNode) && contextNode.ValueKind == JsonValueKind.Number
                ? contextNode.GetInt32()
                : (int?)null;
            var reasoning = parameters.Any(value => IsAny(value, "reasoning", "reasoning_effort", "reasoning_effort_max"))
                            || LooksLikeReasoningModel(id);
            models.Add(new ProviderModelEntry(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name!,
                Vision: modalities.Any(value => IsAny(value, "image", "vision")) || LooksLikeVisionModel(id),
                ContextWindow: context,
                Thinking: reasoning,
                ReasoningEffort: reasoning && parameters.Any(value => value.Contains("effort", StringComparison.OrdinalIgnoreCase)),
                Tools: parameters.Any(value => IsAny(value, "tools", "tool_choice"))));
        }
        return models.OrderByDescending(model => model.Tools)
            .ThenByDescending(model => model.Thinking)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ProviderModelEntry> ParseGeminiModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var id = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name[7..] : name;
            var methods = ReadStringArray(item, "supportedGenerationMethods");
            if (methods.Count > 0 && !methods.Any(method => method.Equals("generateContent", StringComparison.OrdinalIgnoreCase)))
                continue;
            var display = item.TryGetProperty("displayName", out var displayNode) && displayNode.ValueKind == JsonValueKind.String
                ? displayNode.GetString()
                : BeautifyModelName(id);
            var reasoning = LooksLikeReasoningModel(id);
            models.Add(new ProviderModelEntry(
                id,
                string.IsNullOrWhiteSpace(display) ? BeautifyModelName(id) : display!,
                Vision: LooksLikeVisionModel(id),
                Thinking: reasoning,
                ReasoningEffort: reasoning,
                Tools: LooksLikeToolModel(id),
                ThinkingParamKind: reasoning ? ThinkingParamKind.GeminiThinkingLevel.ToString() : null));
        }
        return models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<ProviderModelEntry> ParseImageModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<ProviderModelEntry>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String) continue;
            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id) || !LooksLikeImageModel(id)) continue;
            models.Add(new ProviderModelEntry(id, BeautifyModelName(id), ImageEdit: LooksLikeImageEditModel(id)));
        }
        return models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
            if (!current.TryGetProperty(segment, out current)) return [];
        if (current.ValueKind != JsonValueKind.Array) return [];
        return current.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static bool LooksLikeChatModel(string id)
    {
        var value = id.ToLowerInvariant();
        if (value.Contains("embedding") || value.Contains("moderation") || value.Contains("tts")
            || value.Contains("transcribe") || value.Contains("whisper") || value.Contains("dall-e")
            || value.Contains("image") || value.Contains("audio") || value.Contains("realtime")) return false;
        return value.StartsWith("gpt-") || value.StartsWith("o1") || value.StartsWith("o3")
               || value.StartsWith("o4") || value.StartsWith("deepseek") || value.StartsWith("moonshot")
               || value.StartsWith("qwen") || value.StartsWith("gemini") || value.StartsWith("claude")
               || value.Contains("chat");
    }

    private static bool LooksLikeReasoningModel(string id)
    {
        var value = id.ToLowerInvariant();
        return value.StartsWith("o1") || value.StartsWith("o3") || value.StartsWith("o4")
               || value.StartsWith("gpt-5") || value.Contains("reasoning") || value.Contains("deepseek-r1")
               || value.Contains("deepseek-reasoner") || value.Contains("qwq") || value.Contains("qwen3")
               || value.Contains("gemini-2.5") || value.Contains("gemini-3");
    }

    private static bool LooksLikeVisionModel(string id)
    {
        var value = id.ToLowerInvariant();
        return value.Contains("vision") || value.Contains("gpt-4o") || value.Contains("gpt-4.1")
               || value.Contains("gpt-5") || value.Contains("gemini") || value.Contains("claude-3")
               || value.Contains("claude-sonnet-4") || value.Contains("claude-opus-4")
               || value.Contains("claude-haiku-4") || value.Contains("deepseek-chat") || value.Contains("qwen-vl");
    }

    private static bool LooksLikeImageModel(string id)
    {
        var value = id.ToLowerInvariant();
        return value.Contains("dall-e") || value.Contains("image") || value.Contains("flux")
               || value.Contains("midjourney") || value.Contains("sdxl")
               || value.Contains("stable-diffusion") || value.Contains("recraft");
    }

    private static bool LooksLikeImageEditModel(string id)
    {
        var value = id.ToLowerInvariant();
        return value.Contains("gpt-image") || value.Contains("gpt image")
               || value.Contains("imagen") || value.Contains("edit");
    }

    private static bool LooksLikeToolModel(string id)
    {
        var value = id.ToLowerInvariant();
        return LooksLikeChatModel(id) && !value.Contains("instruct") && !value.Contains("base");
    }

    private static string BeautifyModelName(string id)
    {
        var name = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return name.Replace('_', ' ');
    }

    private static bool IsOpenRouter(ProviderEntry entry) =>
        (entry.BaseUrl ?? string.Empty).Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase)
        || entry.Name.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase);

    private static bool IsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private void OnSettingsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
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

    private void RefreshMcpServers() =>
        PART_NoMcpServers.IsVisible = _settings.McpServers.Count == 0;

    private void OnEditMcp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: McpServerEntry entry }) EditMcp(entry);
    }

    private void EditMcp(McpServerEntry? entry)
    {
        _editingMcpServer = entry;
        PART_McpEditorTitle.Text = entry is null ? "添加 MCP 服务器" : $"编辑「{entry.Name}」";
        PART_McpName.Text = entry?.Name ?? string.Empty;
        PART_McpUrl.Text = entry?.Url ?? string.Empty;
        PART_McpHeader.Text = string.IsNullOrWhiteSpace(entry?.HeaderName) ? "Authorization" : entry.HeaderName;
        PART_McpToken.Text = entry?.Token ?? string.Empty;
        PART_McpToken.PasswordChar = '•';
        PART_McpEnabled.IsChecked = entry?.Enabled ?? true;
        PART_McpStatus.Text = string.Empty;
        PART_McpError.IsVisible = false;
        PART_TestMcp.IsEnabled = _mcpClient is not null;
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

    private bool TryBuildMcpEntry(out McpServerEntry entry)
    {
        entry = default!;
        var url = PART_McpUrl.Text?.Trim() ?? string.Empty;
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

        var name = string.IsNullOrWhiteSpace(PART_McpName.Text) ? "MCP Server" : PART_McpName.Text.Trim();
        var header = string.IsNullOrWhiteSpace(PART_McpHeader.Text) ? "Authorization" : PART_McpHeader.Text.Trim();
        var token = string.IsNullOrWhiteSpace(PART_McpToken.Text) ? null : PART_McpToken.Text.Trim();
        entry = new McpServerEntry(
            _editingMcpServer?.Id ?? Guid.NewGuid().ToString("N"),
            name,
            url,
            "http",
            header,
            token,
            PART_McpEnabled.IsChecked == true);
        return true;
    }

    private void SaveMcp()
    {
        if (!TryBuildMcpEntry(out var entry)) return;
        _settings.UpsertMcpServer(entry);
        CloseMcpEditor();
    }

    private async void OnDeleteMcp(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: McpServerEntry entry }) return;
        if (!await Confirm.AskAsync(this, $"删除「{entry.Name}」？", "该服务器的 Token 会一并移除。", "删除"))
            return;

        _settings.DeleteMcpServer(entry);
        RefreshMcpServers();
    }

    private async void OnTestMcp(object? sender, RoutedEventArgs e)
    {
        if (_mcpClient is null || !TryBuildMcpEntry(out var entry)) return;

        PART_TestMcp.IsEnabled = false;
        PART_TestMcp.Content = "连接中...";
        PART_McpStatus.Text = "正在连接...";
        PART_McpError.IsVisible = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = new McpServerOptions(
                entry.Id, entry.Name, entry.Url, entry.Transport,
                entry.HeaderName, entry.Token, entry.Enabled);
            var session = await _mcpClient.InitializeAsync(options, cts.Token);
            var tools = await _mcpClient.ListToolsAsync(session, cts.Token);
            PART_McpStatus.Text = $"连接成功，发现 {tools.Count} 个工具。";
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
        if (_pythonRuntime is null) return;

        PART_PythonRuntimeStatus.Text = "正在准备 MolaGPT 专用 Python 环境...";

        // The banner is what makes this survive closing the settings window:
        // the row below only exists while this page is open, and a 200 MB
        // download outlives it.
        _notifications?.Progress(PythonRuntimeNotificationKey, "正在配置 Python 环境", "获取清单…");
        try
        {
            var progress = new Progress<PythonRuntimeProgress>(item =>
            {
                PART_PythonRuntimeStatus.Text = string.IsNullOrWhiteSpace(item.Message)
                    ? $"正在配置 Python 运行时 {item.Progress:P0}"
                    : item.Message;
                _notifications?.Progress(
                    PythonRuntimeNotificationKey,
                    string.IsNullOrWhiteSpace(item.Message) ? "正在配置 Python 环境" : item.Message,
                    string.IsNullOrWhiteSpace(item.Stage) ? null : item.Stage,
                    item.Progress > 0 ? item.Progress : null);
            });
            var runtime = await _pythonRuntime.DownloadAndInstallAsync(progress, CancellationToken.None);
            _settings.PythonToolEnabled = true;
            _settings.PythonToolExecutablePath = runtime.PythonExecutablePath;
            _notifications?.Success("Python 环境已就绪", $"Python {runtime.Version}", PythonRuntimeNotificationKey);
        }
        catch (Exception ex)
        {
            PART_PythonRuntimeStatus.Text = "配置失败：" + ex.Message;
            _notifications?.Error("Python 环境配置失败", ex.Message, PythonRuntimeNotificationKey);
        }
    }

    private async Task ConfigurePiSidecarAsync()
    {
        if (_piSidecar is null) return;

        _notifications?.Progress(PiSidecarNotificationKey, "正在下载 Agent 运行环境");
        try
        {
            var progress = new Progress<SandboxProgress>(item =>
            {
                PART_PiSidecarStatus.Text = string.IsNullOrWhiteSpace(item.Message)
                    ? $"正在配置 Agent 运行环境 {item.Fraction:P0}"
                    : item.Message;
                _notifications?.Progress(
                    PiSidecarNotificationKey,
                    string.IsNullOrWhiteSpace(item.Message) ? "正在下载 Agent 运行环境" : item.Message,
                    progress: item.Fraction > 0 ? item.Fraction : null);
            });
            var installed = await _piSidecar.DownloadAndInstallAsync(progress, CancellationToken.None);
            if (_agentRuntimeInstalled is not null)
                await _agentRuntimeInstalled();
            PART_PiSidecarStatus.Text = $"Agent 运行环境 · {installed.Version}";
            _notifications?.Success("Agent 运行环境已就绪", installed.Version, PiSidecarNotificationKey);
        }
        catch (Exception ex)
        {
            PART_PiSidecarStatus.Text = "配置失败：" + ex.Message;
            _notifications?.Error("Agent 运行环境配置失败", ex.Message, PiSidecarNotificationKey);
        }
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
            _agentRuntimeRemoving?.Invoke();
            _piSidecar.Delete();
            _notifications?.Success("Agent 运行环境已移除", key: PiSidecarNotificationKey);
        }
        catch (Exception ex)
        {
            PART_PiSidecarStatus.Text = "移除失败：" + ex.Message;
        }
        EndSandboxOperation();
        await RefreshRuntimeStatusAsync();
    }

    private string? NormalizedPythonPath() =>
        _settings.PythonToolExecutablePath?.Trim().Trim('"');

    private void RefreshPythonBrowseButton()
    {
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
        _skills.Reload();
        PART_SkillsList.ItemsSource = _skills.Skills;
        if (PART_SkillsList.SelectedItem is null && _skills.Skills.Count > 0)
            PART_SkillsList.SelectedIndex = 0;
        RefreshSelectedSkill();
    }

    private void OnSkillSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshSelectedSkill();

    private void RefreshSelectedSkill()
    {
        var skill = PART_SkillsList.SelectedItem as SkillItemViewModel;
        PART_SkillDetail.DataContext = skill;
        PART_SkillDetail.IsVisible = skill is not null;
        PART_SkillEmpty.IsVisible = skill is null;
        PART_DeleteSkill.IsVisible = skill is { IsBuiltin: false };
        PART_SkillStatus.Text = string.Empty;
    }

    private async void OnImportSkill(object? sender, RoutedEventArgs e)
    {
        if (StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择技能压缩包（内含 SKILL.md）",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("技能压缩包") { Patterns = ["*.zip"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { Length: > 0 } path) return;

        try
        {
            var name = _skills.ImportFromPath(path);
            PART_SkillsList.SelectedItem = _skills.Skills.FirstOrDefault(skill => skill.Name == name);
            PART_SkillStatus.Text = $"已导入技能：{name}";
        }
        catch (Exception ex)
        {
            PART_SkillStatus.Text = "导入失败：" + ex.Message;
        }
    }

    private void OnOpenSkillsDirectory(object? sender, RoutedEventArgs e)
    {
        _skills.EnsureUserDirectoryForImport();
        OpenWithShell(_skills.UserSkillsDirectory);
    }

    private void OnRefreshSkills(object? sender, RoutedEventArgs e) => RefreshSkills();

    private void OnViewSkillMd(object? sender, RoutedEventArgs e)
    {
        if (PART_SkillsList.SelectedItem is not SkillItemViewModel skill) return;
        if (!File.Exists(skill.SkillMdPath))
        {
            PART_SkillStatus.Text = "找不到 SKILL.md 文件。";
            return;
        }
        RevealInExplorer(skill.SkillMdPath);
    }

    private async void OnDeleteSkill(object? sender, RoutedEventArgs e)
    {
        if (PART_SkillsList.SelectedItem is not SkillItemViewModel { IsBuiltin: false } skill) return;
        if (!await Confirm.AskAsync(
                this,
                $"删除「{skill.Name}」？",
                "该自定义技能及其文件夹会被永久删除。",
                "删除"))
        {
            return;
        }

        try
        {
            _skills.DeleteUserSkill(skill);
            PART_SkillsList.SelectedIndex = _skills.Skills.Count > 0 ? 0 : -1;
            RefreshSelectedSkill();
        }
        catch (Exception ex)
        {
            PART_SkillStatus.Text = "删除失败：" + ex.Message;
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
            PART_PersonaEditor.IsVisible = persona is not null;
            PART_PersonaEmpty.IsVisible = persona is null;
            PART_PersonaFooter.IsVisible = persona is not null;
            PART_PersonaEditor.DataContext = persona;

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
            PART_PersonaBuiltinHint.IsVisible = persona.IsBuiltin;
            PART_DuplicatePersona.IsEnabled = !isDraft;
            PART_DeletePersona.IsEnabled = editable && !isDraft;
            PART_SavePersona.IsEnabled = editable;
            PART_SavePersona.Content = isDraft ? "创建角色" : "保存角色";
        }
        finally
        {
            _loadingPersonaForm = false;
        }
    }

    private void OnNewPersona(object? sender, RoutedEventArgs e)
    {
        PersistEditingPersona();
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

        _editingPersona = next;
        _editingPersonaIsDraft = false;
        LoadPersonaForm(next);
    }

    private void OnSavePersona(object? sender, RoutedEventArgs e)
    {
        if (_editingPersona is null || _editingPersona.IsBuiltin) return;

        SyncPersonaForm();
        if (string.IsNullOrWhiteSpace(_editingPersona.Name)) _editingPersona.Name = "新角色";

        var saved = _editingPersona;
        _editingPersonaIsDraft = false;
        _personas.Save(saved);

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
        if (!_editingPersonaIsDraft) _personas.Save(_editingPersona);
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
        _editingPersona.DefaultEnableNetwork = PART_PersonaNetwork.IsChecked;
        _editingPersona.DefaultEnableWebFetch = PART_PersonaWebFetch.IsChecked;
        _editingPersona.DefaultThinking = PART_PersonaThinking.IsChecked;
    }

    private void PersistEditingPersona()
    {
        if (_editingPersona is null || _editingPersonaIsDraft || _editingPersona.IsBuiltin) return;
        SyncPersonaForm();
        _personas.Save(_editingPersona);
    }

    // ---- tool grants -------------------------------------------------------

    private void RefreshGrants() =>
        PART_NoGrants.IsVisible = _settings.AlwaysAllowedTools.Count == 0;

    private void OnRevokeGrant(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string key }) return;
        _settings.RevokeToolGrant(key);
        RefreshGrants();
    }
}

public sealed record PersonaIconRow(string Glyph, string Label);

public sealed record ProviderPresetRow(
    string Id,
    string Name,
    string Type,
    string BaseUrl,
    string ModelsPath,
    ThinkingParamKind DefaultThinkingKind,
    string Purpose = "chat",
    string? ApiPath = null,
    string? ImageEditPath = null,
    string? ImageFormat = null);

public sealed partial class DetectedModelRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public DetectedModelRow(ProviderModelEntry entry, bool isSelected, bool isEnabled, string statusText)
    {
        Entry = entry;
        _isSelected = isSelected;
        IsEnabled = isEnabled;
        StatusText = statusText;
    }

    public ProviderModelEntry Entry { get; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isSelected;
    public bool IsEnabled { get; }
    public string StatusText { get; }

    public string CapabilitySummary
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Vision) parts.Add("视觉");
            if (Entry.Tools) parts.Add("工具调用");
            if (Entry.Thinking) parts.Add("推理");
            if (Entry.ReasoningEffort) parts.Add("推理强度");
            if (Entry.ImageEdit) parts.Add("图像编辑");
            if (Entry.ContextWindow is { } context) parts.Add($"上下文 {context / 1000}K");
            return parts.Count == 0 ? "标准模型" : string.Join(" · ", parts);
        }
    }
}
