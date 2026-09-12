using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.Storage;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

public sealed partial class ChatViewModel
{
    public RoleLibraryViewModel? RoleLibrary => _personas?.Library;
    public RolePromptBuildResult? RoleEvaluation { get; private set; }
    public RolePromptTrace? LastRolePromptTrace { get; private set; }

    public void SetRolePromptTrace(string conversationId, RolePromptTrace trace)
    {
        if (conversationId != ConversationId) return;
        LastRolePromptTrace = trace;
        OnPropertyChanged(nameof(LastRolePromptTrace));
    }

    partial void OnConversationIdChanged(string? value)
    {
        SetRoleEvaluation(null);
        LastRolePromptTrace = null;
        OnPropertyChanged(nameof(LastRolePromptTrace));
    }

    public (string Name, string Description) ResolveRoleIdentity()
    {
        var profile = ActivePersona?.Profile;
        var identity = RoleLibrary?.ResolveIdentity(RoleContext.UserPersonaId ?? profile?.UserPersonaId);
        return (RoleContext.UserName ?? identity?.Name ?? profile?.UserName ?? "",
            RoleContext.UserDescription ?? identity?.Description ?? profile?.UserDescription ?? "");
    }

    public IReadOnlyList<Lorebook> GetRoleLorebooks()
    {
        var profile = ActivePersona?.Profile;
        if (profile is null) return [];
        var ids = RoleContext.SharedLorebookIds ?? profile.SharedLorebookIds;
        var shared = RoleLibrary?.ResolveBooks(ids) ?? [];
        return profile.Lorebooks.Concat(shared).DistinctBy(book => book.Id).ToArray();
    }

    public void SetRoleEvaluation(RolePromptBuildResult? result)
    {
        RoleEvaluation = result;
        SetActiveLoreEntries(result?.Lore.Hits ?? []);
        OnPropertyChanged(nameof(RoleEvaluation));
    }

    internal void CompleteRoleGeneration(string conversationId, MessageViewModel message,
        IReadOnlyDictionary<string, LoreActivationState>? states, string? expectedRevision,
        string? generationId, bool regeneration, bool continuation, bool succeeded)
    {
        var row = _conversationRepo?.Get(conversationId);
        var context = ConversationId == conversationId ? RoleContext
            : row is null ? null : RoleJson.Deserialize<ConversationRoleContext>(row.RoleContextJson);
        if (context is null) return;
        var current = expectedRevision == context.HistoryRevision;
        if (regeneration && message.MessageId is { } changedId)
        {
            var pendingSync = context.NeedsHistorySync;
            InvalidateHistory(context, [changedId]);
            context.NeedsHistorySync = pendingSync || continuation;
        }
        if (succeeded && current && states is not null)
        {
            message.MessageId ??= Guid.NewGuid().ToString("N");
            context.LoreStates = states.ToDictionary(pair => pair.Key,
                pair => pair.Value.SourceMessageId == "pending:" + generationId
                    ? pair.Value with { SourceMessageId = message.MessageId } : pair.Value);
        }
        if (message.MessageId is { } completedId)
        {
            context.ExcludedSummaryMessageIds.Remove(completedId);
            if (!succeeded) context.ExcludedSummaryMessageIds.Add(completedId);
        }
        if (row is not null) _conversationRepo!.Upsert(row with { RoleContextJson = RoleJson.Serialize(context) });
        if (ConversationId == conversationId)
        {
            RoleContext = context;
            OnPropertyChanged(nameof(RoleContext));
        }
    }
    public ConversationRoleContext RoleContext { get; private set; } = new();
    public IReadOnlyList<LorebookHit> ActiveLoreEntries { get; private set; } = [];
    public bool CanEditHistory => CurrentMode.IsLocalAgent() && !IsStreaming && !IsConversationLoading;
    public bool IsRoleChat => CurrentMode.IsLocalAgent();
    public ConversationMode InteractionMode => ActivePersona?.Mode ?? ConversationMode.Chat;
    public bool IsAtmosphereMode => CurrentMode.IsLocalAgent() && InteractionMode == ConversationMode.Atmosphere;
    public string InteractionModeLabel => IsAtmosphereMode ? "氛围" : "对话";
    public event Action<bool>? RoleOptionsRequested;
    public event Action<string>? HistorySaveFailed;

    public void SaveRoleContext(ConversationRoleContext context)
    {
        RoleContext = context;
        if (_conversationRepo?.Get(ConversationId ?? "") is { } row)
            _conversationRepo.Upsert(row with { RoleContextJson = RoleJson.Serialize(context) });
        OnPropertyChanged(nameof(RoleContext));
    }

