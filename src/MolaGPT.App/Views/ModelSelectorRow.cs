using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// What the model picker should show: the rows for one side of the Chat ↔
/// local-agent boundary, plus the labels for the footer that swaps to the other
/// side. The footer is a Button outside the ListBox, not a row inside it.
/// </summary>
/// <param name="Rows">Items for the ListBox — one side's models, never both.</param>
/// <param name="FooterTitle">Footer label, or null when the footer should stay
/// hidden (nothing across the boundary to go to). One line, and strictly about
/// where the button goes: a consequence printed on a button reads as that
/// button's consequence, so the cost lives in <paramref name="NoticeText"/>
/// instead.</param>
/// <param name="NoticeText">Caution strip above the list, or null. Set only
/// while the far side is on screen, where it describes every row below it.</param>
public readonly record struct ModelSelectorList(
    List<ModelSelectorRow> Rows,
    string? FooterTitle,
    string? NoticeText);

/// <summary>
/// One line in the model picker: a section header, an empty-state message, or a
/// selectable model.
///
/// A flat list of tagged rows rather than a grouped collection, because the
/// picker's grouping is not one level — BYOK gets a header per provider while
/// Chat and Work get one per mode. Expressing that as rows keeps the template a
/// single switch instead of nested group styles.
///
/// Ported from MainWindow.xaml.cs's ModelSelectorRow, with the WPF Visibility
/// members replaced by booleans.
/// </summary>
public sealed class ModelSelectorRow
{
    private ModelSelectorRow() { }

    public string? HeaderText { get; private init; }
    public string? EmptyText { get; private init; }
    public IChatProvider? Provider { get; private init; }
    public ProviderModel? Model { get; private init; }

    /// <summary>True for the row matching the conversation's current provider+model.</summary>
    public bool IsActive { get; private init; }

    public string? ModelName => Model?.DisplayName;
    public bool IsHeader => HeaderText is not null;
    public bool IsEmpty => EmptyText is not null;
    public bool IsModel => Model is not null;
    public bool SupportsThinking => Model?.SupportsThinking == true;
    public bool SupportsTools => Model?.SupportsToolCalling == true;
    public bool SupportsVision => Model?.SupportsVision == true;

    public static ModelSelectorRow ForHeader(string text) => new() { HeaderText = text };
    public static ModelSelectorRow ForEmpty(string text) => new() { EmptyText = text };

    public static ModelSelectorRow ForModel(IChatProvider provider, ProviderModel model, bool isActive) =>
        new() { Provider = provider, Model = model, IsActive = isActive };

    // ---- construction ------------------------------------------------------

    /// <param name="showingOtherSide">Show the models across the Chat boundary
    /// <em>instead of</em> the current side's, not in addition to them.</param>
    public static ModelSelectorList Build(
        ProviderRegistry registry, ChatViewModel chat, string? query, bool showingOtherSide = false)
    {
        query = query?.Trim() ?? string.Empty;

        var currentMode = chat.CurrentMode;
        var activeProviderId = chat.ActiveProvider?.Id;
        var activeModelId = chat.ActiveModel?.Id;

        var providers = registry.Providers
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // This side = the current mode's whole "family": Chat alone, or both
        // local-agent wallets (Work + BYOK) when in an agent mode. The active
        // mode's group is listed first so the user's current pick stays on top.
        var thisSideRows = new List<ModelSelectorRow>();
        var topModes = ModesInChatFamily(currentMode);
        foreach (var mode in topModes)
            AddSection(thisSideRows, providers.Where(p => p.ToAppMode() == mode),
                mode, query, activeProviderId, activeModelId);

        // Far side = the modes across the Chat boundary. The split mirrors
        // CrossesChatBoundary exactly, so every row over there costs a new
        // conversation. The two sides never share the list: the footer swaps
        // which one is on screen, so "what a click here does" is one rule for
        // everything visible instead of one rule per row.
        var otherSideRows = new List<ModelSelectorRow>();
        foreach (var mode in new[] { AppMode.Chat, AppMode.Work, AppMode.Byok }.Where(m => !topModes.Contains(m)))
            AddSection(otherSideRows, providers.Where(p => p.ToAppMode() == mode),
                mode, query, activeProviderId, activeModelId);

        var onChatSide = currentMode == AppMode.Chat;
        var thisSide = SideLabel(onChatSide);
        var otherSide = SideLabel(!onChatSide);
        var otherCount = otherSideRows.Count(row => row.IsModel);
        var thisCount = thisSideRows.Count(row => row.IsModel);

        var rows = showingOtherSide ? otherSideRows : thisSideRows;
        string? footerTitle = null;
        string? noticeText = null;

        if (showingOtherSide)
        {
            footerTitle = thisCount == 0
                ? $"返回 {thisSide}"
                : query.Length == 0
                    ? $"返回 {thisSide} 模型"
                    : $"返回 {thisSide} 的 {thisCount} 个匹配";

            // Above the rows it governs, so "选择" can only be read as choosing
            // one of them. Skipped when there is nothing below it to warn about.
            if (otherCount > 0)
                noticeText = $"选择下方模型将新建对话，并切换至 {otherSide}";
        }
        else if (otherCount > 0)
        {
            footerTitle = query.Length == 0
                ? $"查看 {otherCount} 个来自 {otherSide} 的模型"
                : $"来自 {otherSide} 的 {otherCount} 个匹配的模型";
        }

        if (rows.Count == 0)
        {
            rows.Add(ForEmpty(query.Length == 0
                ? "这里没有可用的模型"
                : "没有匹配的模型"));
        }

        return new ModelSelectorList(rows, footerTitle, noticeText);
    }

