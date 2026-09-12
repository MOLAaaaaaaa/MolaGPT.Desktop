using MolaGPT.Core.Chat;
using MolaGPT.Core.Models;
using MolaGPT.Storage;
using MolaGPT.ViewModels.Services;

namespace MolaGPT.ViewModels;

public sealed partial class ChatViewModel
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _summaryInProgress = new();

    public async Task<bool> AutoSummarizeAsync(string conversationId, OneShotCompletionClient client,
        IChatProvider provider, ProviderModel model, CancellationToken ct = default)
    {
        if (_conversationRepo?.Get(conversationId) is not { } row || _messageRepo is null
            || _personas?.Find(row.PersonaId)?.IsAtmosphereMode != true || provider is not IOneShotTarget) return false;
        var snapshot = ConversationId == conversationId
            ? RoleJson.Deserialize<ConversationRoleContext>(RoleJson.Serialize(RoleContext))
            : RoleJson.Deserialize<ConversationRoleContext>(row.RoleContextJson);
        if (!snapshot.AutoSummarize || !_summaryInProgress.TryAdd(conversationId, 0)) return false;
        try
        {
            var history = new List<MessageRow>();
            MessageRow? question = null;
            foreach (var message in _messageRepo.List(conversationId))
            {
                if (message.Role == "user") { question = message; continue; }
                if (message.Role != "assistant") continue;
                if (!snapshot.ExcludedSummaryMessageIds.Contains(message.Id) && !WasStopped(message)
                    && !string.IsNullOrWhiteSpace(message.Content))
                {
                    if (question is not null && !string.IsNullOrWhiteSpace(question.Content)) history.Add(question);
                    history.Add(message);
                }
                question = null;
            }
            var start = StorySummaryService.FindWatermark(history, snapshot);
            if (history.Count - start - 1 < 20) return false;
            var result = await StorySummaryService.GenerateAsync(client, provider, model, history, snapshot, ct, incremental: true);
            ct.ThrowIfCancellationRequested();
            return TryApplyStorySummary(conversationId, snapshot, result);
        }
        finally { _summaryInProgress.TryRemove(conversationId, out _); }
    }

    internal bool TryApplyStorySummary(string conversationId, ConversationRoleContext snapshot, ConversationRoleContext result)
    {
        var row = _conversationRepo?.Get(conversationId);
        if (row is null || _personas?.Find(row.PersonaId)?.IsAtmosphereMode != true) return false;
        var current = ConversationId == conversationId ? RoleContext : RoleJson.Deserialize<ConversationRoleContext>(row.RoleContextJson);
        if (!current.AutoSummarize || current.HistoryRevision != snapshot.HistoryRevision
            || current.Summary != snapshot.Summary || current.LastSummarizedMessageId != snapshot.LastSummarizedMessageId
            || !current.SummarySourceIds.SequenceEqual(snapshot.SummarySourceIds)
            || RoleJson.Serialize(current.Memories) != RoleJson.Serialize(snapshot.Memories)) return false;
        current.Summary = result.Summary;
        current.SummarySourceIds = result.SummarySourceIds;
        current.LastSummarizedMessageId = result.LastSummarizedMessageId;
        current.Memories = result.Memories;
        _conversationRepo!.Upsert(row with { RoleContextJson = RoleJson.Serialize(current) });
        if (ConversationId == conversationId) { RoleContext = current; OnPropertyChanged(nameof(RoleContext)); }
        return true;
    }

    private static bool WasStopped(MessageRow message)
    {
        if (message.Meta is null) return false;
        using var json = System.Text.Json.JsonDocument.Parse(message.Meta);
        return json.RootElement.TryGetProperty("stopped", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}