    private void RefreshInteractionMode()
    {
        OnPropertyChanged(nameof(InteractionMode));
        OnPropertyChanged(nameof(IsAtmosphereMode));
        OnPropertyChanged(nameof(InteractionModeLabel));
    }

    public void SetActiveLoreEntries(IReadOnlyList<LorebookHit> entries)
    {
        ActiveLoreEntries = entries;
        OnPropertyChanged(nameof(ActiveLoreEntries));
    }

    public void MarkHistoryChanged(IEnumerable<string>? changedMessageIds = null)
    {
        InvalidateHistory(RoleContext, changedMessageIds ?? []);
        SaveRoleContext(RoleContext);
    }

    private static void InvalidateHistory(ConversationRoleContext context, IEnumerable<string> changedMessageIds)
    {
        context.HistoryRevision = Guid.NewGuid().ToString("N");
        context.NeedsHistorySync = true;
        var changed = changedMessageIds.ToHashSet(StringComparer.Ordinal);
        if (context.SummarySourceIds.Any(changed.Contains))
        {
            context.Summary = "";
            context.SummarySourceIds.Clear();
        }
        context.Memories.RemoveAll(memory => memory.SourceMessageIds.Any(changed.Contains));
        context.ExcludedSummaryMessageIds.RemoveAll(changed.Contains);
        if (context.Summary.Length == 0 || context.LastSummarizedMessageId is { } watermark && changed.Contains(watermark))
            context.LastSummarizedMessageId = null;
        foreach (var key in context.LoreStates.Where(pair => changed.Contains(pair.Value.SourceMessageId)).Select(pair => pair.Key).ToArray())
            context.LoreStates.Remove(key);
    }

    public void MarkHistorySynchronized(string conversationId, string? revision)
    {
        if (revision is null) return;
        var row = _conversationRepo?.Get(conversationId);
        var context = ConversationId == conversationId ? RoleContext
            : row is null ? null : RoleJson.Deserialize<ConversationRoleContext>(row.RoleContextJson);
        if (context is null || context.HistoryRevision != revision) return;
        context.NeedsHistorySync = false;
        if (row is not null)
            _conversationRepo!.Upsert(row with { RoleContextJson = RoleJson.Serialize(context) });
    }

    public IReadOnlyList<MessageRow> GetStoredHistory() => _messageRepo is not null && ConversationId is not null
        ? _messageRepo.List(ConversationId)
        : Messages.Where(message => !message.IsStreaming).Select(message => new MessageRow(
            message.MessageId ??= Guid.NewGuid().ToString("N"), ConversationId ?? "", message.Role,
            message.FullContent, BuildMessageMeta(message), message.Timestamp.ToUnixTimeMilliseconds())).ToList();

    internal IReadOnlyList<MessageViewModel> ReadHistoryBefore(MessageViewModel userMessage)
    {
        var rows = GetStoredHistory();
        var index = rows.ToList().FindIndex(row => row.Id == userMessage.MessageId);
        if (index < 0) throw new InvalidOperationException("找不到当前消息的历史位置。");
        return PrepareMessageSnapshot(rows.Take(index).ToList()).Select(CreateMessageViewModel).ToList();
    }

    internal IReadOnlyList<MessageViewModel> ReadWholeHistory() =>
        PrepareMessageSnapshot(GetStoredHistory()).Select(CreateMessageViewModel).ToList();

    internal void MarkConversationHistoryChanged(string conversationId, string? changedMessageId)
    {
        if (ConversationId == conversationId)
        {
            MarkHistoryChanged(changedMessageId is null ? [] : [changedMessageId]);
            return;
        }
        if (_conversationRepo?.Get(conversationId) is not { } row) return;
        var context = RoleJson.Deserialize<ConversationRoleContext>(row.RoleContextJson);
        InvalidateHistory(context, changedMessageId is null ? [] : [changedMessageId]);
        _conversationRepo.Upsert(row with { RoleContextJson = RoleJson.Serialize(context) });
    }

    public void EnsureRoleGreeting()
    {
        if (!IsAtmosphereMode || Messages.Count != 0 || ActivePersona is not { } persona) return;
        ConversationId ??= ComposerViewModel.CreateWebCompatibleConversationId();
        var text = ResolveGreeting(persona);
        if (string.IsNullOrWhiteSpace(text)) return;
        ConversationId ??= ComposerViewModel.CreateWebCompatibleConversationId();
        EnsureConversationExists();
        var message = new MessageViewModel(ChatMessage.RoleAssistant, text, DateTimeOffset.UtcNow)
        {
            PersonaId = persona.Id,
            PersonaName = persona.Name,
            PersonaAvatar = persona.Avatar
        };
        message.VersionSelected += OnMessageVersionSelected;
        Messages.Add(message);
        PersistMessage(message);
        MarkHistoryChanged();
        TouchConversation();
    }

