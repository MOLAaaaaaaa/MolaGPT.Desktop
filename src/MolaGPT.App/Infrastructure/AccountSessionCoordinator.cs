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
    private readonly MolaGptLocalToolsRegistrar _localTools;
    private readonly ConversationRepository _conversations;
    private readonly ConversationListViewModel _conversationList;
    private readonly ChatViewModel _chat;
    private readonly ComposerViewModel _composer;
    private readonly SettingsViewModel _settings;

    public AccountSessionCoordinator(
        MolaGptAuthService auth,
        ProviderRegistry registry,
        CloudSyncService cloudSync,
        MolaGptLocalToolsRegistrar localTools,
        ConversationRepository conversations,
        ConversationListViewModel conversationList,
        ChatViewModel chat,
        ComposerViewModel composer,
        SettingsViewModel settings)
    {
        _auth = auth;
        _registry = registry;
        _cloudSync = cloudSync;
        _localTools = localTools;
        _conversations = conversations;
        _conversationList = conversationList;
        _chat = chat;
        _composer = composer;
        _settings = settings;
        _auth.SessionChanged += OnSessionChanged;
    }

    public void Initialize() => ApplySession(startup: true);

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplySession();
        else Dispatcher.UIThread.Post(() => ApplySession());
    }

    private void ApplySession(bool startup = false)
    {
        var session = _auth.ReadSession();
        if (session.Generation <= _settings.AccountSessionGeneration) return;

        var currentWasMolaGpt = IsMolaGptConversation(_chat.ConversationId);
        var selectedId = _conversationList.SelectedId;

        if (!startup)
        {
            _composer.StopAccountTurns();
            _registry.Unregister(MolaGptProviderIds.Proxy);
            _localTools.Deactivate();
        }
        if (session.IsLoggedIn)
        {
            if (startup) _cloudSync.AdoptSignedInAccountAtStartup();
            else _cloudSync.BindCurrentAccountAfterLogin();
            _settings.ApplyAccountSession(session.Generation, true, session.Username);
            if (currentWasMolaGpt && _chat.ConversationId is { } id && _conversations.Get(id) is null)
            {
                _composer.DetachToBackground();
                _chat.StartDraftConversation();
                _conversationList.ClearSelection();
            }
            return;
        }

        _settings.ApplyAccountSession(session.Generation, false, null);
        if (SettingsViewModel.NormalizeWebSearchProvider(_settings.WebSearchProvider) == "molagpt")
            _settings.WebSearchProvider = "duckduckgo";
        _cloudSync.CleanupLocalPlaceholdersForLogout();
        _conversationList.Reload();

        if (currentWasMolaGpt)
        {
            _composer.DetachToBackground();
            _chat.StartDraftConversation();
            _chat.TryAutoPickActive();
            _conversationList.ClearSelection();
        }
        else if (!string.IsNullOrEmpty(selectedId) && _conversationList.FindItem(selectedId) is null)
        {
            _conversationList.ClearSelection();
        }
    }

    private bool IsMolaGptConversation(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return false;
        return MolaGptProviderIds.IsMolaGptAccount(_conversations.Get(conversationId)?.ProviderId);
    }

    public void Dispose() => _auth.SessionChanged -= OnSessionChanged;
}
