using System.Text.Json;
using MolaGPT.Core.Chat.Tools;

namespace MolaGPT.Core.Chat.Tasks;

/// <summary>One sub-agent run, as the parent's tool call asked for it.</summary>
/// <param name="Parent">The parent turn's tool context: which provider, model and
/// request (system prompt, tool switches) the child inherits.</param>
/// <param name="InheritContext">Start from a byte-for-byte copy of the parent's
/// history rather than from nothing.</param>
public sealed record SubagentRequest(
    ChatToolContext Parent,
    AgentHandle Agent,
    string Task,
    bool InheritContext,
    bool FirstRun);

public sealed record SubagentResult(bool Success, string Answer);
public sealed record SubagentModel(string ProviderId, string ModelId);
public sealed record StoredSubagent(
    string AgentId, string TaskId, string Label, string ModelId, string? ProviderId,
    AgentTaskStatus Status, string? Task, string? Report);

/// <summary>Runs a sub-agent to completion. Implemented where the agent engine lives,
/// so the tool host does not need to know how.</summary>
public interface ISubagentRunner
{
    SubagentModel ResolveModel(ChatToolContext parent);
    Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct);
}

public static class SubagentTool
{
    public const string ToolName = "spawn_agent";
    public const string SendToolName = "send_agent_message";
    public const string FollowupToolName = "followup_agent";
    public const string WaitToolName = "wait_agents";

    public static StoredSubagent? ReadStored(ToolCallDelta tool)
    {
        if (tool.Name is not (ToolName or FollowupToolName)
            || string.IsNullOrWhiteSpace(tool.ResultPreviewJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(tool.ResultPreviewJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("agent_id", out var idNode)
                || idNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("model", out var modelNode)
                || modelNode.ValueKind != JsonValueKind.String) return null;
            var agentId = idNode.GetString();
            var model = modelNode.GetString();
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(model)) return null;
            var taskId = root.TryGetProperty("task_id", out var taskNode)
                         && taskNode.ValueKind == JsonValueKind.String
                ? taskNode.GetString() : null;
            taskId ??= agentId + "-initial";
            var label = root.TryGetProperty("label", out var labelNode)
                        && labelNode.ValueKind == JsonValueKind.String
                ? labelNode.GetString() : null;
            var task = Parse(tool.ArgumentsJson ?? "{}")?.Task;
            label = string.IsNullOrWhiteSpace(label) ? Clip(task ?? agentId) : label;
            var providerId = root.TryGetProperty("provider_id", out var providerNode)
                             && providerNode.ValueKind == JsonValueKind.String
                ? providerNode.GetString() : null;
            var background = root.TryGetProperty("background", out var backgroundNode)
                             && backgroundNode.ValueKind == JsonValueKind.True;
            var succeeded = root.TryGetProperty("success", out var successNode)
                            && successNode.ValueKind == JsonValueKind.True;
            var status = tool.TaskState switch
            {
                "completed" => AgentTaskStatus.Completed,
                "failed" => AgentTaskStatus.Failed,
                "cancelled" => AgentTaskStatus.Cancelled,
                "interrupted" or "running" => AgentTaskStatus.Interrupted,
                _ => background ? AgentTaskStatus.Interrupted
                    : succeeded ? AgentTaskStatus.Completed : AgentTaskStatus.Failed
            };
            var report = root.TryGetProperty("answer", out var answerNode)
                         && answerNode.ValueKind == JsonValueKind.String
                ? answerNode.GetString() : null;
            return new StoredSubagent(agentId!, taskId, label!, model!, providerId, status, task, report);
        }
        catch (JsonException) { return null; }
    }

