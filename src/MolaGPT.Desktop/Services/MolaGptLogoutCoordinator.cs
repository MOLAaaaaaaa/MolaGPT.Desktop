using System.Windows;
using MolaGPT.Core.Auth;
using MolaGPT.Core.Chat;
using MolaGPT.Storage.Repositories;
using MolaGPT.ViewModels;

namespace MolaGPT.Desktop.Services;

public sealed class MolaGptLogoutCoordinator
{
    private const string MolaGptProviderId = "molagpt-proxy";

    private readonly MolaGptAuthService _auth;
    private readonly ProviderRegistry _registry;
    private readonly CloudSyncService _cloudSync;
    private readonly ConversationRepository _conversations;
    private readonly ConversationListViewModel _conversationList;
    private readonly ChatViewModel _chat;

    public MolaGptLogoutCoordinator(
        MolaGptAuthService auth,
        ProviderRegistry registry,
        CloudSyncService cloudSync,
        ConversationRepository conversations,
        ConversationListViewModel conversationList,
        ChatViewModel chat)
    {
        _auth = auth;
        _registry = registry;
        _cloudSync = cloudSync;
        _conversations = conversations;
        _conversationList = conversationList;
        _chat = chat;

        _auth.LoggedOut += OnLoggedOut;
    }

    public void CleanupLoggedOutAccountState(string reason)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => CleanupLoggedOutAccountState(reason));
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(_auth.CurrentJwt))
            {
                DiagnosticLog.Write("MolaGptLogout", $"skip reason={reason} jwt-present");
                return;
            }

            var currentWasMolaGpt = IsMolaGptConversation(_chat.ConversationId);
            var selectedId = _conversationList.SelectedId;
            var providerRemoved = _registry.Unregister(MolaGptProviderId);
            var deleted = _cloudSync.CleanupLocalPlaceholdersForLogout();

            _conversationList.Reload();

            if (currentWasMolaGpt)
            {
                _chat.StartDraftConversation();
                _chat.TryAutoPickActive();
                _conversationList.ClearSelection();
            }
            else if (!string.IsNullOrEmpty(selectedId)
                && _conversationList.FindItem(selectedId) is null)
            {
                _conversationList.ClearSelection();
            }

            DiagnosticLog.Write(
                "MolaGptLogout",
                $"cleanup reason={reason} providerRemoved={providerRemoved} deletedPlaceholders={deleted} currentWasMolaGpt={currentWasMolaGpt}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("MolaGptLogout", $"cleanup failed reason={reason}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnLoggedOut(object? sender, EventArgs e) =>
        CleanupLoggedOutAccountState("auth-logout");

    private bool IsMolaGptConversation(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return false;
        var row = _conversations.Get(conversationId);
        return string.Equals(row?.ProviderId, MolaGptProviderId, StringComparison.OrdinalIgnoreCase);
    }
}
