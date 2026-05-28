using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MolaGPT.Core.Auth;
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
    private const string WebSearchProviderKey = "web_search_provider";
    private const string WebSearchBaseUrlKey = "web_search_base_url";
    private const string WebSearchMaxResultsKey = "web_search_max_results";
    private const string WebPageMaxCharactersKey = "web_page_max_characters";
    private const string WebSearchSecretPrefix = "web_search_api_key:";

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
    [ObservableProperty] private string _webSearchProvider = "duckduckgo";
    [ObservableProperty] private string? _webSearchBaseUrl;
    [ObservableProperty] private string? _webSearchApiKey;
    [ObservableProperty] private int _webSearchMaxResults = 6;
    [ObservableProperty] private int _webPageMaxCharacters = 12000;

    public ObservableCollection<ProviderEntry> Providers { get; } = new();

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
            Providers.Add(new ProviderEntry(row.Id, row.Type, row.Name, row.BaseUrl, plainKey, models, row.Enabled, row.SortOrder));
        }
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
        }
        finally
        {
            _loadingSettings = false;
        }
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
            SortOrder: entry.SortOrder));
    }

    public void Delete(string id)
    {
        if (_repo is null) return;
        _repo.Delete(id);
        var existing = Providers.FirstOrDefault(p => p.Id == id);
        if (existing is not null) Providers.Remove(existing);
    }

    private static List<ProviderModelEntry> TryDeserializeModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ProviderModelEntry>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

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

public sealed record ProviderEntry(
    string Id,
    string Type,                                           // openai|openai-compat|anthropic|gemini
    string Name,
    string? BaseUrl,
    string? ApiKey,
    List<ProviderModelEntry> Models,
    bool Enabled,
    int SortOrder);

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
    string? SystemPrompt = null);
