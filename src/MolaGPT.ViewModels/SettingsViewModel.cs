using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels;

/// <summary>
/// Settings window VM. Tabs: Account / Providers / Appearance / Behavior.
/// Provider entries persist via <see cref="ProviderRepository"/>; API keys are
/// DPAPI-encrypted via <see cref="CredentialStore"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string SyncConversationsKey = "sync_conversations";
    private const string TracksEnabledKey = "molagpt_tracks_enabled";
    private const string CompletionNotificationKey = "completion_notification";
    private const string ThemeModeKey = "theme_mode";
    private const string TrayIconEnabledKey = "tray_icon_enabled";
    private const string TrayCloseBehaviorKey = "tray_close_behavior";
    private const string WebSearchProviderKey = "web_search_provider";
    private const string WebSearchBaseUrlKey = "web_search_base_url";
    private const string WebSearchMaxResultsKey = "web_search_max_results";
    private const string WebPageMaxCharactersKey = "web_page_max_characters";
    private const string WebSearchSecretPrefix = "web_search_api_key:";
    private const string ByokMcpServersKey = "byok_mcp_servers";
    private const string McpServerSecretPrefix = "mcp_server_token:";
    private const string VisionProxyEnabledKey = "vision_proxy_enabled";
    private const string VisionProxyProviderIdKey = "vision_proxy_provider_id";
    private const string VisionProxyModelIdKey = "vision_proxy_model_id";
    private const string ImageGenerationEnabledKey = "image_generation_enabled";
    private const string ImageGenerationProviderIdKey = "image_generation_provider_id";
    private const string ImageGenerationModelIdKey = "image_generation_model_id";
    private const string ImageGenerationSizeKey = "image_generation_size";
    private const string ImageGenerationStyleKey = "image_generation_style";
    private const string WorkbenchImageGenerationProviderIdKey = "image_workbench_provider_id";
    private const string WorkbenchImageGenerationModelIdKey = "image_workbench_model_id";
    private const string WorkbenchImageGenerationSizeKey = "image_workbench_size";
    private const string WorkbenchImageGenerationStyleKey = "image_workbench_style";
    private const string PythonToolEnabledKey = "python_tool_enabled";
    private const string FileToolsEnabledKey = "file_tools_enabled";
    private const string PythonToolExecutablePathKey = "python_tool_executable_path";
    private const string PythonToolTimeoutSecondsKey = "python_tool_timeout_seconds";
    private const string PythonToolMaxOutputCharactersKey = "python_tool_max_output_characters";
    private const string PythonToolAllowNetworkKey = "python_tool_allow_network";
    private const string ToolPermissionModeKey = "tool_permission_mode";
    private const string PythonToolPermissionModeKey = "python_tool_permission_mode";
    private const string ImageGenerationPermissionModeKey = "image_generation_permission_mode";
    private const string VisionPermissionModeKey = "vision_permission_mode";
    private const string McpPermissionModeKey = "mcp_permission_mode";
    private const string PythonExecutionPermissionModeKey = "python_execution_permission_mode";
    private const string PythonToolAllowedImportsKey = "python_tool_allowed_imports";
    private const string PythonToolDeniedImportsKey = "python_tool_denied_imports";
    private const string PythonToolAllowedPathPrefixesKey = "python_tool_allowed_path_prefixes";
    private const string PythonToolDeniedPathPrefixesKey = "python_tool_denied_path_prefixes";

    // Agent control layer (Claude Code / Codex CLI). Keys mirror DesktopAgentConfigProvider.
    private const string AgentClaudeCodePathKey = "agent.claudeCodePath";
    private const string AgentCodexPathKey = "agent.codexPath";
    private const string AgentPermissionModeKey = "agent.permissionMode";

    /// <summary>
    /// Raised whenever the user changes the theme mode in settings (or when
    /// <see cref="ThemeMode"/> is mutated programmatically by the header
    /// toggle button). The host listens to apply token dictionaries.
    /// </summary>
    public event EventHandler<ThemeMode>? ThemeModeChanged;

    [ObservableProperty] private string? _molaGptUsername;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private ThemeMode _themeMode = ThemeMode.System;
    [ObservableProperty] private bool _enterToSend = true;
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private bool _syncConversations = true;
    [ObservableProperty] private bool _tracksEnabled = true;
    [ObservableProperty] private bool _enableCompletionNotification = true;
    [ObservableProperty] private bool _enableTrayIcon;
    [ObservableProperty] private TrayCloseBehavior _trayCloseBehavior = TrayCloseBehavior.Ask;
    [ObservableProperty] private string _webSearchProvider = "duckduckgo";
    [ObservableProperty] private string? _webSearchBaseUrl;
    [ObservableProperty] private string? _webSearchApiKey;
    [ObservableProperty] private int _webSearchMaxResults = 6;
    [ObservableProperty] private int _webPageMaxCharacters = 12000;
    [ObservableProperty] private bool _visionProxyEnabled;
    [ObservableProperty] private string? _visionProxyProviderId;
    [ObservableProperty] private string? _visionProxyModelId;
    [ObservableProperty] private bool _imageGenerationEnabled;
    [ObservableProperty] private string? _imageGenerationProviderId;
    [ObservableProperty] private string? _imageGenerationModelId;
    [ObservableProperty] private string _imageGenerationSize = "1024x1024";
    [ObservableProperty] private string? _imageGenerationStyle;
    [ObservableProperty] private string? _workbenchImageGenerationProviderId;
    [ObservableProperty] private string? _workbenchImageGenerationModelId;
    [ObservableProperty] private string _workbenchImageGenerationSize = "1024x1024";
    [ObservableProperty] private string? _workbenchImageGenerationStyle;
    [ObservableProperty] private bool _pythonToolEnabled;
    [ObservableProperty] private bool _fileToolsEnabled;
    [ObservableProperty] private string? _pythonToolExecutablePath;
    [ObservableProperty] private int _pythonToolTimeoutSeconds = 60;
    [ObservableProperty] private int _pythonToolMaxOutputCharacters = 20000;
    [ObservableProperty] private bool _pythonToolAllowNetwork;
    [ObservableProperty] private ToolPermissionMode _localToolPermissionMode = ToolPermissionMode.Approval;
    [ObservableProperty] private ToolPermissionMode _imageGenerationPermissionMode = ToolPermissionMode.Approval;
    [ObservableProperty] private ToolPermissionMode _visionPermissionMode = ToolPermissionMode.Approval;
    [ObservableProperty] private ToolPermissionMode _mcpPermissionMode = ToolPermissionMode.Approval;
    [ObservableProperty] private ToolPermissionMode _pythonExecutionPermissionMode = ToolPermissionMode.Approval;
    [ObservableProperty] private string? _pythonToolAllowedImports;
    [ObservableProperty] private string? _pythonToolDeniedImports;
    [ObservableProperty] private string? _pythonToolAllowedPathPrefixes;
    [ObservableProperty] private string? _pythonToolDeniedPathPrefixes;

    [ObservableProperty] private string? _agentClaudeCodePath;
    [ObservableProperty] private string? _agentCodexPath;
    [ObservableProperty] private MolaGPT.Core.Chat.Agents.AgentPermissionMode _agentPermissionMode
        = MolaGPT.Core.Chat.Agents.AgentPermissionMode.AcceptEdits;

    public ObservableCollection<ProviderEntry> Providers { get; } = new();
    public ObservableCollection<McpServerEntry> McpServers { get; } = new();
    public ObservableCollection<VisionProviderModelOption> VisionProviderModels { get; } = new();
    public ObservableCollection<ImageGenerationProviderModelOption> ImageGenerationProviderModels { get; } = new();

    private readonly ProviderRepository? _repo;
    private readonly CredentialStore? _credentialStore;
    private readonly SettingsRepository? _settingsRepo;
    private bool _loadingSettings;

    public SettingsViewModel() { }

    public SettingsViewModel(ProviderRepository? repo, CredentialStore? credentialStore, SettingsRepository? settingsRepo = null)
    {
        _repo = repo;
        _credentialStore = credentialStore;
        _settingsRepo = settingsRepo;
        Reload();
    }

    public void Reload()
    {
        LoadSettings();
        Providers.Clear();
        if (_repo is null) return;
        foreach (var row in _repo.List())
        {
            string? plainKey = null;
            if (row.ApiKeyEnc is { Length: > 0 } && _credentialStore is not null)
                plainKey = _credentialStore.Decrypt(row.ApiKeyEnc);
            var models = TryDeserializeModels(row.Models);
            var customHeaders = TryDeserializeHeaders(row.CustomHeaders);
            Providers.Add(new ProviderEntry(row.Id, row.Type, row.Name, row.BaseUrl, plainKey, models, row.Enabled, row.SortOrder, row.Purpose, row.ApiPath, row.ImageEditPath, row.ImageFormat, customHeaders));
        }
        RefreshVisionProviderModels();
        RefreshImageGenerationProviderModels();
    }

    private void LoadSettings()
    {
        if (_settingsRepo is null) return;
        _loadingSettings = true;
        try
        {
            if (bool.TryParse(_settingsRepo.Get(SyncConversationsKey), out var syncConversations))
                SyncConversations = syncConversations;
            if (bool.TryParse(_settingsRepo.Get(TracksEnabledKey), out var tracksEnabled))
                TracksEnabled = tracksEnabled;
            if (bool.TryParse(_settingsRepo.Get(CompletionNotificationKey), out var completionNotification))
                EnableCompletionNotification = completionNotification;
            if (bool.TryParse(_settingsRepo.Get(TrayIconEnabledKey), out var trayIconEnabled))
                EnableTrayIcon = trayIconEnabled;
            var trayBehaviorRaw = _settingsRepo.Get(TrayCloseBehaviorKey);
            if (!string.IsNullOrEmpty(trayBehaviorRaw)
                && Enum.TryParse<TrayCloseBehavior>(trayBehaviorRaw, true, out var trayBehavior))
            {
                TrayCloseBehavior = trayBehavior;
            }
            var themeRaw = _settingsRepo.Get(ThemeModeKey);
            if (!string.IsNullOrEmpty(themeRaw) && Enum.TryParse<ThemeMode>(themeRaw, true, out var theme))
                ThemeMode = theme;
            WebSearchProvider = _settingsRepo.Get(WebSearchProviderKey) ?? "duckduckgo";
            WebSearchBaseUrl = _settingsRepo.Get(WebSearchBaseUrlKey) ?? DefaultWebSearchBaseUrl(WebSearchProvider);
            if (int.TryParse(_settingsRepo.Get(WebSearchMaxResultsKey), out var maxResults))
                WebSearchMaxResults = Math.Clamp(maxResults, 1, 10);
            if (int.TryParse(_settingsRepo.Get(WebPageMaxCharactersKey), out var maxChars))
                WebPageMaxCharacters = Math.Clamp(maxChars, 1000, 30000);
            WebSearchApiKey = _credentialStore?.LoadSecret(WebSearchSecretPrefix + WebSearchProvider);
            LoadMcpServers();
            VisionProxyEnabled = bool.TryParse(_settingsRepo.Get(VisionProxyEnabledKey), out var visionEnabled) && visionEnabled;
            VisionProxyProviderId = _settingsRepo.Get(VisionProxyProviderIdKey);
            VisionProxyModelId = _settingsRepo.Get(VisionProxyModelIdKey);
            ImageGenerationEnabled = bool.TryParse(_settingsRepo.Get(ImageGenerationEnabledKey), out var imageGenEnabled) && imageGenEnabled;
            ImageGenerationProviderId = _settingsRepo.Get(ImageGenerationProviderIdKey);
            ImageGenerationModelId = _settingsRepo.Get(ImageGenerationModelIdKey);
            ImageGenerationSize = _settingsRepo.Get(ImageGenerationSizeKey) ?? "1024x1024";
            ImageGenerationStyle = _settingsRepo.Get(ImageGenerationStyleKey);
            WorkbenchImageGenerationProviderId = _settingsRepo.Get(WorkbenchImageGenerationProviderIdKey) ?? ImageGenerationProviderId;
            WorkbenchImageGenerationModelId = _settingsRepo.Get(WorkbenchImageGenerationModelIdKey) ?? ImageGenerationModelId;
            WorkbenchImageGenerationSize = _settingsRepo.Get(WorkbenchImageGenerationSizeKey) ?? ImageGenerationSize;
            WorkbenchImageGenerationStyle = _settingsRepo.Get(WorkbenchImageGenerationStyleKey) ?? ImageGenerationStyle;
            PythonToolEnabled = bool.TryParse(_settingsRepo.Get(PythonToolEnabledKey), out var pythonEnabled) && pythonEnabled;
            FileToolsEnabled = bool.TryParse(_settingsRepo.Get(FileToolsEnabledKey), out var fileToolsEnabled) && fileToolsEnabled;
            PythonToolExecutablePath = _settingsRepo.Get(PythonToolExecutablePathKey);
            if (int.TryParse(_settingsRepo.Get(PythonToolTimeoutSecondsKey), out var pythonTimeout))
                PythonToolTimeoutSeconds = Math.Clamp(pythonTimeout, 5, 300);
            if (int.TryParse(_settingsRepo.Get(PythonToolMaxOutputCharactersKey), out var pythonMaxOutput))
                PythonToolMaxOutputCharacters = Math.Clamp(pythonMaxOutput, 2000, 100000);
            PythonToolAllowNetwork = bool.TryParse(_settingsRepo.Get(PythonToolAllowNetworkKey), out var pythonAllowNetwork) && pythonAllowNetwork;
            var permissionModeRaw = _settingsRepo.Get(ToolPermissionModeKey)
                                    ?? _settingsRepo.Get(PythonToolPermissionModeKey);
            if (!string.IsNullOrWhiteSpace(permissionModeRaw)
                && Enum.TryParse<ToolPermissionMode>(permissionModeRaw, true, out var permissionMode))
            {
                LocalToolPermissionMode = permissionMode;
            }
            if (Enum.TryParse<ToolPermissionMode>(_settingsRepo.Get(ImageGenerationPermissionModeKey), true, out var imgMode))
                ImageGenerationPermissionMode = imgMode;
            if (Enum.TryParse<ToolPermissionMode>(_settingsRepo.Get(VisionPermissionModeKey), true, out var visMode))
                VisionPermissionMode = visMode;
            if (Enum.TryParse<ToolPermissionMode>(_settingsRepo.Get(McpPermissionModeKey), true, out var mcpMode))
                McpPermissionMode = mcpMode;
            if (Enum.TryParse<ToolPermissionMode>(_settingsRepo.Get(PythonExecutionPermissionModeKey), true, out var pyMode))
                PythonExecutionPermissionMode = pyMode;
            PythonToolAllowedImports = _settingsRepo.Get(PythonToolAllowedImportsKey);
            PythonToolDeniedImports = _settingsRepo.Get(PythonToolDeniedImportsKey);
            PythonToolAllowedPathPrefixes = _settingsRepo.Get(PythonToolAllowedPathPrefixesKey);
            PythonToolDeniedPathPrefixes = _settingsRepo.Get(PythonToolDeniedPathPrefixesKey);

            AgentClaudeCodePath = _settingsRepo.Get(AgentClaudeCodePathKey);
            AgentCodexPath = _settingsRepo.Get(AgentCodexPathKey);
            if (Enum.TryParse<MolaGPT.Core.Chat.Agents.AgentPermissionMode>(
                    _settingsRepo.Get(AgentPermissionModeKey), true, out var agentMode))
                AgentPermissionMode = agentMode;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void LoadMcpServers()
    {
        McpServers.Clear();
        var json = _settingsRepo?.Get(ByokMcpServersKey);
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<McpServerEntry>>(json) ?? new();
            foreach (var entry in entries)
            {
                McpServers.Add(entry with
                {
                    Token = _credentialStore?.LoadSecret(McpServerSecretPrefix + entry.Id)
                });
            }
        }
        catch (JsonException) { }
    }

    partial void OnSyncConversationsChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(SyncConversationsKey, value.ToString());
    }

    partial void OnTracksEnabledChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(TracksEnabledKey, value.ToString());
    }

    partial void OnEnableCompletionNotificationChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(CompletionNotificationKey, value.ToString());
    }

    partial void OnEnableTrayIconChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(TrayIconEnabledKey, value.ToString());
    }

    partial void OnTrayCloseBehaviorChanged(TrayCloseBehavior value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(TrayCloseBehaviorKey, value.ToString());
    }

    partial void OnThemeModeChanged(ThemeMode value)
    {
        // Always raise the change event — the host needs to re-apply the
        // token dictionary even on the very first selection (which happens
        // before _settingsRepo persists a value), and even when the
        // settings repo is unavailable in test contexts.
        ThemeModeChanged?.Invoke(this, value);
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(ThemeModeKey, value.ToString());
    }

    partial void OnWebSearchProviderChanged(string value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        var provider = NormalizeWebSearchProvider(value);
        _settingsRepo.Set(WebSearchProviderKey, provider);
        WebSearchBaseUrl = DefaultWebSearchBaseUrl(provider);
        WebSearchApiKey = _credentialStore?.LoadSecret(WebSearchSecretPrefix + provider);
    }

    partial void OnWebSearchBaseUrlChanged(string? value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        if (string.IsNullOrWhiteSpace(value))
            _settingsRepo.Remove(WebSearchBaseUrlKey);
        else
            _settingsRepo.Set(WebSearchBaseUrlKey, value.Trim());
    }

    partial void OnWebSearchApiKeyChanged(string? value)
    {
        if (_loadingSettings || _credentialStore is null) return;
        var key = WebSearchSecretPrefix + NormalizeWebSearchProvider(WebSearchProvider);
        if (string.IsNullOrWhiteSpace(value))
            _credentialStore.RemoveSecret(key);
        else
            _credentialStore.SaveSecret(key, value.Trim());
    }

    partial void OnWebSearchMaxResultsChanged(int value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        WebSearchMaxResults = Math.Clamp(value, 1, 10);
        _settingsRepo.Set(WebSearchMaxResultsKey, WebSearchMaxResults.ToString());
    }

    partial void OnWebPageMaxCharactersChanged(int value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        WebPageMaxCharacters = Math.Clamp(value, 1000, 30000);
        _settingsRepo.Set(WebPageMaxCharactersKey, WebPageMaxCharacters.ToString());
    }

    partial void OnVisionProxyEnabledChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(VisionProxyEnabledKey, value.ToString());
    }

    partial void OnVisionProxyProviderIdChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(VisionProxyProviderIdKey, value);
    }

    partial void OnVisionProxyModelIdChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(VisionProxyModelIdKey, value);
    }

    partial void OnImageGenerationEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsImageGenerationConfigured));
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(ImageGenerationEnabledKey, value.ToString());
    }

    partial void OnImageGenerationProviderIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsImageGenerationConfigured));
        if (_loadingSettings) return;
        SetOrRemove(ImageGenerationProviderIdKey, value);

        if (_loadingSettings) return;
        var selected = ImageGenerationProviderModels.FirstOrDefault(m =>
            string.Equals(m.ProviderId, value, StringComparison.Ordinal));
        if (selected is not null && !string.Equals(ImageGenerationModelId, selected.ModelId, StringComparison.Ordinal))
            ImageGenerationModelId = selected.ModelId;
    }

    partial void OnImageGenerationModelIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsImageGenerationConfigured));
        if (_loadingSettings) return;
        SetOrRemove(ImageGenerationModelIdKey, value);
    }

    partial void OnImageGenerationSizeChanged(string value)
    {
        if (_loadingSettings) return;
        SetOrRemove(ImageGenerationSizeKey, value);
    }

    partial void OnImageGenerationStyleChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(ImageGenerationStyleKey, value);
    }

    partial void OnWorkbenchImageGenerationProviderIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsWorkbenchImageGenerationConfigured));
        if (_loadingSettings) return;
        SetOrRemove(WorkbenchImageGenerationProviderIdKey, value);

        var selected = ImageGenerationProviderModels.FirstOrDefault(m =>
            string.Equals(m.ProviderId, value, StringComparison.Ordinal));
        if (selected is not null && !string.Equals(WorkbenchImageGenerationModelId, selected.ModelId, StringComparison.Ordinal))
            WorkbenchImageGenerationModelId = selected.ModelId;
    }

    partial void OnWorkbenchImageGenerationModelIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsWorkbenchImageGenerationConfigured));
        if (_loadingSettings) return;
        SetOrRemove(WorkbenchImageGenerationModelIdKey, value);
    }

    partial void OnWorkbenchImageGenerationSizeChanged(string value)
    {
        if (_loadingSettings) return;
        SetOrRemove(WorkbenchImageGenerationSizeKey, value);
    }

    partial void OnWorkbenchImageGenerationStyleChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(WorkbenchImageGenerationStyleKey, value);
    }

    partial void OnPythonToolEnabledChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(PythonToolEnabledKey, value.ToString());
    }

    partial void OnFileToolsEnabledChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(FileToolsEnabledKey, value.ToString());
    }

    partial void OnPythonToolExecutablePathChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(PythonToolExecutablePathKey, value);
    }

    partial void OnPythonToolTimeoutSecondsChanged(int value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        var clamped = Math.Clamp(value, 5, 300);
        if (clamped != value)
        {
            PythonToolTimeoutSeconds = clamped;
            return;
        }
        _settingsRepo.Set(PythonToolTimeoutSecondsKey, clamped.ToString());
    }

    partial void OnPythonToolMaxOutputCharactersChanged(int value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        var clamped = Math.Clamp(value, 2000, 100000);
        if (clamped != value)
        {
            PythonToolMaxOutputCharacters = clamped;
            return;
        }
        _settingsRepo.Set(PythonToolMaxOutputCharactersKey, clamped.ToString());
    }

    partial void OnPythonToolAllowNetworkChanged(bool value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(PythonToolAllowNetworkKey, value.ToString());
    }

    partial void OnLocalToolPermissionModeChanged(ToolPermissionMode value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(ToolPermissionModeKey, value.ToString());
    }

    partial void OnImageGenerationPermissionModeChanged(ToolPermissionMode value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(ImageGenerationPermissionModeKey, value.ToString());
    }

    partial void OnVisionPermissionModeChanged(ToolPermissionMode value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(VisionPermissionModeKey, value.ToString());
    }

    partial void OnMcpPermissionModeChanged(ToolPermissionMode value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(McpPermissionModeKey, value.ToString());
    }

    partial void OnPythonExecutionPermissionModeChanged(ToolPermissionMode value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(PythonExecutionPermissionModeKey, value.ToString());
    }

    partial void OnPythonToolAllowedImportsChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(PythonToolAllowedImportsKey, value);
    }

    partial void OnPythonToolDeniedImportsChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(PythonToolDeniedImportsKey, value);
    }

    partial void OnAgentClaudeCodePathChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(AgentClaudeCodePathKey, value);
    }

    partial void OnAgentCodexPathChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(AgentCodexPathKey, value);
    }

    partial void OnAgentPermissionModeChanged(MolaGPT.Core.Chat.Agents.AgentPermissionMode value)
    {
        if (_loadingSettings || _settingsRepo is null) return;
        _settingsRepo.Set(AgentPermissionModeKey, value.ToString());
    }

    partial void OnPythonToolAllowedPathPrefixesChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(PythonToolAllowedPathPrefixesKey, value);
    }

    partial void OnPythonToolDeniedPathPrefixesChanged(string? value)
    {
        if (_loadingSettings) return;
        SetOrRemove(PythonToolDeniedPathPrefixesKey, value);
    }

    public static string NormalizeWebSearchProvider(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
            ? "duckduckgo"
            : provider.Trim().ToLowerInvariant() switch
            {
                "tavily" => "tavily",
                "exa" => "exa",
                _ => "duckduckgo"
            };

    public static string DefaultWebSearchBaseUrl(string? provider) =>
        NormalizeWebSearchProvider(provider) switch
        {
            "tavily" => "https://api.tavily.com",
            "exa" => "https://api.exa.ai",
            _ => string.Empty
        };

    public void Save(ProviderEntry entry)
    {
        if (_repo is null) return;
        byte[]? cipher = null;
        if (!string.IsNullOrEmpty(entry.ApiKey) && _credentialStore is not null)
            cipher = _credentialStore.Encrypt(entry.ApiKey);

        _repo.Upsert(new MolaGPT.Storage.ProviderRow(
            Id: entry.Id,
            Type: entry.Type,
            Name: entry.Name,
            BaseUrl: entry.BaseUrl,
            ApiKeyEnc: cipher,
            Models: JsonSerializer.Serialize(entry.Models),
            Enabled: entry.Enabled,
            SortOrder: entry.SortOrder,
            Purpose: entry.Purpose,
            ApiPath: entry.ApiPath,
            ImageEditPath: entry.ImageEditPath,
            ImageFormat: entry.ImageFormat,
            CustomHeaders: entry.CustomHeaders is { Count: > 0 }
                ? JsonSerializer.Serialize(entry.CustomHeaders)
                : null));
        RefreshVisionProviderModels();
        RefreshImageGenerationProviderModels();

        if (string.Equals(entry.Purpose, "image", StringComparison.OrdinalIgnoreCase))
        {
            ImageGenerationEnabled = true;
            ImageGenerationProviderId = entry.Id;
            var firstModel = entry.Models.FirstOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(firstModel))
            {
                ImageGenerationModelId = firstModel;
                if (string.IsNullOrWhiteSpace(WorkbenchImageGenerationProviderId))
                {
                    WorkbenchImageGenerationProviderId = entry.Id;
                    WorkbenchImageGenerationModelId = firstModel;
                }
            }
        }
    }

    public void UpsertMcpServer(McpServerEntry entry)
    {
        var existing = McpServers.FirstOrDefault(s => s.Id == entry.Id);
        if (existing is not null) McpServers.Remove(existing);
        McpServers.Add(entry);
        SaveMcpServers();
    }

    public void DeleteMcpServer(McpServerEntry entry)
    {
        McpServers.Remove(entry);
        _credentialStore?.RemoveSecret(McpServerSecretPrefix + entry.Id);
        SaveMcpServers();
    }

    public IReadOnlyList<McpServerOptions> BuildMcpServerOptions() =>
        McpServers
            .Select(s => new McpServerOptions(s.Id, s.Name, s.Url, s.Transport, s.HeaderName, s.Token, s.Enabled))
            .ToArray();

    public VisionProxyOptions BuildVisionProxyOptions() => new(
        VisionProxyEnabled,
        VisionProxyProviderId,
        VisionProxyModelId);

    public ImageGenerationOptions BuildImageGenerationOptions()
    {
        var provider = GetImageGenerationProvider();
        return new ImageGenerationOptions(
            ImageGenerationEnabled,
            provider?.BaseUrl,
            provider?.ApiKey,
            ImageGenerationModelId,
            string.IsNullOrWhiteSpace(ImageGenerationSize) ? "1024x1024" : ImageGenerationSize.Trim(),
            ImageGenerationStyle,
            true,
            SelectedImageGenerationModel?.SupportsEdit == true,
            provider?.ImageFormat,
            provider?.ApiPath,
            provider?.ImageEditPath);
    }

    public ImageGenerationOptions BuildWorkbenchImageGenerationOptions()
    {
        var provider = GetWorkbenchImageGenerationProvider();
        return new ImageGenerationOptions(
            true,
            provider?.BaseUrl,
            provider?.ApiKey,
            WorkbenchImageGenerationModelId,
            string.IsNullOrWhiteSpace(WorkbenchImageGenerationSize) ? "1024x1024" : WorkbenchImageGenerationSize.Trim(),
            WorkbenchImageGenerationStyle,
            false,
            SelectedWorkbenchImageGenerationModel?.SupportsEdit == true,
            provider?.ImageFormat,
            provider?.ApiPath,
            provider?.ImageEditPath);
    }

    public PythonExecutionOptions BuildPythonExecutionOptions() => new(
        PythonToolEnabled,
        PythonToolExecutablePath,
        Math.Clamp(PythonToolTimeoutSeconds, 5, 300),
        Math.Clamp(PythonToolMaxOutputCharacters, 2000, 100000),
        PythonToolAllowNetwork,
        LocalToolPermissionMode == ToolPermissionMode.FullAccess || PythonExecutionPermissionMode == ToolPermissionMode.FullAccess
            ? PythonPermissionMode.FullAccess
            : PythonPermissionMode.Approval,
        PythonToolAllowedImports,
        PythonToolDeniedImports,
        PythonToolAllowedPathPrefixes,
        PythonToolDeniedPathPrefixes);

    public ImageGenerationProviderModelOption? SelectedImageGenerationModel =>
        ImageGenerationProviderModels.FirstOrDefault(m =>
            string.Equals(m.ProviderId, ImageGenerationProviderId, StringComparison.Ordinal)
            && string.Equals(m.ModelId, ImageGenerationModelId, StringComparison.Ordinal));

    public ImageGenerationProviderModelOption? SelectedWorkbenchImageGenerationModel =>
        ImageGenerationProviderModels.FirstOrDefault(m =>
            string.Equals(m.ProviderId, WorkbenchImageGenerationProviderId, StringComparison.Ordinal)
            && string.Equals(m.ModelId, WorkbenchImageGenerationModelId, StringComparison.Ordinal));

    public string? ImageGenerationBaseUrl
    {
        get => GetImageGenerationProvider()?.BaseUrl;
        set
        {
            var provider = GetImageGenerationProvider();
            if (provider is null) return;
            Save(provider with { BaseUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
        }
    }

    public string? ImageGenerationApiKey
    {
        get => GetImageGenerationProvider()?.ApiKey;
        set
        {
            var provider = GetImageGenerationProvider();
            if (provider is null) return;
            Save(provider with { ApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
        }
    }

    public string? ImageGenerationModel
    {
        get => ImageGenerationModelId;
        set => ImageGenerationModelId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool IsImageGenerationConfigured =>
        ImageGenerationEnabled
        && !string.IsNullOrWhiteSpace(ImageGenerationProviderId)
        && !string.IsNullOrWhiteSpace(ImageGenerationModelId)
        && GetImageGenerationProvider() is { BaseUrl: { Length: > 0 }, ApiKey: { Length: > 0 } }
        && ImageGenerationProviderModels.Any(m =>
            string.Equals(m.ProviderId, ImageGenerationProviderId, StringComparison.Ordinal)
            && string.Equals(m.ModelId, ImageGenerationModelId, StringComparison.Ordinal));

    public bool IsWorkbenchImageGenerationConfigured =>
        !string.IsNullOrWhiteSpace(WorkbenchImageGenerationProviderId)
        && !string.IsNullOrWhiteSpace(WorkbenchImageGenerationModelId)
        && GetWorkbenchImageGenerationProvider() is { BaseUrl: { Length: > 0 }, ApiKey: { Length: > 0 } }
        && ImageGenerationProviderModels.Any(m =>
            string.Equals(m.ProviderId, WorkbenchImageGenerationProviderId, StringComparison.Ordinal)
            && string.Equals(m.ModelId, WorkbenchImageGenerationModelId, StringComparison.Ordinal));

    public bool IsVisionProxyAvailableFor(ProviderKind? providerKind, ProviderModel? model) =>
        providerKind == ProviderKind.OpenAICompatible
        && model?.SupportsToolCalling == true
        && model.SupportsVision != true
        && VisionProxyEnabled;

    private void SaveMcpServers()
    {
        if (_settingsRepo is null) return;
        var publicEntries = McpServers.Select(s => s with { Token = null }).ToList();
        _settingsRepo.Set(ByokMcpServersKey, JsonSerializer.Serialize(publicEntries));
        if (_credentialStore is null) return;
        foreach (var server in McpServers)
        {
            var key = McpServerSecretPrefix + server.Id;
            if (string.IsNullOrWhiteSpace(server.Token))
                _credentialStore.RemoveSecret(key);
            else
                _credentialStore.SaveSecret(key, server.Token.Trim());
        }
    }

    private void SetOrRemove(string key, string? value)
    {
        if (_settingsRepo is null) return;
        if (string.IsNullOrWhiteSpace(value))
            _settingsRepo.Remove(key);
        else
            _settingsRepo.Set(key, value.Trim());
    }

    public void RefreshVisionProviderModels()
    {
        VisionProviderModels.Clear();
        foreach (var provider in Providers.Where(p => p.Enabled))
        {
            foreach (var model in provider.Models.Where(m => m.Vision))
            {
                VisionProviderModels.Add(new VisionProviderModelOption(
                    provider.Id,
                    model.Id,
                    $"{provider.Name} / {model.DisplayName}"));
            }
        }
    }

    public void RefreshImageGenerationProviderModels()
    {
        ImageGenerationProviderModels.Clear();
        foreach (var provider in Providers.Where(p => p.Enabled && IsImagePurpose(p.Purpose)))
        {
            foreach (var model in provider.Models)
            {
                ImageGenerationProviderModels.Add(new ImageGenerationProviderModelOption(
                    provider.Id,
                    model.Id,
                    $"{provider.Name} / {model.DisplayName}",
                    model.ImageEdit));
            }
        }

        if (ImageGenerationProviderModels.Count == 0)
            return;

        EnsureImageGenerationSelection();
        EnsureWorkbenchImageGenerationSelection();
    }

    public ProviderEntry? GetImageGenerationProvider() =>
        GetImageProvider(ImageGenerationProviderId);

    public ProviderEntry? GetWorkbenchImageGenerationProvider() =>
        GetImageProvider(WorkbenchImageGenerationProviderId);

    private ProviderEntry? GetImageProvider(string? providerId) =>
        Providers.FirstOrDefault(p =>
            string.Equals(p.Id, providerId, StringComparison.Ordinal)
            && p.Enabled
            && IsImagePurpose(p.Purpose));

    public void RefreshImageGenerationSelection()
    {
        if (ImageGenerationProviderModels.Count == 0) return;

        EnsureImageGenerationSelection();
        EnsureWorkbenchImageGenerationSelection();
    }

    private void EnsureImageGenerationSelection()
    {
        if (!ImageGenerationProviderModels.Any(m => string.Equals(m.ProviderId, ImageGenerationProviderId, StringComparison.Ordinal)
                                                   && string.Equals(m.ModelId, ImageGenerationModelId, StringComparison.Ordinal)))
        {
            var first = ImageGenerationProviderModels[0];
            if (!string.Equals(ImageGenerationProviderId, first.ProviderId, StringComparison.Ordinal))
                ImageGenerationProviderId = first.ProviderId;
            if (!string.Equals(ImageGenerationModelId, first.ModelId, StringComparison.Ordinal))
                ImageGenerationModelId = first.ModelId;
        }
    }

    private void EnsureWorkbenchImageGenerationSelection()
    {
        if (!ImageGenerationProviderModels.Any(m => string.Equals(m.ProviderId, WorkbenchImageGenerationProviderId, StringComparison.Ordinal)
                                                   && string.Equals(m.ModelId, WorkbenchImageGenerationModelId, StringComparison.Ordinal)))
        {
            var first = ImageGenerationProviderModels[0];
            if (!string.Equals(WorkbenchImageGenerationProviderId, first.ProviderId, StringComparison.Ordinal))
                WorkbenchImageGenerationProviderId = first.ProviderId;
            if (!string.Equals(WorkbenchImageGenerationModelId, first.ModelId, StringComparison.Ordinal))
                WorkbenchImageGenerationModelId = first.ModelId;
        }
    }

    public void Delete(string id)
    {
        if (_repo is null) return;
        _repo.Delete(id);
        var existing = Providers.FirstOrDefault(p => p.Id == id);
        if (existing is not null) Providers.Remove(existing);
        RefreshImageGenerationProviderModels();

        if (string.Equals(ImageGenerationProviderId, id, StringComparison.Ordinal))
        {
            ImageGenerationProviderId = null;
            ImageGenerationModelId = null;
        }
        if (string.Equals(WorkbenchImageGenerationProviderId, id, StringComparison.Ordinal))
        {
            WorkbenchImageGenerationProviderId = null;
            WorkbenchImageGenerationModelId = null;
        }
    }

    private static List<ProviderModelEntry> TryDeserializeModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ProviderModelEntry>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static List<CustomHeaderEntry>? TryDeserializeHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<CustomHeaderEntry>>(json); }
        catch (JsonException) { return null; }
    }

    public static bool IsImagePurpose(string? purpose) =>
        string.Equals(purpose, "image", StringComparison.OrdinalIgnoreCase);

    public string? GetModelSystemPrompt(string? providerId, string? modelId)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId)) return null;
        var provider = Providers.FirstOrDefault(p =>
            string.Equals(p.Id, providerId, StringComparison.Ordinal));
        return provider?.Models.FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.Ordinal))?.SystemPrompt;
    }
}

