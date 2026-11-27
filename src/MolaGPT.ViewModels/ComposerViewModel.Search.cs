using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;

namespace MolaGPT.ViewModels;

public sealed partial class ComposerViewModel
{
    private static readonly (string Id, string Name)[] SearchProviders =
    [
        ("molagpt", "MolaGPT Web Search"),
        ("duckduckgo", "DuckDuckGo"),
        ("deepseek", "DeepSeek Search"),
        ("tavily", "Tavily"),
        ("exa", "Exa")
    ];

    public string? ConversationSearchProvider => NormalizeConversationSearchProvider(SubjectRoleContext.SearchProvider);

    public string GlobalSearchProvider
    {
        get
        {
            var provider = SettingsViewModel.NormalizeWebSearchProvider(_settings?.WebSearchProvider);
            return provider == "molagpt" && _settings?.IsLoggedIn != true ? "duckduckgo" : provider;
        }
    }

    private ProviderEntry? ActiveByokProviderEntry
    {
        get
        {
            var provider = SubjectProvider;
            if (provider is null || provider.ToAppMode() != AppMode.Byok) return null;
            return _settings?.Providers.FirstOrDefault(item =>
                item.Enabled && string.Equals(item.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public BuiltinSearchVendor? ModelBuiltinSearchVendor
    {
        get
        {
            if (SubjectModel is not { SupportsBuiltinSearch: true }
                || ActiveByokProviderEntry is not { } entry
                || !BuiltinWebSearch.IsEnabledOnProvider(entry.Type, entry.BaseUrl, entry.BuiltinSearch))
            {
                return null;
            }

            return BuiltinWebSearch.DetectVendor(entry.BaseUrl);
        }
    }

    public bool HasModelBuiltinSearch => ModelBuiltinSearchVendor is not null;

    private string? AvailableConversationSearchProvider
    {
        get
        {
            var selected = ConversationSearchProvider;
            return selected is not null && IsSearchProviderConfigured(selected) ? selected : null;
        }
    }

    public BuiltinSearchVendor? ActiveBuiltinSearchVendor =>
        AvailableConversationSearchProvider is null ? ModelBuiltinSearchVendor : null;

    public bool IsBuiltinSearchActive => ActiveBuiltinSearchVendor is not null;

    public string? ActiveBuiltinSearchProviderId =>
        HasModelBuiltinSearch ? ActiveByokProviderEntry?.Id : null;

    public bool IsThinkingForcedByBuiltinSearch =>
        EnableNetwork
        && AreNetworkToolsEnabled
        && ActiveBuiltinSearchVendor is { } vendor
        && BuiltinWebSearch.ForcesThinking(vendor, SubjectModel?.Id);

    public string ActiveSearchProvider =>
        ActiveBuiltinSearchVendor is { } vendor
            ? BuiltinWebSearch.ProviderId(vendor)
            : AvailableConversationSearchProvider ?? GlobalSearchProvider;

    public string ActiveSearchProviderLabel =>
        ActiveBuiltinSearchVendor is { } vendor
            ? BuiltinWebSearch.Label(vendor)
            : SearchProviderLabel(ActiveSearchProvider);

    public bool IsSearchProviderSwitchVisible =>
        SubjectProvider is { } provider && provider.ToAppMode().IsLocalAgent()
        && SubjectModel?.SupportsToolCalling == true;

    public bool IsNetworkGroupActive => EnableNetwork && AreNetworkToolsEnabled;

    public bool IsNetworkSplit => IsNetworkGroupActive && IsSearchProviderSwitchVisible;

    public string NetworkSegmentLabel =>
        AvailableConversationSearchProvider is { } selected
            ? SearchProviderLabel(selected)
            : string.Empty;

    public bool HasNetworkSegmentLabel => !string.IsNullOrEmpty(NetworkSegmentLabel);

    public string SearchProviderToolTip =>
        AvailableConversationSearchProvider is { } selected
            ? $"搜索服务：{SearchProviderLabel(selected)}"
            : ModelBuiltinSearchVendor is { } vendor
                ? $"搜索服务：{BuiltinWebSearch.Label(vendor)} · 当前模型默认"
                : $"搜索服务：{SearchProviderLabel(GlobalSearchProvider)} · 跟随设置";

    public IReadOnlyList<SearchProviderChoice> SearchProviderChoices
    {
        get
        {
            var selected = AvailableConversationSearchProvider;
            var defaultChoice = ModelBuiltinSearchVendor is { } vendor
                ? new SearchProviderChoice(null, BuiltinWebSearch.Label(vendor), "当前模型默认", true, selected is null)
                : new SearchProviderChoice(null, "跟随设置", SearchProviderLabel(GlobalSearchProvider), true, selected is null);
            var choices = new List<SearchProviderChoice> { defaultChoice };

            foreach (var (id, name) in SearchProviders)
            {
                var configured = IsSearchProviderConfigured(id);
                var detail = id switch
                {
                    "molagpt" => configured ? "使用账户额度" : "登录后可用",
                    "duckduckgo" => "免费",
                    "deepseek" => configured ? "按成功请求计费" : "未配置 API Key",
                    _ => configured ? null : "未配置 API Key"
                };
                choices.Add(new SearchProviderChoice(id, name, detail, configured, selected == id));
            }

            return choices;
        }
    }

    private bool IsSearchProviderConfigured(string id) =>
        _settings is null
            ? id == "duckduckgo"
            : _settings.IsWebSearchProviderConfigured(id);

    [RelayCommand]
    private void SelectSearchProvider(SearchProviderChoice? choice)
    {
        if (choice is { IsSelectable: true })
            SetConversationSearchProvider(choice.Id);
    }

    public void SetConversationSearchProvider(string? provider)
    {
        var normalized = NormalizeConversationSearchProvider(provider);
        if (normalized is not null && !IsSearchProviderConfigured(normalized)) return;

        var context = _chat.RoleContext;
        if (string.Equals(context.SearchProvider, normalized, StringComparison.Ordinal)) return;
        context.SearchProvider = normalized;
        _chat.SaveRoleContext(context);
        RaiseSearchProviderChanged();
    }

    public void RefreshSearchProviderUi() => RaiseSearchProviderChanged();

    private void RaiseSearchProviderChanged()
    {
        OnPropertyChanged(nameof(ConversationSearchProvider));
        OnPropertyChanged(nameof(GlobalSearchProvider));
        OnPropertyChanged(nameof(ModelBuiltinSearchVendor));
        OnPropertyChanged(nameof(HasModelBuiltinSearch));
        OnPropertyChanged(nameof(ActiveBuiltinSearchVendor));
        OnPropertyChanged(nameof(IsBuiltinSearchActive));
        OnPropertyChanged(nameof(ActiveBuiltinSearchProviderId));
        OnPropertyChanged(nameof(IsThinkingForcedByBuiltinSearch));
        OnPropertyChanged(nameof(IsThinkingToggleChecked));
        OnPropertyChanged(nameof(ThinkingToolTip));
        OnPropertyChanged(nameof(IsReasoningEffortVisible));
        RaiseReasoningGroupChanged();
        OnPropertyChanged(nameof(ActiveSearchProvider));
        OnPropertyChanged(nameof(ActiveSearchProviderLabel));
        OnPropertyChanged(nameof(IsNetworkGroupActive));
        OnPropertyChanged(nameof(IsNetworkSplit));
        OnPropertyChanged(nameof(NetworkSegmentLabel));
        OnPropertyChanged(nameof(HasNetworkSegmentLabel));
        OnPropertyChanged(nameof(SearchProviderToolTip));
        OnPropertyChanged(nameof(SearchProviderChoices));
    }

    internal static string SearchProviderLabel(string? provider) =>
        SearchProviders.FirstOrDefault(entry => entry.Id == provider).Name ?? "DuckDuckGo";

    private static string? NormalizeConversationSearchProvider(string? provider)
    {
        var id = provider?.Trim().ToLowerInvariant();
        return SearchProviders.Any(entry => entry.Id == id) ? id : null;
    }
}

public sealed record SearchProviderChoice(
    string? Id,
    string Name,
    string? Detail,
    bool IsSelectable,
    bool IsSelected)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}
