using System.Text;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

public sealed partial class ComposerViewModel
{
    private bool _applyingRoleOptions;

    private void ApplyRoleOptions(bool restoring)
    {
        if (!_chat.CurrentMode.IsLocalAgent()) return;
        _applyingRoleOptions = true;
        try
        {
            var persona = _chat.ActivePersona;
            var profile = persona?.Profile;
            // A role's default model applies to a conversation it starts, not to
            // one already under way. Picking a role mid-thread to change how it
            // talks silently swapped the model out from under the answer in
            // progress — the header pill just read something else afterwards,
            // with no way to tell what had done it.
            var beforeFirstTurn = !_chat.Messages.Any(message => message.Role == ChatMessage.RoleUser);
            if (!restoring && beforeFirstTurn
                && profile?.ProviderId is { Length: > 0 } providerId && profile.ModelId is { Length: > 0 } modelId)
                _chat.SetActiveByIds(providerId, modelId);
            var context = _chat.RoleContext;
            EnableNetwork = (restoring ? context.EnableNetwork : null) ?? persona?.DefaultEnableNetwork ?? false;
            EnableWebFetch = (restoring ? context.EnableWebFetch : null) ?? persona?.DefaultEnableWebFetch ?? false;
            EnableThinking = (restoring ? context.EnableThinking : null) ?? persona?.DefaultThinking ?? false;
            var effort = (restoring ? context.ReasoningEffort : null) ?? persona?.DefaultReasoningEffort;
            if (!string.IsNullOrEmpty(effort) && AvailableEffortLevels.Contains(effort)) ReasoningEffort = effort;
            OnPropertyChanged(nameof(IsPythonToolVisible));
            OnPropertyChanged(nameof(CanProcessOpaqueFiles));
        }
        finally { _applyingRoleOptions = false; }
    }

    private void PersistRoleOptions()
    {
        if (_applyingRoleOptions || _chat.IsConversationLoading || !_chat.CurrentMode.IsLocalAgent()) return;
        var context = _chat.RoleContext;
        context.EnableNetwork = EnableNetwork;
        context.EnableWebFetch = EnableWebFetch;
        context.EnableThinking = EnableThinking;
        context.ReasoningEffort = ReasoningEffort;
        _chat.SaveRoleContext(context);
    }

    partial void OnEnableNetworkChanged(bool value) => PersistRoleOptions();
    partial void OnEnableWebFetchChanged(bool value) => PersistRoleOptions();

    private RolePromptBuildResult? PrepareRolePrompt(MessageViewModel assistant, string generationId, bool continuation, int? maxTokens)
    {
        if (!_chat.IsAtmosphereMode || _chat.ActivePersona is not { } persona)
        {
            _chat.SetRoleEvaluation(null);
            return null;
        }
        var history = _chat.GetStoredHistory().ToList();
        var at = history.FindIndex(row => row.Id == assistant.MessageId);
        if (at >= 0) history = history.Take(at + (continuation ? 1 : 0)).ToList();
        var identity = _chat.ResolveRoleIdentity();
        var vars = new PromptVariables
        {
            Now = DateTimeOffset.Now,
            ModelDisplayName = _chat.ActiveModel?.DisplayName, ModelId = _chat.ActiveModel?.Id,
            ProviderDisplayName = _chat.ActiveProvider?.DisplayName, Username = _settings?.MolaGptUsername,
            UserName = identity.Name, CharacterName = persona.Name,
            StableSeed = _chat.ConversationId + ":" + persona.Id, GenerationSeed = generationId,
            RoleFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["persona"] = identity.Description }
        };
        var result = RolePromptBuilder.Build(persona, _chat.ActiveModelSystemPrompt, _chat.ConversationSystemPrompt,
            _chat.SystemPromptMode, _chat.RoleContext, history, _chat.GetRoleLorebooks(), vars,
            assistant.MessageId ?? "pending:" + generationId,
            maxTokens, continuation);
        _chat.SetRoleEvaluation(result);
        return result;
    }

    private IReadOnlyList<ChatMessage>? BuildHistorySeed(MessageViewModel? userMessage)
    {
        if (!_chat.RoleContext.NeedsHistorySync || userMessage is null) return null;
        var history = _chat.ReadHistoryBefore(userMessage);
        try
        {
            return history.Select(message => new ChatMessage(message.Role, BuildContentForHistory(message),
                Attachments: message.Role == ChatMessage.RoleUser ? BuildHistoryAttachments(message) : null)).ToArray();
        }
        finally
        {
            foreach (var message in history) message.Dispose();
        }
    }

    private IReadOnlyList<ChatMessage> BuildContinuationHistorySeed()
    {
        var history = _chat.ReadWholeHistory();
        try
        {
            return history.Select(message => new ChatMessage(message.Role, BuildContentForHistory(message),
                Attachments: message.Role == ChatMessage.RoleUser ? BuildHistoryAttachments(message) : null)).ToArray();
        }
        finally
        {
            foreach (var message in history) message.Dispose();
        }
    }

    private int? ResolveRoleMaxTokens(ProviderModel model)
    {
        var maxTokens = _chat.ActivePersona?.Profile.MaxTokens;
        return maxTokens is { } requested && model.MaxOutputTokens is > 0 and var limit
            ? Math.Min(requested, limit) : maxTokens;
    }

    private ChatRequest ApplyRoleRequestOptions(ChatRequest request, IReadOnlyList<ChatMessage>? seed, int? maxTokens)
    {
        if (!_chat.CurrentMode.IsLocalAgent()) return request;
        var profile = _chat.ActivePersona?.Profile;
        return request with
        {
            Temperature = _chat.ActiveModel?.SupportsTemperature == true ? profile?.Temperature : null,
            TopP = _chat.ActiveModel?.SupportsTopP == true ? profile?.TopP : null,
            MaxTokens = maxTokens,
            HistorySeed = seed,
            HistoryRevision = seed is null ? null : _chat.RoleContext.HistoryRevision
        };
    }
}
