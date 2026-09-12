using System.Collections.ObjectModel;
using MolaGPT.Core.Models;
using MolaGPT.Storage.Repositories;

namespace MolaGPT.ViewModels;

public sealed class RoleLibraryViewModel
{
    private const string BooksPrefix = "role.lorebook.";
    private const string IdentitiesPrefix = "role.user_persona.";
    private const string DefaultIdentityKey = "role.default_user_persona";
    private readonly SettingsRepository? _settings;
    private readonly PersonaRepository? _personas;
    private readonly ConversationRepository? _conversations;
    public ObservableCollection<Lorebook> Books { get; } = [];
    public ObservableCollection<UserPersona> Identities { get; } = [];
    public string? DefaultIdentityId { get; private set; }

    public RoleLibraryViewModel(SettingsRepository? settings = null,
        PersonaRepository? personas = null, ConversationRepository? conversations = null)
    {
        _settings = settings;
        _personas = personas;
        _conversations = conversations;
        if (settings is null) return;
        foreach (var row in settings.GetByPrefix(BooksPrefix))
            Books.Add(RoleJson.Deserialize<Lorebook>(row.Value));
        foreach (var row in settings.GetByPrefix(IdentitiesPrefix))
            Identities.Add(RoleJson.Deserialize<UserPersona>(row.Value));
        DefaultIdentityId = settings.Get(DefaultIdentityKey);
    }

    public IReadOnlyList<Lorebook> ResolveBooks(IEnumerable<string> ids) => ids.Distinct(StringComparer.Ordinal)
        .Select(id => Books.FirstOrDefault(book => book.Id == id)
            ?? throw new InvalidOperationException("关联的世界书不存在，请重新选择。")).ToArray();

    public UserPersona? ResolveIdentity(string? id)
    {
        if (id is null) id = DefaultIdentityId;
        if (string.IsNullOrEmpty(id)) return null;
        return Identities.FirstOrDefault(identity => identity.Id == id)
            ?? throw new InvalidOperationException("关联的用户身份不存在，请重新选择。");
    }

    public string? BookDeletionReason(Lorebook book) => ReferenceCount(book.Id, false) > 0
        ? "仍有角色或对话关联这本世界书。" : null;

    public void SaveBooks(IReadOnlyList<Lorebook> books)
    {
        var removed = Books.Where(book => !books.Any(item => item.Id == book.Id)).ToArray();
        foreach (var book in removed)
            if (BookDeletionReason(book) is { } reason) throw new InvalidOperationException(reason);
        foreach (var book in books) _settings?.Set(BooksPrefix + book.Id, RoleJson.Serialize(book));
        foreach (var book in removed) _settings?.Remove(BooksPrefix + book.Id);
        Books.Clear();
        foreach (var book in books) Books.Add(book);
    }

    public void SaveIdentity(UserPersona identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Name)) throw new InvalidOperationException("请填写身份名称。");
        _settings?.Set(IdentitiesPrefix + identity.Id, RoleJson.Serialize(identity));
        var old = Identities.FirstOrDefault(item => item.Id == identity.Id);
        if (old is null) Identities.Add(identity);
        else Identities[Identities.IndexOf(old)] = identity;
    }

    public void DeleteIdentity(UserPersona identity)
    {
        if (DefaultIdentityId == identity.Id || ReferenceCount(identity.Id, true) > 0)
            throw new InvalidOperationException("请先取消默认身份或已有的角色、对话关联。");
        _settings?.Remove(IdentitiesPrefix + identity.Id);
        Identities.Remove(identity);
    }

    public void SetDefaultIdentity(string? id)
    {
        if (id is not null && !Identities.Any(item => item.Id == id))
            throw new InvalidOperationException("用户身份不存在。");
        if (id is null) _settings?.Remove(DefaultIdentityKey);
        else _settings?.Set(DefaultIdentityKey, id);
        DefaultIdentityId = id;
    }

    public string? IdentityDeletionReason(UserPersona identity) => ReferenceCount(identity.Id, true) > 0
        ? "仍有角色或对话关联这个身份。" : null;

    public void SaveIdentities(IReadOnlyList<UserPersona> identities, string? defaultId)
    {
        if (identities.Any(identity => string.IsNullOrWhiteSpace(identity.Name)))
            throw new InvalidOperationException("请填写身份名称。");
        if (defaultId is not null && !identities.Any(identity => identity.Id == defaultId))
            throw new InvalidOperationException("默认身份不存在。");
        var removed = Identities.Where(identity => !identities.Any(item => item.Id == identity.Id)).ToArray();
        foreach (var identity in removed)
            if (IdentityDeletionReason(identity) is { } reason) throw new InvalidOperationException(reason);
        foreach (var identity in identities) SaveIdentity(identity);
        SetDefaultIdentity(defaultId);
        foreach (var identity in removed) DeleteIdentity(identity);
    }

    private int ReferenceCount(string id, bool identity) =>
        (_personas?.CountRoleReference(id, identity) ?? 0) + (_conversations?.CountRoleReference(id, identity) ?? 0);
}
