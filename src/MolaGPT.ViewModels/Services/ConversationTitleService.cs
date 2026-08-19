using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Models;
using MolaGPT.Storage;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels.Services;

/// <summary>
/// Generates a better title for the first completed turn of a local conversation.
/// BYOK conversations stay on the user's own provider; Work conversations use the
/// authenticated MolaGPT title endpoint supplied by the desktop host.
/// </summary>
public sealed class ConversationTitleService
{
    public const string AutoTitleEnabledKey = "auto_title_enabled";
    public const string TitleProviderIdKey = "auto_title_provider_id";
    public const string TitleModelIdKey = "auto_title_model_id";

    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(30);

    private readonly ConversationRepository _conversations;
    private readonly MessageRepository _messages;
    private readonly ProviderRegistry _providers;
    private readonly SettingsRepository _settings;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _generateMolaGptTitle;

    public ConversationTitleService(
        ConversationRepository conversations,
        MessageRepository messages,
        ProviderRegistry providers,
        SettingsRepository settings,
        Func<string, string, CancellationToken, Task<string?>>? generateMolaGptTitle = null)
    {
        _conversations = conversations;
        _messages = messages;
        _providers = providers;
        _settings = settings;
        _generateMolaGptTitle = generateMolaGptTitle;
    }

    /// <summary>
    /// Returns the generated title when it was committed, otherwise <see langword="null"/>.
    /// Failures are deliberately silent: the first-message fallback remains a useful title.
    /// </summary>
    public async Task<string?> GenerateAsync(string conversationId, CancellationToken ct = default)
    {
        return await GenerateAsync(conversationId, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates using the provider/model that actually served the completed turn.
    /// Those values are captured by the composer so a mode switch during streaming
    /// cannot accidentally move BYOK content across the MolaGPT account boundary.
    /// </summary>
    public async Task<string?> GenerateAsync(
        string conversationId,
        string? turnProviderId,
        string? turnModelId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || !IsEnabled()) return null;

        try
        {
            var original = _conversations.Get(conversationId);
            if (original is null || original.DeletedAt is not null) return null;
            var providerId = string.IsNullOrWhiteSpace(turnProviderId)
                ? original.ProviderId
                : turnProviderId;
            var modelId = string.IsNullOrWhiteSpace(turnModelId)
                ? original.ModelId
                : turnModelId;
            if (string.Equals(providerId, MolaGptProviderIds.Proxy, StringComparison.OrdinalIgnoreCase))
                return null;

            var rows = _messages.List(conversationId);
            if (ConversationTitleText.IsImageWorkbenchConversation(rows)) return null;

            var window = ConversationTitleText.BuildWindow(rows);
            var firstUser = window.FirstOrDefault(message => message.Role == ChatMessage.RoleUser)?.Text;
            var lastAssistant = window.LastOrDefault(message => message.Role == ChatMessage.RoleAssistant)?.Text;
            if (string.IsNullOrWhiteSpace(firstUser) || string.IsNullOrWhiteSpace(lastAssistant))
                return null;
            if (!string.Equals(
                    original.Title,
                    ChatViewModel.GenerateTitle(firstUser),
                    StringComparison.Ordinal))
            {
                // A non-fallback title means the user (or another source) already
                // named the conversation before this background task began.
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(GenerationTimeout);

            string? title;
            if (string.Equals(providerId, MolaGptProviderIds.LocalTools, StringComparison.OrdinalIgnoreCase))
            {
                if (_generateMolaGptTitle is null) return null;
                title = await _generateMolaGptTitle(firstUser, lastAssistant, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                var target = ResolveByokTarget(providerId, modelId);
                if (target is null) return null;

                var prompt = ConversationTitleText.BuildPrompt(
                    window,
                    CultureInfo.CurrentUICulture.DisplayName);
                var raw = await RequestByokTitleAsync(
                        target.Value.Provider,
                        target.Value.Model,
                        prompt,
                        timeoutCts.Token)
                    .ConfigureAwait(false);
                title = ConversationTitleText.CleanGeneratedTitle(raw);
            }

            if (string.IsNullOrWhiteSpace(title)
                || string.Equals(title, original.Title, StringComparison.Ordinal))
            {
                return null;
            }

            // The request may take several seconds. Preserve a title the user edited
            // while it was in flight, and never resurrect a deleted conversation.
            var latest = _conversations.Get(conversationId);
            if (latest is null
                || latest.DeletedAt is not null
                || !string.Equals(latest.Title, original.Title, StringComparison.Ordinal))
            {
                return null;
            }

            _conversations.Rename(
                conversationId,
                title,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return title;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool IsEnabled() =>
        !bool.TryParse(_settings.Get(AutoTitleEnabledKey), out var enabled) || enabled;

    private (IChatProvider Provider, ProviderModel Model)? ResolveByokTarget(
        string? conversationProviderId,
        string? conversationModelId)
    {
        var configuredProviderId = _settings.Get(TitleProviderIdKey);
        var configuredModelId = _settings.Get(TitleModelIdKey);
        var configured = FindPrivateTarget(configuredProviderId, configuredModelId);
        if (configured is not null) return configured;

        return FindPrivateTarget(conversationProviderId, conversationModelId);
    }

    private (IChatProvider Provider, ProviderModel Model)? FindPrivateTarget(
        string? providerId,
        string? modelId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return null;

        var target = _providers.FindModel(providerId, modelId);
        if (target is null) return null;

        // A stale/manually-edited setting must never route BYOK conversation text
        // through either MolaGPT account provider.
        if (MolaGptProviderIds.IsMolaGptAccount(target.Value.Provider.Id)
            || target.Value.Provider.Kind == ProviderKind.MolaGptProxy)
        {
            return null;
        }

        return target;
    }

    private static async Task<string> RequestByokTitleAsync(
        IChatProvider provider,
        ProviderModel model,
        string prompt,
        CancellationToken ct)
    {
        var titleConversationId = $"title-{Guid.NewGuid():N}";
        var request = new ChatRequest(
            ModelId: model.Id,
            Messages: [new ChatMessage(ChatMessage.RoleUser, prompt)],
            Temperature: 0.2,
            Stream: true,
            UseThinking: false,
            ConversationId: titleConversationId,
            SessionId: Guid.NewGuid().ToString("N"),
            MaxTokens: provider.Kind == ProviderKind.Anthropic ? 80 : null,
            ThinkingParamKind: model.ThinkingConfig?.Kind
                               ?? ThinkingParamKindInference.InferFromModelId(model.Id));

        var text = new StringBuilder();
        try
        {
            await foreach (var chunk in provider.StreamChatAsync(request, ct)
                               .WithCancellation(ct)
                               .ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.DeltaText) && text.Length < 4096)
                {
                    var remaining = 4096 - text.Length;
                    text.Append(chunk.DeltaText.AsSpan(0, Math.Min(remaining, chunk.DeltaText.Length)));
                }

                if (chunk.FinishReason is not null) break;
            }
        }
        finally
        {
            // Pi-backed BYOK providers key sidecars by conversation id. The title
            // request is intentionally isolated, so reclaim its temporary sidecar.
            if (provider is PiWorkProvider piProvider)
            {
                try { await piProvider.CloseConversationAsync(titleConversationId).ConfigureAwait(false); }
                catch { }
            }
        }

        return text.ToString();
    }
}

public sealed record ConversationTitleMessage(string Role, string Text);

public static class ConversationTitleText
{
    private const int MaxMessages = 6;
    private const int MaxCharsPerMessage = 500;
    private const int MaxTotalChars = 3000;

    private static readonly Regex ClosedThinkTag = new(
        @"<think(?:ing)?>.*?</think(?:ing)?>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex OpenThinkTag = new(
        @"<think(?:ing)?>.*",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex EdgeMarkers = new(
        "^[\"'“”‘’「」『』《》*#\\s]+|[\"'“”‘’「」『』《》*\\s]+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TitlePrefix = new(
        @"^(标题|title)\s*[:：]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HttpError = new(
        @"HTTP\s*[45]\d\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ConversationTitleMessage> BuildWindow(
        IReadOnlyList<MessageRow> messages)
    {
        var picked = new List<ConversationTitleMessage>(MaxMessages);
        var total = 0;

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (picked.Count >= MaxMessages || total >= MaxTotalChars) break;

            var message = messages[index];
            if (!string.Equals(message.Role, ChatMessage.RoleUser, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(message.Role, ChatMessage.RoleAssistant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = MessageViewModel.StripSystemHints(message.Content).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length > MaxCharsPerMessage) text = text[..MaxCharsPerMessage];

            picked.Add(new ConversationTitleMessage(message.Role.ToLowerInvariant(), text));
            total += text.Length;
        }

        picked.Reverse();
        return picked;
    }

    public static bool IsImageWorkbenchConversation(IReadOnlyList<MessageRow> messages) =>
        messages.Any(message =>
            message.Meta?.Contains("\"image_workbench\"", StringComparison.OrdinalIgnoreCase) == true);

    public static string BuildPrompt(
        IReadOnlyList<ConversationTitleMessage> messages,
        string locale)
    {
        var content = string.Join("\n\n", messages.Select(message =>
            $"{(message.Role == ChatMessage.RoleAssistant ? "Assistant" : "User")}: {message.Text}"));

        return $$"""
            我会在 <content> 块里给你一段对话内容。
            请把这段用户与助手的对话总结成一个简短标题。
            1. 标题语言与用户的主要语言保持一致
            2. 不要使用标点符号或其他特殊符号
            3. 直接回复标题本身，不要任何前缀、引号或解释
            4. 使用 {{locale}} 语言总结
            5. 标题不超过 12 个字

            <content>
            {{content}}
            </content>
            """;
    }

    public static string? CleanGeneratedTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var withoutThinking = OpenThinkTag.Replace(ClosedThinkTag.Replace(raw, " "), " ");
        var title = withoutThinking
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (string.IsNullOrWhiteSpace(title) || LooksLikeErrorPayload(title)) return null;

        title = EdgeMarkers.Replace(title, string.Empty);
        title = TitlePrefix.Replace(title, string.Empty).Trim();
        if (LooksLikeErrorPayload(title)) return null;
        title = title.Trim('。', '，', ',', '.', ':', '：', ';', '；', '!', '！', '?', '？');
        if (title.Length > 30) title = title[..30].Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static bool LooksLikeErrorPayload(string text) =>
        text.StartsWith('{')
        || text.StartsWith('[')
        || text.StartsWith('<')
        || HttpError.IsMatch(text);
}
