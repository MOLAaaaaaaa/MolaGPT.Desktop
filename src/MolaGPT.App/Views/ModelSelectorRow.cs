using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Views;

/// <summary>
/// One line in the model picker: a section header, a hint, an empty-state
/// message, or a selectable model.
///
/// A flat list of tagged rows rather than a grouped collection, because the
/// picker's grouping is not one level — BYOK gets a header per provider while
/// Chat and Work get one per mode — and the "other modes" block carries its own
/// explanatory hint between the header and the first model. Expressing that as
/// rows keeps the template a single switch instead of nested group styles.
///
/// Ported from MainWindow.xaml.cs's ModelSelectorRow, with the WPF Visibility
/// members replaced by booleans.
/// </summary>
public sealed class ModelSelectorRow
{
    private ModelSelectorRow(
        string? headerText, string? hintText, string? emptyText,
        IChatProvider? provider, ProviderModel? model, bool isActive)
    {
        HeaderText = headerText;
        HintText = hintText;
        EmptyText = emptyText;
        Provider = provider;
        Model = model;
        IsActive = isActive;
    }

    public string? HeaderText { get; }
    public string? HintText { get; }
    public string? EmptyText { get; }
    public IChatProvider? Provider { get; }
    public ProviderModel? Model { get; }

    /// <summary>True for the row matching the conversation's current provider+model.</summary>
    public bool IsActive { get; }

    public string? ModelName => Model?.DisplayName;
    public bool IsHeader => HeaderText is not null;
    public bool IsHint => HintText is not null;
    public bool IsEmpty => EmptyText is not null;
    public bool IsModel => Model is not null;
    public bool SupportsThinking => Model?.SupportsThinking == true;
    public bool SupportsTools => Model?.SupportsToolCalling == true;
    public bool SupportsVision => Model?.SupportsVision == true;

    public static ModelSelectorRow ForHeader(string text) => new(text, null, null, null, null, false);
    public static ModelSelectorRow ForHint(string text) => new(null, text, null, null, null, false);
    public static ModelSelectorRow ForEmpty(string text) => new(null, null, text, null, null, false);
    public static ModelSelectorRow ForModel(IChatProvider provider, ProviderModel model, bool isActive) =>
        new(null, null, null, provider, model, isActive);

    // ---- construction ------------------------------------------------------

    public static List<ModelSelectorRow> Build(ProviderRegistry registry, ChatViewModel chat, string? query)
    {
        query = query?.Trim() ?? string.Empty;

        var rows = new List<ModelSelectorRow>();
        var currentMode = chat.CurrentMode;
        var activeProviderId = chat.ActiveProvider?.Id;
        var activeModelId = chat.ActiveModel?.Id;

        var providers = registry.Providers
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Top section = the current mode's whole "family": Chat alone, or both
        // local-agent wallets (Work + BYOK) when in an agent mode. The active
        // mode's group is listed first so the user's current pick stays on top.
        var topModes = ModesInChatFamily(currentMode);
        foreach (var mode in topModes)
            AddSection(rows, providers.Where(p => p.ToAppMode() == mode), mode, query, activeProviderId, activeModelId);

        // Other section = the modes across the Chat boundary; picking one of
        // these starts a new conversation.
        var otherRows = new List<ModelSelectorRow>();
        foreach (var mode in new[] { AppMode.Chat, AppMode.Work, AppMode.Byok }.Where(m => !topModes.Contains(m)))
            AddSection(otherRows, providers.Where(p => p.ToAppMode() == mode), mode, query, activeProviderId, activeModelId);

        if (otherRows.Any(row => row.IsModel))
        {
            rows.Add(ForHeader("其他模式可用模型"));
            rows.Add(ForHint("选择这些模型会切换到对应模式，并新建一个对话。"));
            rows.AddRange(otherRows);
        }

        if (rows.Count == 0)
        {
            rows.Add(ForEmpty(query.Length == 0
                ? "当前对话没有可切换的同类型模型"
                : "没有匹配的模型"));
        }

        return rows;
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