    /// <summary>Modes on the same side of the Chat ↔ local-agent boundary as
    /// <paramref name="currentMode"/>, ordered with the active mode first. Chat is
    /// alone; Work and BYOK travel together (either can continue an agent chat).</summary>
    private static IReadOnlyList<AppMode> ModesInChatFamily(AppMode currentMode) =>
        currentMode == AppMode.Chat
            ? [AppMode.Chat]
            : currentMode == AppMode.Work
                ? [AppMode.Work, AppMode.Byok]
                : [AppMode.Byok, AppMode.Work];

    /// <summary>Name for one side of the Chat boundary, worded like the title
    /// bar's two segments so the footer points at something the user can see.
    /// Public because the shell's post-switch banner has to say the same words;
    /// Work and BYOK are one place here, since only the wallet differs.</summary>
    public static string SideLabel(AppMode mode) => SideLabel(mode == AppMode.Chat);

    private static string SideLabel(bool chatSide) => chatSide ? "MolaGPT 网页版对话" : "MolaGPT 本地对话";

    private static void AddSection(
        ICollection<ModelSelectorRow> rows,
        IEnumerable<IChatProvider> providers,
        AppMode mode,
        string query,
        string? activeProviderId,
        string? activeModelId)
    {
        var addedHeader = false;

        foreach (var provider in providers)
        {
            var providerMatches = Matches(query, provider.DisplayName, provider.Id);
            var models = provider.Models
                .Where(model => providerMatches || Matches(query, model.DisplayName, model.Id))
                .ToList();
            if (models.Count == 0) continue;

            if (mode == AppMode.Byok)
            {
                // BYOK gets one group per provider ("自定义 API · <名称>") so models
                // from different providers don't blur into one flat list.
                // Chat/Work are single-provider modes and keep the mode header.
                rows.Add(ForHeader($"{ModeLabel(mode)} · {provider.DisplayName}"));
            }
            else if (!addedHeader)
            {
                rows.Add(ForHeader($"{ModeLabel(mode)} 模型"));
                addedHeader = true;
            }

            foreach (var model in models)
            {
                var isActive = string.Equals(provider.Id, activeProviderId, StringComparison.Ordinal)
                               && string.Equals(model.Id, activeModelId, StringComparison.Ordinal);
                rows.Add(ForModel(provider, model, isActive));
            }
        }
    }

    private static bool Matches(string query, params string?[] values)
    {
        if (query.Length == 0) return true;
        foreach (var value in values)
        {
            if (value is { Length: > 0 } && value.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ModeLabel(AppMode mode) => mode switch
    {
        AppMode.Chat => "MolaGPT Chat",
        AppMode.Work => "MolaGPT 账号",
        _ => "自定义 API"
    };
}
