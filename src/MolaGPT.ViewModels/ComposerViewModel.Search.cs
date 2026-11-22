using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;

namespace MolaGPT.ViewModels;

public sealed partial class ComposerViewModel
{
    private static readonly (string Id, string Name, string? Detail)[] SearchProviders =
    [
        ("molagpt", "MolaGPT Web Search", null),
        ("duckduckgo", "DuckDuckGo", "无需 API Key"),
        ("deepseek", "DeepSeek 搜索 API", "成功请求按次计费"),
        ("tavily", "Tavily", "需要 API Key"),
        ("exa", "Exa", "需要 API Key"),
        (DeepSeekServerSearch.ProviderId, "DeepSeek 服务端搜索", "开启深度思考，计入模型用量")
    ];

    public string? ConversationSearchProvider => NormalizeConversationSearchProvider(SubjectRoleContext.SearchProvider);

    public string GlobalSearchProvider
    {
        get
        {
            var provider = SettingsViewModel.NormalizeWebSearchProvider(_settings?.WebSearchProvider);
            return provider == DeepSeekServerSearch.ProviderId ? "duckduckgo" : provider;
        }
    }

    public bool CanUseDeepSeekServerSearch
    {
        get
        {
            var provider = SubjectProvider;
            if (provider is null || provider.ToAppMode() != AppMode.Byok
                || SubjectModel is not { SupportsToolCalling: true } model)
                return false;

            var entry = _settings?.Providers.FirstOrDefault(item =>
                item.Enabled && string.Equals(item.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            return entry is not null
                && DeepSeekServerSearch.IsSupportedEndpointType(entry.Type)
                && DeepSeekServerSearch.IsSupportedTarget(model.Id, entry.BaseUrl);
        }
    }

    private string? AvailableConversationSearchProvider
    {
        get
        {
            var selected = ConversationSearchProvider;
            return selected == DeepSeekServerSearch.ProviderId && !CanUseDeepSeekServerSearch ? null : selected;
        }
    }

    public string ActiveSearchProvider => AvailableConversationSearchProvider ?? GlobalSearchProvider;

    public string ActiveSearchProviderLabel => SearchProviderLabel(ActiveSearchProvider);

    public bool IsSearchProviderSwitchVisible =>
        SubjectProvider is { } provider && provider.ToAppMode().IsLocalAgent()
        && SubjectModel?.SupportsToolCalling == true;

    public IReadOnlyList<SearchProviderChoice> SearchProviderChoices
    {
        get
        {
            var selected = AvailableConversationSearchProvider;
            bool canUseServerSearch = CanUseDeepSeekServerSearch;
            bool signedIn = _settings?.IsLoggedIn == true;
            var choices = new List<SearchProviderChoice>
            {
                new(null, $"跟随设置（{SearchProviderLabel(GlobalSearchProvider)}）", null, true, selected is null)
            };

            foreach (var (id, name, detail) in SearchProviders)
            {
                bool selectable = id switch
                {
                    DeepSeekServerSearch.ProviderId => canUseServerSearch,
                    "molagpt" => signedIn,
                    _ => true
                };
                var description = selectable ? detail : id == "molagpt"
                    ? "请先登录 MolaGPT"
                    : "需要 DeepSeek 官方 deepseek-flash 模型";
                choices.Add(new SearchProviderChoice(id, name, description, selectable, selected == id));
            }

            return choices;
        }
    }

    [RelayCommand]
    private void SelectSearchProvider(SearchProviderChoice? choice)
    {
        if (choice is { IsSelectable: true })
            SetConversationSearchProvider(choice.Id);
    }

    public void SetConversationSearchProvider(string? provider)
    {
        var normalized = NormalizeConversationSearchProvider(provider);
        if (normalized == DeepSeekServerSearch.ProviderId && !CanUseDeepSeekServerSearch) return;

        var context = _chat.RoleContext;
        if (string.Equals(context.SearchProvider, normalized, StringComparison.Ordinal)) return;
        context.SearchProvider = normalized;
        _chat.SaveRoleContext(context);
        RaiseSearchProviderChanged();
    }

    private void RaiseSearchProviderChanged()
    {
        OnPropertyChanged(nameof(ConversationSearchProvider));
        OnPropertyChanged(nameof(ActiveSearchProvider));
        OnPropertyChanged(nameof(ActiveSearchProviderLabel));
        OnPropertyChanged(nameof(SearchProviderChoices));
        OnPropertyChanged(nameof(CanUseDeepSeekServerSearch));
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
