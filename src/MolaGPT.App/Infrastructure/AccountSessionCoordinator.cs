using Avalonia.Threading;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Desktop.Services;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.App.Infrastructure;

internal sealed class AccountSessionCoordinator : IDisposable
{
    private readonly MolaGptAuthService _auth;
    private readonly ProviderRegistry _registry;
    private readonly CloudSyncService _cloudSync;
    private readonly ConversationRepository _conversations;
    private readonly ConversationListViewModel _conversationList;
    private readonly ChatViewModel _chat;
    private readonly SettingsViewModel _settings;

    public AccountSessionCoordinator(
        MolaGptAuthService auth,
        ProviderRegistry registry,
        CloudSyncService cloudSync,
        ConversationRepository conversations,
        ConversationListViewModel conversationList,
        ChatViewModel chat,
        SettingsViewModel settings)
    {
        _auth = auth;
        _registry = registry;
        _cloudSync = cloudSync;
        _conversations = conversations;
        _conversationList = conversationList;
        _chat = chat;
        _settings = settings;
        _auth.LoggedOut += OnLoggedOut;
    }

    public void CleanupLoggedOutAccountState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(CleanupLoggedOutAccountState);
            return;
        }

        if (!string.IsNullOrEmpty(_auth.CurrentJwt)) return;

        var currentWasMolaGpt = IsMolaGptConversation(_chat.ConversationId);
        var selectedId = _conversationList.SelectedId;

        _settings.IsLoggedIn = false;
        _settings.MolaGptUsername = null;
        _registry.Unregister(MolaGptProviderIds.Proxy);
        _registry.Unregister(MolaGptProviderIds.LocalTools);
        _cloudSync.CleanupLocalPlaceholdersForLogout();
        _conversationList.Reload();

        if (currentWasMolaGpt)
        {
            _chat.StartDraftConversation();
            _chat.TryAutoPickActive();
            _conversationList.ClearSelection();
        }
        else if (!string.IsNullOrEmpty(selectedId) && _conversationList.FindItem(selectedId) is null)
        {
            _conversationList.ClearSelection();
        }
    }

    private void OnLoggedOut(object? sender, EventArgs e) => CleanupLoggedOutAccountState();

    private bool IsMolaGptConversation(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return false;
        return MolaGptProviderIds.IsMolaGptAccount(_conversations.Get(conversationId)?.ProviderId);
    }

    public void Dispose() => _auth.LoggedOut -= OnLoggedOut;
}
