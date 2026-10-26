using System.Text.Json.Nodes;
using MolaGPT.Core.Chat;
using MolaGPT.Core.Chat.Tasks;

namespace MolaGPT.Desktop.Services.Remote;

/// <summary>
/// A conversation's background work as a phone receives it: the tasks still running,
/// drawn as chips above its input, and every sub-agent with what it has been doing,
/// for its sub-agent page. Sub-agent activity lives in memory only, so after a
/// restart an agent is gone here; its card in the conversation keeps the result.
/// </summary>
internal static class RemoteTasks
{
    private const int ActionLimit = 120;
    private const int FailureLimit = 2000;
    private const int TextLimit = 32 * 1024;
    private const int CompactTextLimit = 2 * 1024;
    private const int ArgumentsLimit = 2 * 1024;
    private const int ResultLimit = 4 * 1024;
    public const int MaxActivities = 200;
    private const int ActivityBudget = 400 * 1024;

    public static JsonObject List(AgentTaskRegistry registry, string conversationId)
    {
        var running = new JsonArray();
        foreach (var task in registry.List(conversationId).Where(t => t.IsRunning))
        {
            var item = new JsonObject
            {
                ["id"] = task.Id,
                ["kind"] = task.Kind,
                ["label"] = task.Label,
                ["agentId"] = task.AgentId,
                ["elapsedMs"] = (long)task.Elapsed.TotalMilliseconds
            };
            running.Add(item);
        }

        var agents = new JsonArray();
        foreach (var agent in registry.ListAgents(conversationId))
        {
            var task = agent.LatestTaskId is { } taskId ? registry.Find(conversationId, taskId) : null;
            // Restored handles contain a terminal summary, not the live activity history.
            if (task is { IsRestored: true }) continue;
            var item = new JsonObject
            {
                ["id"] = agent.Id,
                ["label"] = agent.Label,
                ["model"] = agent.ModelId,
                ["taskId"] = task?.Id,
                ["status"] = task is null ? "waiting" : TaskTools.StatusName(task.Status),
                ["currentAction"] = task is { IsRunning: true }
                    ? Tail(agent.RecentOutput.Replace('\n', ' ').Trim(), ActionLimit) : null,
                ["failure"] = task is { Status: AgentTaskStatus.Failed } ? Head(task.Report, FailureLimit) : null
            };
            if (task is { IsRestored: false })
                item["elapsedMs"] = (long)task.Elapsed.TotalMilliseconds;
            agents.Add(item);
        }
        return new JsonObject { ["running"] = running, ["agents"] = agents };
    }

    /// <summary>An agent's activities for agent_open: newest first until the budget is
    /// spent, the rest compact, and no more than <see cref="MaxActivities"/>.</summary>
    public static (JsonArray Items, bool HasEarlier) Activities(IReadOnlyList<AgentActivity> activities)
    {
        var start = Math.Max(0, activities.Count - MaxActivities);
        var items = new List<JsonObject>();
        var used = 2;
        for (var i = activities.Count - 1; i >= start; i--)
        {
            var item = Activity(activities[i]);
            var size = RemoteProjection.Size(item) + 1;
            if (used + size > ActivityBudget)
            {
                item = Activity(activities[i], compact: true);
                size = RemoteProjection.Size(item) + 1;
            }
            if (used + size > ActivityBudget)
            {
                start = i + 1;
                break;
            }
            used += size;
            items.Add(item);
        }
        items.Reverse();
        return (new JsonArray(items.Select(item => (JsonNode)item).ToArray()), start > 0);
    }

    /// <summary>One activity whole. Output keeps its end, a task its beginning, and a
    /// tool is its deltas folded into one card.</summary>
    public static JsonObject Activity(AgentActivity activity, bool compact = false)
    {
        var item = new JsonObject
        {
            ["id"] = activity.Id,
            ["kind"] = activity.Kind,
            ["label"] = Head(activity.Label, ActionLimit),
            ["status"] = activity.Status
        };
        if (activity.Kind == "tool")
        {
            item["tool"] = Tool(activity.ToolDeltas, compact);
            return item;
        }
        var limit = compact ? CompactTextLimit : TextLimit;
        var text = activity.Text;
        if (text.Length > limit)
        {
            item["truncated"] = text.Length;
            text = activity.Kind == "task" ? text[..limit] + "…" : "…" + text[^limit..];
        }
        item["text"] = text;
        return item;
    }

    /// <summary>In the shape of a message's meta.tool_calls item, so the phone reads
    /// both the same way.</summary>
    private static JsonObject? Tool(IReadOnlyList<ToolCallDelta>? deltas, bool compact)
    {
        if (deltas is not { Count: > 0 }) return null;
        string? label = null, summary = null, detail = null, arguments = null, result = null, provider = null;
        foreach (var delta in deltas)
        {
            label = delta.Label ?? label;
            summary = delta.Summary ?? summary;
            detail = delta.Detail ?? detail;
            arguments = delta.ArgumentsJson ?? arguments;
            result = delta.ResultPreviewJson ?? result;
            provider = delta.Provider ?? provider;
        }
        var last = deltas[deltas.Count - 1];
        return new JsonObject
        {
            ["id"] = last.Id,
            ["name"] = last.Name,
            ["status"] = last.Status,
            ["label"] = Head(label, ActionLimit),
            ["summary"] = Head(summary, ArgumentsLimit),
            ["detail"] = compact ? null : Head(detail, ResultLimit),
            ["arguments_json"] = compact ? null : Head(arguments, ArgumentsLimit),
            ["result_preview_json"] = compact ? null : Head(result, ResultLimit),
            ["provider"] = Head(provider, ActionLimit)
        };
    }

    private static string? Head(string? text, int limit) =>
        text is null || text.Length <= limit ? text : text[..limit] + "…";

    private static string Tail(string text, int limit) =>
        text.Length <= limit ? text : "…" + text[^limit..];
}
