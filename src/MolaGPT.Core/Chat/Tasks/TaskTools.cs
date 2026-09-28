using System.Text;
using System.Text.Json;

namespace MolaGPT.Core.Chat.Tasks;

/// <summary>
/// The model's view of its background tasks: two tools, the notification it receives
/// when one ends, and the summary that rides along with later turns.
///
/// Both tool descriptions say the same thing the notification exists to make true:
/// the model is told when a task ends, so it has no reason to poll. Models that are
/// not told this sleep and re-read output files in a loop.
/// </summary>
public static class TaskTools
{
    public const string StatusToolName = "task_status";
    public const string StopToolName = "stop_task";

    /// <summary>How much of a running task's output a status query returns.</summary>
    private const int StatusTailCharacters = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static object BuildStatusDefinition() => new
    {
        type = "function",
        function = new
        {
            name = StatusToolName,
            description = "查看本对话后台任务的状态与最近输出。任务结束时会自动通知你，不要反复查询或等待。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    task_id = new { type = "string", description = "任务 ID；省略则列出本对话全部任务。" }
                }
            }
        }
    };

    public static object BuildStopDefinition() => new
    {
        type = "function",
        function = new
        {
            name = StopToolName,
            description = "停止一个正在运行的后台任务。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    task_id = new { type = "string", description = "要停止的任务 ID。" }
                },
                required = new[] { "task_id" }
            }
        }
    };

    public static string ExecuteStatus(AgentTaskRegistry registry, string conversationId, string argumentsJson)
    {
        var id = ReadTaskId(argumentsJson);
        if (!string.IsNullOrWhiteSpace(id))
        {
            var task = registry.Find(conversationId, id);
            if (task is null) return Error($"本对话没有任务 {id}。");
            if (!task.IsRunning) registry.MarkDelivered(task);
            return JsonSerializer.Serialize(Describe(registry, task, includeTail: true), JsonOptions);
        }

        var tasks = registry.List(conversationId);
        return JsonSerializer.Serialize(new
        {
            tasks = tasks.Select(t => Describe(registry, t, includeTail: false)).ToArray()
        }, JsonOptions);
    }

    public static string ExecuteStop(AgentTaskRegistry registry, string conversationId, string argumentsJson)
    {
        var id = ReadTaskId(argumentsJson);
        if (string.IsNullOrWhiteSpace(id)) return Error("缺少 task_id。");
        var task = registry.Find(conversationId, id);
        if (task is null) return Error($"本对话没有任务 {id}。");
        if (!task.IsRunning)
            return JsonSerializer.Serialize(new { success = false, task_id = task.Id, status = StatusName(task.Status), error = "任务已结束。" }, JsonOptions);

        registry.Stop(conversationId, id);
        // The model asked for it; the tool result is its notice.
        registry.MarkDelivered(task);
        return JsonSerializer.Serialize(new { success = true, task_id = task.Id, status = "stop_requested" }, JsonOptions);
    }

    /// <summary>
    /// The message that tells the model a task has ended. Sent as a user-role message
    /// so it takes part in the conversation like any other turn; the app recognises
    /// the leading tag and renders it as a notice rather than as something the user typed.
    /// </summary>
    public static string BuildNotification(AgentTask task)
    {
        var sb = new StringBuilder();
        sb.Append(NotificationOpenTag).Append('\n');
        sb.Append("<task-id>").Append(task.Id).Append("</task-id>\n");
        if (task.AgentId is { } agentId)
            sb.Append("<agent-id>").Append(agentId).Append("</agent-id>\n");
        sb.Append("<status>").Append(StatusName(task.Status)).Append("</status>\n");
        sb.Append("<label>").Append(task.Label).Append("</label>\n");
        sb.Append("<elapsed>").Append(FormatElapsed(task.Elapsed)).Append("</elapsed>\n");
        if (!string.IsNullOrWhiteSpace(task.Report))
            sb.Append("<report>\n").Append(task.Report!.Trim()).Append("\n</report>\n");
        sb.Append("</task-notification>");
        return sb.ToString();
    }

    public const string NotificationOpenTag = "<task-notification>";

    /// <summary>
    /// What a user message carries about tasks the model has not heard the end of:
    /// the ones still running, and the ones that ended without a notification —
    /// stopped by the user, or finished while waking was switched off. Appended to
    /// the end of the message so the provider's prefix cache is untouched.
    /// </summary>
    public static string? BuildTurnSummary(IReadOnlyList<AgentTask> running, IReadOnlyList<AgentTask> undelivered)
    {
        if (running.Count == 0 && undelivered.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var task in undelivered)
        {
            if (task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed)
            {
                sb.Append(BuildNotification(task)).Append("\n\n");
            }
        }

        var lines = running
            .Select(t => $"{t.Id}{(t.AgentId is { } agentId ? " / " + agentId : "")} 运行中 {FormatElapsed(t.Elapsed)} · {t.Label}")
            .Concat(undelivered
                .Where(t => t.Status is AgentTaskStatus.Cancelled or AgentTaskStatus.Interrupted)
                .Select(t => $"{t.Id}{(t.AgentId is { } agentId ? " / " + agentId : "")} "
                             + $"{(t.Status == AgentTaskStatus.Cancelled ? "已被用户停止" : "已中断")} · {t.Label}"))
            .ToList();
        if (lines.Count > 0)
            sb.Append("<background-tasks>\n").Append(string.Join("\n", lines)).Append("\n</background-tasks>");

        return sb.ToString().TrimEnd();
    }

    public static string StatusName(AgentTaskStatus status) => status switch
    {
        AgentTaskStatus.Running => "running",
        AgentTaskStatus.Completed => "completed",
        AgentTaskStatus.Failed => "failed",
        AgentTaskStatus.Cancelled => "cancelled",
        _ => "interrupted"
    };

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60) return $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s";
        return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:00}m";
    }

    private static object Describe(AgentTaskRegistry registry, AgentTask task, bool includeTail)
    {
        var tail = includeTail && task.IsRunning ? Clip(task.Tail()) : null;
        return new
        {
            task_id = task.Id,
            agent_id = task.AgentId,
            model = task.AgentId is { } agentId ? registry.FindAgent(task.ConversationId, agentId)?.ModelId : null,
            kind = task.Kind,
            label = task.Label,
            status = StatusName(task.Status),
            elapsed = task.IsRestored ? null : FormatElapsed(task.Elapsed),
            recent_output = string.IsNullOrWhiteSpace(tail) ? null : tail,
            report = includeTail && !task.IsRunning ? task.Report : null
        };
    }

    private static string Clip(string text) =>
        text.Length <= StatusTailCharacters ? text : "…" + text[^StatusTailCharacters..];

    private static string? ReadTaskId(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return doc.RootElement.TryGetProperty("task_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message }, JsonOptions);
}