public enum ThemeMode { System, Light, Dark }

public enum TrayCloseBehavior
{
    Ask,
    MinimizeToTray,
    Exit
}

public sealed record ProviderEntry(
    string Id,
    string Type,                                           // 协议族: openai-compat|anthropic|gemini
    string Name,
    string? BaseUrl,
    string? ApiKey,
    List<ProviderModelEntry> Models,
    bool Enabled,
    int SortOrder,
    string Purpose = "chat",                               // 用途: chat|image
    string? ApiPath = null,                                // chat: 对话路径; image: 生成路径 (含版本段)
    string? ImageEditPath = null,                          // image(openai-images): 编辑路径
    string? ImageFormat = null,                            // image: openai-images|openai-chat-image
    List<CustomHeaderEntry>? CustomHeaders = null);        // BYOK: 附加到该服务全部请求的自定义请求头

public sealed record ProviderModelEntry(
    string Id,
    string DisplayName,
    bool Vision = false,
    int? ContextWindow = null,
    bool Thinking = false,
    bool ReasoningEffort = false,
    bool Tools = false,
    string? ThinkingParamKind = null,
    int? ThinkingBudgetMin = null,
    int? ThinkingBudgetMax = null,
    int? ThinkingBudgetDefault = null,
    string? DefaultEffort = null,
    string? SystemPrompt = null,
    bool ImageEdit = false,
    List<CustomBodyEntry>? CustomBody = null,
    List<string>? EffortLevels = null);             // BYOK: 覆写/追加推理强度档位（如 max、ultra）