    public static object BuildSendDefinition() => new
    {
        type = "function",
        function = new
        {
            name = SendToolName,
            description = "给同一对话中的主 Agent 或子 Agent 留言。只发送消息，不会让空闲的子 Agent 开始新一轮工作。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    agent_id = new { type = "string", description = "收件方的 Agent ID；子 Agent 可用 parent 发给主 Agent。" },
                    message = new { type = "string", description = "要发送的消息。" }
                },
                required = new[] { "agent_id", "message" }
            }
        }
    };

    public static object BuildFollowupDefinition() => new
    {
        type = "function",
        function = new
        {
            name = FollowupToolName,
            description = "让已有的空闲子 Agent 在原会话中继续完成一项工作。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    agent_id = new { type = "string", description = "已有子 Agent 的 ID。" },
                    task = new { type = "string", description = "下一项完整任务说明。" },
                    run_in_background = new { type = "boolean", description = "默认 true。" }
                },
                required = new[] { "agent_id", "task" }
            }
        }
    };

    public static object BuildWaitDefinition() => new
    {
        type = "function",
        function = new
        {
            name = WaitToolName,
            description = "等待指定子 Agent 的任务结束或发来消息。默认最多等待 30 秒。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    agent_ids = new { type = "array", items = new { type = "string" }, description = "要等待的子 Agent ID。" },
                    timeout_seconds = new { type = "integer", description = "最长等待秒数，1 到 60，默认 30。" }
                },
                required = new[] { "agent_ids" }
            }
        }
    };

    public static object BuildDefinition() => new
    {
        type = "function",
        function = new
        {
            name = ToolName,
            description = "把一项能独立完成的工作交给子 Agent：它用你的工作目录和工具自行完成多步操作，完成后交回结果。"
                + "适合互不依赖、可以同时进行的几件事，以及需要大量阅读或反复尝试、而你只需要结论的工作；一两步能完成的事直接做。"
                + "默认在后台运行。"
                + "子 Agent 不能向用户提问，也不能操作浏览器、写入记忆、创建子 Agent 或执行需要授权的操作。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    task = new
                    {
                        type = "string",
                        description = "完整的任务说明：目标、已知信息、交付物。除非 context 为 full，子 Agent 看不到这段对话。"
                    },
                    label = new
                    {
                        type = "string",
                        description = "简短标题，展示给用户。"
                    },
                    context = new
                    {
                        type = "string",
                        @enum = new[] { "none", "full" },
                        description = "none：只提供任务说明（默认）；full：继承当前对话的全部上下文。"
                    },
                    run_in_background = new
                    {
                        type = "boolean",
                        description = "默认 true。仅当下一步必须用到结果时设为 false，此时等待子 Agent 完成并直接返回结果。"
                    }
                },
                required = new[] { "task", "label" }
            }
        }
    };

    public sealed record Arguments(string Task, string Label, bool InheritContext, bool Background);

    public static Arguments? Parse(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            var task = root.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(task)) return null;
            var label = root.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            var inherit = root.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.String
                && string.Equals(c.GetString(), "full", StringComparison.OrdinalIgnoreCase);
            var background = !(root.TryGetProperty("run_in_background", out var b) && b.ValueKind == JsonValueKind.False);
            return new Arguments(task!.Trim(), string.IsNullOrWhiteSpace(label) ? Clip(task!) : label!.Trim(), inherit, background);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text)
    {
        var line = text.Replace('\n', ' ').Trim();
        return line.Length <= 24 ? line : line[..24] + "…";
    }

    public static string BuildChildSystemPrompt(string agentId) =>
        $"你是主 Agent 委派的子 Agent，ID 为 {agentId}。只处理主 Agent 指定的范围，不要擅自改写其他 Agent 负责的部分。"
        + "独立完成任务，不要向用户提问；只有完成工作后才给出最终回复，写清结果与产出的文件。遇到阻碍应明确报告，不能把请求主 Agent 继续当作完成。"
        + "需要协调时可用 send_agent_message 给主 Agent 或同一对话的其他子 Agent 留言。"
        + "不能操作浏览器、写入记忆、创建子 Agent 或执行需要用户授权的操作；主 Agent 系统提示中的 <后台任务> 不适用于你，代码只在前台运行，超时即终止。";

    public static string BuildChildPrompt(string task) =>
        "<subagent-task>\n" + task + "\n</subagent-task>";
}