    private string ResolveGreeting(PersonaItemViewModel persona)
    {
        var greetings = new[] { persona.Profile.Greeting }.Concat(persona.Profile.AlternateGreetings).ToList();
        var index = Math.Clamp(RoleContext.GreetingIndex, 0, greetings.Count - 1);
        var greeting = greetings[index];
        var identity = ResolveRoleIdentity();
        return SystemPromptInterpolator.Interpolate(greeting, new PromptVariables
        {
            Now = DateTimeOffset.Now, CharacterName = string.IsNullOrWhiteSpace(persona.Profile.Nickname) ? persona.Name : persona.Profile.Nickname,
            UserName = identity.Name,
            ModelDisplayName = ActiveModel?.DisplayName, ModelId = ActiveModel?.Id,
            ProviderDisplayName = ActiveProvider?.DisplayName,
            StableSeed = ConversationId + ":" + persona.Id,
            GenerationSeed = ConversationId + ":greeting:" + index,
            RoleFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["description"] = persona.Profile.Description, ["personality"] = persona.Profile.Personality,
                ["scenario"] = RoleContext.Scenario ?? persona.Profile.Scenario, ["persona"] = identity.Description,
                ["charprompt"] = persona.SystemPrompt, ["charjailbreak"] = persona.Profile.PostHistoryInstructions
            }
        });
    }

    public void RefreshRoleGreeting()
    {
        if (!CurrentMode.IsLocalAgent() || IsStreaming || GetStoredHistory().Any(row => row.Role == ChatMessage.RoleUser)) return;
        if (Messages.Count == 0) { EnsureRoleGreeting(); return; }
        var text = IsAtmosphereMode && ActivePersona is { } persona ? ResolveGreeting(persona) : "";
        var message = Messages[0];
        MarkHistoryChanged(message.MessageId is { } id ? [id] : []);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (message.MessageId is { } messageId) _messageRepo?.Delete(messageId);
            Messages.Remove(message);
            message.Dispose();
        }
        else
        {
            message.Content = text;
            message.PersonaId = ActivePersona?.Id;
            message.PersonaName = ActivePersona?.Name;
            message.PersonaAvatar = ActivePersona?.Avatar;
            message.RetryAttempts = null;
            UpdatePersistedMessage(message);
        }
        TouchConversation();
    }

    public void StartRoleConversation(string personaId)
    {
        ValidateRoleModel(personaId);
        StartDraftConversation();
        SaveActivePersona(personaId);
        EnsureRoleGreeting();
    }

    private void ValidateRoleModel(string? personaId)
    {
        var profile = _personas?.Find(personaId)?.Profile;
        if (profile?.ProviderId is not { Length: > 0 } providerId || profile.ModelId is not { Length: > 0 } modelId) return;
        var target = _providers.FindModel(providerId, modelId);
        if (target is null || !target.Value.Provider.ToAppMode().IsLocalAgent())
            throw new InvalidOperationException($"角色的默认模型 {modelId} 不可用，请在角色设置中重新选择。");
    }

    public Task<MessageViewModel?> EditMessageAsync(MessageViewModel message, string content)
    {
        if (content == message.FullContent) return Task.FromResult<MessageViewModel?>(null);
        return CreateTimelineBranchAsync(message, content);
    }

    public Task<MessageViewModel?> CreateBranchAsync(MessageViewModel message) =>
        CreateTimelineBranchAsync(message, null);

    private async Task<MessageViewModel?> CreateTimelineBranchAsync(MessageViewModel message, string? editedContent)
    {
        var conversationId = ConversationId;
        if (!CanEditHistory || !Messages.Contains(message)
            || _conversationRepo is null || _messageRepo is null || conversationId is null)
            return null;

        var history = GetStoredHistory().ToList();
        var index = history.FindIndex(row => row.Id == message.MessageId);
        if (index < 0) throw new InvalidOperationException("找不到分支消息。");
        var source = history[index];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = Guid.NewGuid().ToString("N");
        var content = editedContent ?? source.Content;
        var meta = source.Meta;
        if (editedContent is not null)
        {
            var edited = CreateMessageViewModel(PrepareMessageSnapshot([source])[0]);
            try
            {
                edited.MessageId = id;
                edited.ParentMessageId = source.ParentId;
                edited.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now);
                if (source.Role == ChatMessage.RoleAssistant)
                {
                    edited.BeginRetryAttempt();
                    edited.Content = editedContent;
                    edited.ContentPartsJson = null;
                }
                else
                {
                    edited.Content = editedContent;
                    edited.ContentPartsJson = RewriteTextContentPart(edited.ContentPartsJson, editedContent);
                }
                edited.RetryAttempts = null;
                edited.RetryCurrentIndex = 0;
                meta = BuildMessageMeta(edited);
            }
            finally { edited.Dispose(); }
        }

        var context = RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(RoleContext));
        var affected = history.Skip(index).Select(item => item.Id).ToArray();
        InvalidateHistory(context, affected);
        _conversationRepo.CreateTimelineWithMessage(
            new MessageRow(id, conversationId, source.Role, content, meta, now, source.ParentId),
            RoleJson.Serialize(context));
        await LoadConversationAsync(conversationId);
        AnnounceHistoryEdit();
        return Messages.FirstOrDefault(item => item.MessageId == id);
    }

    private static string? RewriteTextContentPart(string? json, string content)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray parts) return null;
            var textPart = parts.OfType<JsonObject>()
                .FirstOrDefault(part => part["type"]?.GetValue<string>() == "text");
            if (string.IsNullOrWhiteSpace(content))
            {
                if (textPart is not null) parts.Remove(textPart);
            }
            else if (textPart is not null) textPart["text"] = content;
            else parts.Insert(0, new JsonObject { ["type"] = "text", ["text"] = content });
            return parts.Count == 0 ? null : parts.ToJsonString();
        }
        catch (JsonException) { return null; }
    }

    private void PersistHistoryEdit(MessageViewModel message, ConversationRoleContext context)
    {
        if (_messageRepo is not null && ConversationId is not null && message.MessageId is not null)
            _messageRepo.UpdateRoleMessage(ConversationId, message.MessageId, message.FullContent,
                BuildMessageMeta(message), RoleJson.Serialize(context));
        RoleContext = context;
        OnPropertyChanged(nameof(RoleContext));
    }

    private void OnMessageVersionSelected(object? sender, int previousIndex)
    {
        if (sender is not MessageViewModel message || !CanEditHistory) return;
        var context = RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(RoleContext));
        InvalidateHistory(context, message.MessageId is { } id ? [id] : []);
        try
        {
            PersistHistoryEdit(message, context);
        }
        catch (Exception ex)
        {
            message.RestoreAttempt(previousIndex);
            HistorySaveFailed?.Invoke(ex.Message);
            return;
        }
        message.PersonaAvatar = _personas?.Find(message.PersonaId)?.Avatar;
        AnnounceHistoryEdit();
    }

    private void AnnounceHistoryEdit()
    {
        if (ConversationId is not { } id) return;
        ConversationTouched?.Invoke(this, new ConversationTouchedEventArgs(
            id, ConversationTitle, DateTimeOffset.UtcNow, ActiveProvider?.Id, ActivePersona?.Name));
    }

    public async Task SelectMessageBranchAsync(MessageViewModel message, int offset)
    {
        var conversationId = ConversationId;
        if (!CanEditHistory || _conversationRepo is null || _messageRepo is null || conversationId is null
            || message.BranchSiblingIds.Count < 2)
            return;
        var targetIndex = message.BranchIndex + offset;
        if (targetIndex < 0 || targetIndex >= message.BranchSiblingIds.Count) return;
        var targetId = message.BranchSiblingIds[targetIndex];
        var rows = _messageRepo.ListAll(conversationId).ToDictionary(row => row.Id, StringComparer.Ordinal);
        var timeline = _conversationRepo.ListTimelines(conversationId)
            .FirstOrDefault(item => TimelineContains(item, targetId, rows));
        if (timeline is null) throw new InvalidOperationException("找不到所选时间线。");
        _conversationRepo.SelectTimeline(conversationId, timeline.Id);
        await LoadConversationAsync(conversationId);
    }

    private static bool TimelineContains(
        ConversationTimelineRow timeline,
        string messageId,
        IReadOnlyDictionary<string, MessageRow> rows)
    {
        var current = timeline.LeafMessageId;
        while (current is not null && rows.TryGetValue(current, out var row))
        {
            if (current == messageId) return true;
            current = row.ParentId;
        }
        return false;
    }

    partial void OnIsStreamingChanged(bool value)
    {
        foreach (var message in Messages) message.HistoryLocked = value;
        OnPropertyChanged(nameof(CanEditHistory));
    }

    partial void OnIsConversationLoadingChanged(bool value) => OnPropertyChanged(nameof(CanEditHistory));
}
