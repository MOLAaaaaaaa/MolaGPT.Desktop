using System.Text.Json.Nodes;
using MolaGPT.Core.Chat.Tasks;

namespace MolaGPT.Desktop.Services.Remote;

public sealed partial class RemoteControlHost
{
    // Only the state tick writes these fingerprints.
    private readonly Dictionary<string, string> _tasksSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentWatch> _agentWatches = new(StringComparer.Ordinal);
    private long _agentWatchSeq;

    private sealed class AgentWatch(string conversationId, string agentId, long id, JsonArray items, bool hasEarlier, long version)
    {
        public string ConversationId { get; } = conversationId;
        public string AgentId { get; } = agentId;
        public long Id { get; } = id;
        public bool Ready;
        public JsonArray Items = items;
        public bool HasEarlier = hasEarlier;
        public long Version = version;
    }

    private Func<string, string?>? TaskStates(string conversationId) =>
        _chat.Tasks is { } tasks
            ? id => tasks.Find(conversationId, id) is { } task ? TaskTools.StatusName(task.Status) : null
            : null;

    private JsonObject TasksJson(string conversationId) =>
        _chat.Tasks is { } registry ? RemoteTasks.List(registry, conversationId)
            : new JsonObject { ["running"] = new JsonArray(), ["agents"] = new JsonArray() };

    private async Task<JsonObject> StopTaskAsync(JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        var taskId = Text(args, "taskId") ?? throw new RemoteError("bad_request", "缺少任务标识");
        var stopped = false;
        await _onUi(() =>
        {
            if (_chat.Tasks is not { } tasks || tasks.Find(conversationId, taskId) is not { } task)
                throw new RemoteError("not_found", "任务不存在");
            if (!task.IsRunning || !tasks.Stop(conversationId, taskId))
                throw new RemoteError("resolved", "任务已结束");
            stopped = true;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return new JsonObject { ["stopped"] = stopped };
    }

    private JsonObject AgentOpen(string peer, JsonObject args)
    {
        var conversationId = RequiredConversation(args);
        var agentId = Text(args, "agentId") ?? throw new RemoteError("bad_request", "缺少子 Agent 标识");
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(peer, out var mine) || !mine.ContainsKey(conversationId))
                throw new RemoteError("not_open", "请先打开对话");
            if (_chat.Tasks is not { } tasks || tasks.FindAgent(conversationId, agentId) is not { } agent
                || (agent.LatestTaskId is { } taskId && tasks.Find(conversationId, taskId) is { IsRestored: true }))
                throw new RemoteError("not_found", "子 Agent 的活动记录已不可用");
            var version = agent.Version;
            var (items, hasEarlier) = RemoteTasks.Activities(
                tasks.ListAgentActivities(conversationId, agentId, RemoteTasks.MaxActivities + 1));
            var watch = new AgentWatch(conversationId, agentId, ++_agentWatchSeq, items, hasEarlier, version);
            _agentWatches[peer] = watch;
            return new JsonObject { ["watch"] = watch.Id, ["activities"] = items, ["hasEarlier"] = hasEarlier };
        }
    }

    private void FinishAgentOpen(string peer)
    {
        lock (_gate)
            if (_agentWatches.TryGetValue(peer, out var watch)) watch.Ready = true;
    }

    private JsonObject AgentClose(string peer)
    {
        lock (_gate) _agentWatches.Remove(peer);
        return new JsonObject();
    }

    private void PushTaskChanges()
    {
        if (_chat.Tasks is not { } registry) return;
        HashSet<string> open;
        lock (_gate)
            open = _subscriptions.Values.SelectMany(mine => mine.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _tasksSent.Keys.Where(id => !open.Contains(id)).ToList()) _tasksSent.Remove(id);
        foreach (var id in open)
        {
            var data = RemoteTasks.List(registry, id);
            var stable = (JsonObject)data.DeepClone();
            foreach (var items in stable.Select(pair => pair.Value).OfType<JsonArray>())
                foreach (var item in items.OfType<JsonObject>()) item.Remove("elapsedMs");
            var fingerprint = stable.ToJsonString();
            if (_tasksSent.TryGetValue(id, out var sent) && sent == fingerprint) continue;
            Push(id, "tasks", data);
            _tasksSent[id] = fingerprint;
        }
    }

    private void PushAgentActivity()
    {
        if (_chat.Tasks is not { } registry) return;
        List<(string Peer, AgentWatch Watch)> watches;
        lock (_gate)
        {
            foreach (var peer in _agentWatches.Where(p => !_subscriptions.TryGetValue(p.Key, out var mine)
                         || !mine.ContainsKey(p.Value.ConversationId)).Select(p => p.Key).ToList())
                _agentWatches.Remove(peer);
            watches = _agentWatches.Where(p => p.Value.Ready).Select(p => (p.Key, p.Value)).ToList();
        }
        foreach (var (peer, watch) in watches)
        {
            var version = registry.FindAgent(watch.ConversationId, watch.AgentId)?.Version ?? -1;
            if (version == watch.Version) continue;
            var (current, hasEarlier) = RemoteTasks.Activities(
                registry.ListAgentActivities(watch.ConversationId, watch.AgentId, RemoteTasks.MaxActivities + 1));
            var previous = watch.Items.OfType<JsonObject>().ToDictionary(item => Text(item, "id")!);
            var ids = current.OfType<JsonObject>().Select(item => Text(item, "id")!).ToHashSet();
            var reset = previous.Keys.Any(id => !ids.Contains(id));
            var changes = new JsonArray();
            foreach (var item in current.OfType<JsonObject>())
            {
                var id = Text(item, "id")!;
                if (!reset && previous.TryGetValue(id, out var before))
                {
                    if (JsonNode.DeepEquals(before, item)) continue;
                    // Append only while the displayed window still has the same prefix.
                    if (Text(item, "kind") is "text" or "thinking"
                        && item["truncated"] is null && before["truncated"] is null
                        && Text(before, "text") is { } oldText && Text(item, "text") is { } text
                        && text.StartsWith(oldText, StringComparison.Ordinal))
                    {
                        changes.Add(new JsonObject { ["id"] = id, ["append"] = text[oldText.Length..] });
                        continue;
                    }
                }
                changes.Add(item.DeepClone());
            }
            if (changes.Count == 0 && hasEarlier == watch.HasEarlier)
            {
                watch.Version = version;
                continue;
            }
            lock (_gate)
            {
                if (!_agentWatches.TryGetValue(peer, out var active) || !ReferenceEquals(active, watch)) continue;
                PushTo(peer, watch.ConversationId, "agent", new JsonObject
                {
                    ["watch"] = watch.Id, ["agentId"] = watch.AgentId, ["items"] = changes,
                    ["reset"] = reset, ["hasEarlier"] = hasEarlier
                });
                watch.Items = current;
                watch.HasEarlier = hasEarlier;
                watch.Version = version;
            }
        }
    }
}
