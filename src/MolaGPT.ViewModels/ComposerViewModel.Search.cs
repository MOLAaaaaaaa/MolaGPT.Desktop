using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;

namespace MolaGPT.ViewModels;

/// <summary>
/// 会话级的「联网搜索」服务商切换。
///
/// 全局设置里选的那个是默认值，但一次对话里想换一个查法是很正常的：这一段在查
/// 代码文档，下一段在查新闻，而设置页是全局的、改一次影响所有对话。所以覆盖值
/// 存在对话自己的 <see cref="ConversationRoleContext.SearchProvider"/> 里，没存就
/// 跟随全局。
///
/// 「DeepSeek服务端搜索」只在这里出现，不能作为全局服务商：它绑在具体模型上
/// （只有 DeepSeek 的模型认这个工具），换一个非 DeepSeek 的模型全局搜索就会全废。
/// </summary>
public sealed partial class ComposerViewModel
{
    /// <summary>会话里选中了谁；null = 跟随全局设置。</summary>
    public string? ConversationSearchProvider => Normalize(_chat.RoleContext.SearchProvider);

    /// <summary>
    /// 设置页里的全局选择。全局侧不认识的名字由设置页折成 DuckDuckGo；服务端搜索
    /// 根本不该出现在全局记录里，万一旧配置留下了它也当没设过。
    /// </summary>
    public string GlobalSearchProvider
    {
        get
        {
            var provider = SettingsViewModel.NormalizeWebSearchProvider(_settings?.WebSearchProvider);
            return provider == DeepSeekServerSearch.ProviderId ? "duckduckgo" : provider;
        }
    }

    /// <summary>
    /// 当前模型 + 端点能不能用 DeepSeek 的服务端搜索。三件事同时成立才行：
    /// 会话跑在本地 Agent 上（Chat 模式由后端自己搜索）、模型 id 认得出是
    /// DeepSeek、端点协议能放得下这个服务端工具。
    /// </summary>
    public bool CanUseDeepSeekServerSearch
    {
        get
        {
            if (!_chat.CurrentMode.IsLocalAgent()) return false;
            var provider = SubjectProvider;
            if (provider is null || MolaGptProviderIds.IsMolaGptAccount(provider.Id)) return false;
            if (!DeepSeekServerSearch.IsSupportedModel(SubjectModel?.Id)) return false;
            return DeepSeekServerSearch.IsSupportedEndpointType(EndpointTypeOf(provider));
        }
    }

    /// <summary>
    /// 这一轮真正生效的服务商。会话覆盖值优先，但它得仍然可用——换了个模型之后
    /// 原来选的服务端搜索就该悄悄退回全局，而不是让整轮请求失败。
    /// </summary>
    public string ActiveSearchProvider
    {
        get
        {
            var chosen = ConversationSearchProvider;
            if (chosen is null) return GlobalSearchProvider;
            if (chosen == DeepSeekServerSearch.ProviderId && !CanUseDeepSeekServerSearch)
                return GlobalSearchProvider;
            return chosen;
        }
    }

    public string ActiveSearchProviderLabel => SearchProviderLabel(ActiveSearchProvider);

    /// <summary>入口只在本地 Agent 的会话里出现；Chat 模式的搜索在服务端完成，
    /// 这里换谁都没有意义。</summary>
    public bool IsSearchProviderSwitchVisible =>
        _chat.CurrentMode.IsLocalAgent() && SubjectProvider?.Kind != ProviderKind.MolaGptProxy;

    /// <summary>
    /// 下拉里的一行。「跟随全局」也是一个选项（<c>Id == null</c>），它把覆盖值清掉，
    /// 之后设置页再改，这个对话也跟着改。
    /// </summary>
    public IReadOnlyList<SearchProviderChoice> SearchProviderChoices
    {
        get
        {
            var choices = new List<SearchProviderChoice>
            {
                new(null, $"跟随全局（{SearchProviderLabel(GlobalSearchProvider)}）",
                    "在设置里换服务商时，这个对话也跟着换", true, ConversationSearchProvider is null)
            };

            foreach (var (id, name, detail) in ProviderCatalogue)
            {
                var selectable = id switch
                {
                    DeepSeekServerSearch.ProviderId => CanUseDeepSeekServerSearch,
                    "molagpt" => _settings?.IsLoggedIn == true,
                    _ => true
                };
                choices.Add(new SearchProviderChoice(id, name, detail, selectable,
                    selectable && ConversationSearchProvider == id));
            }

            return choices;
        }
    }

