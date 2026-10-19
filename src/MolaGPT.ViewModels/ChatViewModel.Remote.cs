using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels;

/// <summary>What a turn needs to know about a conversation, read from storage, so a
/// turn can run in a conversation that is not the one on screen.</summary>
public sealed record StoredConversation(
    ConversationRow Row,
    IChatProvider Provider,
    ProviderModel Model,
    PersonaItemViewModel? Persona,
    ConversationRoleContext RoleContext)
{
    public bool IsAtmosphereMode =>
        Provider.ToAppMode().IsLocalAgent() && Persona?.Mode == ConversationMode.Atmosphere;

    public string SystemPromptMode =>
        string.Equals(Row.SystemPromptMode, "append", StringComparison.OrdinalIgnoreCase) ? "append" : "override";
}

public sealed partial class ChatViewModel
{
    /// <summary>
    /// The stored state of <paramref name="conversationId"/>, or null when it is gone
    /// or its model is no longer registered. For the conversation on screen this is
    /// what was last saved, not the live view.
    /// </summary>
    public StoredConversation? ReadStoredConversation(string conversationId)
    {
        if (_conversationRepo?.Get(conversationId) is not { DeletedAt: null } row) return null;
        if (string.IsNullOrWhiteSpace(row.ProviderId) || string.IsNullOrWhiteSpace(row.ModelId)) return null;
        if (_providers.FindModel(row.ProviderId, row.ModelId) is not { } resolved) return null;
        ConversationRoleContext context;
        try { context = RoleJson.Deserialize<ConversationRoleContext>(row.RoleContextJson); }
        catch (Exception) { context = new(); }
        return new StoredConversation(row, resolved.Provider, resolved.Model, _personas?.Find(row.PersonaId), context);
    }

    /// <summary>The active branch of a conversation, oldest first.</summary>
    public IReadOnlyList<MessageRow> ReadStoredMessages(string conversationId) =>
        _messageRepo?.List(conversationId) ?? [];

    /// <summary>Every model a Work or BYOK conversation may continue with.</summary>
    public IEnumerable<(IChatProvider Provider, ProviderModel Model)> AgentModels() =>
        _providers.Providers
            .Where(provider => provider.Kind != ProviderKind.MolaGptProxy)
            .SelectMany(provider => provider.Models.Select(model => (provider, model)));

    /// <summary>Record the model on screen against the open conversation now, rather
    /// than on its next message, so lists elsewhere show it.</summary>
    public void SaveActiveModel()
    {
        if (_conversationRepo?.Get(ConversationId ?? "") is not { } row) return;
        _conversationRepo.Upsert(row with { ModelId = ActiveModel?.Id, ProviderId = ActiveProvider?.Id });
    }

    /// <summary>
    /// A conversation a phone is drafting, as it would be stored if created now: the
    /// model asked for (else the agent model in use here, else the first one), and
    /// the persona asked for (else the default one). Null when no such model exists.
    /// </summary>
    public StoredConversation? DraftConversation(
        string id, string? providerId, string? modelId, string? personaId, ConversationRoleContext? context = null)
    {
        var models = AgentModels().Where(choice => choice.Provider is PiWorkProvider).ToList();
        var matches = providerId is not null && modelId is not null
            ? models.Where(c => c.Provider.Id == providerId && c.Model.Id == modelId)
            : models.Where(c => c.Provider == ActiveProvider && c.Model == ActiveModel).Concat(models);
        if (!matches.Any()) return null;
        var choice = matches.First();
        var persona = _personas?.Find(personaId) ?? _personas?.Find(ResolveDefaultPersonaId());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = new ConversationRow(
            Id: id,
            Title: "新对话",
            ModelId: choice.Model.Id,
            ProviderId: choice.Provider.Id,
            CreatedAt: now,
            UpdatedAt: now,
            Pinned: false,
            DeletedAt: null,
            SystemPrompt: null,
            PersonaId: persona?.Id,
            SystemPromptMode: null);
        return new StoredConversation(row, choice.Provider, choice.Model, persona, context ?? new());
    }

    /// <summary>Personas a phone may start a conversation with. Role-play personas
    /// are continued on the desktop only, so they are left out.</summary>
    public IReadOnlyList<PersonaItemViewModel> RemotePersonas() =>
        _personas?.Personas
            .Where(persona => !persona.IsAtmosphereMode)
            .OrderByDescending(persona => persona.Pinned)
            .ThenBy(persona => persona.SortOrder)
            .ToList() ?? [];

    public bool ConversationExists(string conversationId) => _conversationRepo?.Get(conversationId) is not null;

    /// <summary>Store <paramref name="draft"/> as a new conversation, listed as one the
    /// desktop started is.</summary>
    public void CreateStoredConversation(StoredConversation draft, string title)
    {
        if (_conversationRepo is null) throw new InvalidOperationException("没有可用的对话存储");
        var row = draft.Row with { Title = title, RoleContextJson = RoleJson.Serialize(draft.RoleContext) };
        _conversationRepo.Upsert(row);
        ConversationTouched?.Invoke(this, new ConversationTouchedEventArgs(
            row.Id, title, DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt), row.ProviderId, draft.Persona?.Name));
    }

    /// <summary>Remove a conversation <see cref="CreateStoredConversation"/> made when
    /// its first message was refused, so no empty conversation is left behind.</summary>
    public void DiscardStoredConversation(string conversationId) =>
        _conversationRepo?.SoftDelete(conversationId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Write the model and switches of a conversation that is not on screen.</summary>
    public void SaveStoredSettings(string conversationId, string providerId, string modelId, ConversationRoleContext context)
    {
        if (_conversationRepo?.Get(conversationId) is not { } row) return;
        _conversationRepo.Upsert(row with
        {
            ProviderId = providerId,
            ModelId = modelId,
            RoleContextJson = RoleJson.Serialize(context)
        });
    }
}