/// <summary>A user-defined HTTP header appended to a BYOK provider's requests.</summary>
public sealed record CustomHeaderEntry(string Name = "", string Value = "");

/// <summary>A user-defined request-body override for a BYOK model. <see cref="Type"/>
/// is one of string|number|boolean|json and controls how <see cref="Value"/> is parsed.</summary>
public sealed record CustomBodyEntry(string Key = "", string Type = "string", string Value = "");

/// <summary>Converts persisted <see cref="CustomBodyEntry"/>/<see cref="CustomHeaderEntry"/>
/// lists into the runtime forms the providers consume.</summary>
public static class CustomParamConverter
{
    public static IReadOnlyDictionary<string, System.Text.Json.JsonElement>? ToBodyDict(IReadOnlyList<CustomBodyEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return null;
        var dict = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Key)) continue;
            dict[e.Key.Trim()] = ConvertValue(e.Type, e.Value);
        }
        return dict.Count == 0 ? null : dict;
    }

    public static IReadOnlyList<KeyValuePair<string, string>>? ToHeaderList(IReadOnlyList<CustomHeaderEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return null;
        var list = entries
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .Select(h => new KeyValuePair<string, string>(h.Name.Trim(), h.Value ?? string.Empty))
            .ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>Deserializes a persisted <c>custom_headers</c> JSON blob into the runtime
    /// header list. Tolerant: returns null on missing/invalid JSON.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? ToHeaderListFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return ToHeaderList(System.Text.Json.JsonSerializer.Deserialize<List<CustomHeaderEntry>>(json)); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static System.Text.Json.JsonElement ConvertValue(string? type, string? value)
    {
        value ??= string.Empty;
        switch (type?.Trim().ToLowerInvariant())
        {
            case "number":
                if (long.TryParse(value, out var l))
                    return System.Text.Json.JsonSerializer.SerializeToElement(l);
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return System.Text.Json.JsonSerializer.SerializeToElement(d);
                return System.Text.Json.JsonSerializer.SerializeToElement(value);
            case "boolean":
                return System.Text.Json.JsonSerializer.SerializeToElement(bool.TryParse(value, out var b) && b);
            case "json":
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(value);
                    return doc.RootElement.Clone();
                }
                catch (System.Text.Json.JsonException)
                {
                    return System.Text.Json.JsonSerializer.SerializeToElement(value);
                }
            default: // "string"
                return System.Text.Json.JsonSerializer.SerializeToElement(value);
        }
    }
}

public sealed record McpServerEntry(
    string Id,
    string Name,
    string Url,
    string Transport = "http",
    string HeaderName = "Authorization",
    string? Token = null,
    bool Enabled = true);

public sealed record VisionProviderModelOption(
    string ProviderId,
    string ModelId,
    string Label);

public sealed record ImageGenerationProviderModelOption(
    string ProviderId,
    string ModelId,
    string Label,
    bool SupportsEdit = false);