    private static readonly (string Id, string Name, string? Detail)[] ProviderCatalogue =
    [
        ("molagpt", "MolaGPT Web Search", "MolaGPT 内置搜索，需要登录账号"),
        ("deepseek", "DeepSeek Search", "DeepSeek 官方搜索 API，按次计费"),
        ("duckduckgo", "DuckDuckGo", "直接抓取搜索结果页，不需要 Key"),
        ("tavily", "Tavily", "需要填写 API Key"),
        ("exa", "Exa", "需要填写 API Key"),
        (DeepSeekServerSearch.ProviderId, "DeepSeek服务端搜索",
            "DeepSeek官方提供的服务端搜索工具，搜索不按次收费，每次搜索都会额外消耗模型Token。")
    ];

    [RelayCommand]
    private void SelectSearchProvider(SearchProviderChoice? choice)
    {
        if (choice is null) return;
        SetConversationSearchProvider(choice.Id);
    }

    /// <summary>
    /// 写入（或清掉）本会话的覆盖值。<paramref name="provider"/> 为 null / 空即回到
    /// 跟随全局。不可用的选择直接忽略——灰掉的项理论上点不到，这是第二道闸。
    /// </summary>
    public void SetConversationSearchProvider(string? provider)
    {
        var normalized = Normalize(provider);
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

    /// <summary>端点类型来自设置里的服务商行；找不到行（内置合成服务商）时按
    /// <see cref="ProviderKind"/> 粗判，宁可少开也不要把工具塞进不认它的协议里。</summary>
    private string? EndpointTypeOf(IChatProvider provider)
    {
        var row = _settings?.Providers.FirstOrDefault(entry =>
            string.Equals(entry.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        if (row?.Type is { Length: > 0 } type) return type;
        return provider.Kind switch
        {
            ProviderKind.Anthropic => "anthropic",
            ProviderKind.OpenAI or ProviderKind.OpenAICompatible or ProviderKind.Custom => "openai-compat",
            _ => null
        };
    }

    /// <summary>界面上的名字。取值不认识时按 DuckDuckGo 显示——那是本地搜索的默认实现。</summary>
    internal static string SearchProviderLabel(string? provider) =>
        Normalize(provider) switch
        {
            "molagpt" => "MolaGPT Web Search",
            "deepseek" => "DeepSeek Search",
            "tavily" => "Tavily",
            "exa" => "Exa",
            DeepSeekServerSearch.ProviderId => "DeepSeek服务端搜索",
            _ => "DuckDuckGo"
        };

    /// <summary>会话覆盖值允许为空（跟随全局）；空之外的未知值落回 null，宁可跟随全局
    /// 也不要让一个拼错的字符串把搜索关掉。</summary>
    private static string? Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var normalized = SettingsViewModel.NormalizeWebSearchProvider(provider);
        // NormalizeWebSearchProvider 把不认识的都折成 duckduckgo，那种「折」只有
        // 全局设置该发生；会话里的未知值当作没设过，交回全局。
        return normalized switch
        {
            "molagpt" or "deepseek" or "tavily" or "exa" or "duckduckgo"
                or DeepSeekServerSearch.ProviderId => normalized,
            _ => null
        };
    }
}

/// <summary>会话搜索服务商下拉里的一行。</summary>
/// <param name="Id">服务商标识；null 表示「跟随全局」。</param>
/// <param name="Name">主标题。</param>
/// <param name="Detail">副标题，灰字，解释这个选项是什么/要什么。</param>
/// <param name="IsSelectable">false 时整行灰掉（未登录的 MolaGPT 搜索、当前模型不支持的
/// 服务端搜索）。</param>
/// <param name="IsSelected">右侧对勾。</param>
public sealed record SearchProviderChoice(
    string? Id,
    string Name,
    string? Detail,
    bool IsSelectable,
    bool IsSelected)
{
    /// <summary>给 XAML 用的可见性，副标题可能为空。</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}
