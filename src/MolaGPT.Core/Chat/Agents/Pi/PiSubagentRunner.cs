using System.Diagnostics;
using System.Text;
using MolaGPT.Core.Chat.Tasks;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Runs each sub-agent in its own retained Pi transcript. The provider and model
/// come from sub-agent settings; the prompt and tool switches come from the parent.
///
/// The child's limits are enforced when its calls arrive, so it can keep the
/// parent's tool list. Same-provider runs can also reuse the parent's cached prefix.
/// </summary>
public sealed class PiSubagentRunner : ISubagentRunner
{
    private readonly ProviderRegistry _providers;
    private readonly Func<SubagentRuntimeOptions> _options;
    private readonly AgentTaskRegistry _tasks;

    public PiSubagentRunner(ProviderRegistry providers, Func<SubagentRuntimeOptions> options, AgentTaskRegistry tasks)
    {
        _providers = providers;
        _options = options;
        _tasks = tasks;
    }

    public SubagentModel ResolveModel(ChatToolContext parent)
    {
        var settings = _options();
        var providerId = string.IsNullOrWhiteSpace(settings.ProviderId) ? parent.ProviderId : settings.ProviderId;
        var modelId = string.IsNullOrWhiteSpace(settings.ModelId) ? parent.ModelId : settings.ModelId;
        if (_providers.GetById(providerId) is not PiWorkProvider provider
            || !provider.Models.Any(model => model.Id == modelId))
            throw new InvalidOperationException("子 Agent 模型不可用，请检查设置。");
        return new SubagentModel(providerId, modelId);
    }

    public async Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct)
    {
        var parent = request.Parent;
        var agent = request.Agent;
        if (_providers.GetById(agent.ProviderId) is not PiWorkProvider provider)
            return new SubagentResult(false, "当前模型服务不支持子 Agent。");

        var parentConversation = agent.ConversationId;

        // Never derived from the parent's id: transcripts are found by substring, and
        // a child named after its parent would be mistaken for it.
        var childKey = "subagent_" + agent.Id;
        if (!request.FirstRun && !provider.HasSession(childKey))
            return new SubagentResult(false, "子 Agent 的会话记录不存在，无法接续此前工作。请重新派发任务。");
        var queued = new HashSet<AgentMessage>();
        var messageGate = new object();
        var initialMessages = _tasks.PeekAgentMessages(parentConversation, agent.Id);
        var displayBuffer = new StringBuilder();
        var displayThinking = false;
        var displayFlushedAt = Stopwatch.GetTimestamp();
        void FlushDisplay()
        {
            if (displayBuffer.Length == 0) return;
            _tasks.AppendAgentText(parentConversation, agent.Id, displayBuffer.ToString(), displayThinking);
            displayBuffer.Clear();
            displayFlushedAt = Stopwatch.GetTimestamp();
        }
        void QueueDisplay(string delta, bool thinking)
        {
            if (displayBuffer.Length > 0 && displayThinking != thinking) FlushDisplay();
            displayThinking = thinking;
            displayBuffer.Append(delta);
            if (displayBuffer.Length >= 1024
                || Stopwatch.GetElapsedTime(displayFlushedAt) >= TimeSpan.FromMilliseconds(100))
                FlushDisplay();
        }
        void EnqueuePending(string targetId)
        {
            if (targetId != agent.Id) return;
            lock (messageGate)
            {
                foreach (var message in _tasks.PeekAgentMessages(parentConversation, agent.Id))
                {
                    if (initialMessages.Contains(message) || queued.Contains(message)) continue;
                    if (provider.TryEnqueue(childKey, FormatMessage(message), followUp: false))
                        queued.Add(message);
                }
            }
        }
        _tasks.AgentMessageQueued += EnqueuePending;
        try
        {
            if (request.FirstRun && request.InheritContext)
            {
                if (!await provider.ForkSessionAsync(parentConversation, childKey, ct).ConfigureAwait(false))
                    return new SubagentResult(false, "无法读取主 Agent 的会话上下文。");
            }

            var messages = parent.Request.Messages
                .Where(m => m.Role == ChatMessage.RoleSystem)
                .ToList();
            messages.Add(new ChatMessage(ChatMessage.RoleSystem,
                SubagentTool.BuildChildSystemPrompt(agent.Id)));
            var prompt = request.FirstRun
                ? SubagentTool.BuildChildPrompt(request.Task)
                : "<subagent-followup>\n" + request.Task + "\n</subagent-followup>";
            if (initialMessages.Count > 0)
                prompt += "\n\n" + string.Join("\n\n", initialMessages.Select(FormatMessage));
            messages.Add(new ChatMessage(ChatMessage.RoleUser, prompt));

            var child = parent.Request with
            {
                Messages = messages,
                ConversationId = childKey,
                ModelId = agent.ModelId,
                SessionId = Guid.NewGuid().ToString("N"),
                ExtraBody = ChildExtraBody(parent.Request.ExtraBody, parentConversation, agent.Id),
                HistorySeed = null,
                HistoryRevision = null,
                RolePrompt = null,
                IsSubagent = true,
                Parent = parent.Request
            };

            // The answer is what the child wrote after its last tool call; text
            // before a call is narration ("先搜索一下"), not the result.
            var segment = new StringBuilder();
            var whole = new StringBuilder();
            var sawTool = false;
            var initialAcknowledged = false;
            string? finishReason = null;
            _tasks.BeginAgentTurn(parentConversation, agent.Id, request.Task);
            await foreach (var chunk in provider.StreamChatAsync(child, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (!initialAcknowledged && (chunk.DeltaText is not null || chunk.Tool is not null))
                {
                    _tasks.AcknowledgeAgentMessages(parentConversation, agent.Id, initialMessages.Select(m => m.Id));
                    initialAcknowledged = true;
                }
                if (chunk.Tool is { } tool)
                {
                    FlushDisplay();
                    _tasks.UpdateAgentTool(parentConversation, agent.Id, tool);
                }
                if (chunk.DeltaThinking is { Length: > 0 } thinking)
                    QueueDisplay(thinking, thinking: true);
                if (chunk.Tool is { Status: "preparing" or "running" })
                {
                    segment.Clear();
                    sawTool = true;
                }
                if (chunk.DeltaText is { Length: > 0 } text)
                {
                    segment.Append(text);
                    whole.Append(text);
                    QueueDisplay(text, thinking: false);
                }
                if (chunk.Injected is { } injected)
                {
                    lock (messageGate)
                    {
                        var received = queued.Where(m => injected.Text.Contains("<message-id>" + m.Id + "</message-id>", StringComparison.Ordinal)).ToArray();
                        _tasks.AcknowledgeAgentMessages(parentConversation, agent.Id, received.Select(m => m.Id));
                        queued.ExceptWith(received);
                    }
                }
                EnqueuePending(agent.Id);
                if (chunk.FinishReason is not null)
                {
                    finishReason = chunk.FinishReason;
                    break;
                }
            }

            var answer = sawTool ? segment.ToString().Trim() : whole.ToString().Trim();
            if (finishReason == "stop" && !initialAcknowledged)
                _tasks.AcknowledgeAgentMessages(parentConversation, agent.Id, initialMessages.Select(m => m.Id));
            if (finishReason == "length")
                return new SubagentResult(false, "子 Agent 达到输出上限，结果不完整。" + (answer.Length > 0 ? "\n" + answer : ""));
            if (finishReason != "stop")
                return new SubagentResult(false, "子 Agent 运行未正常结束。" + (answer.Length > 0 ? "\n" + answer : ""));
            return string.IsNullOrWhiteSpace(answer)
                ? new SubagentResult(false, "子 Agent 结束时没有给出结果。")
                : new SubagentResult(true, answer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SubagentResult(false, "子 Agent 出错：" + ex.Message);
        }
        finally
        {
            FlushDisplay();
            _tasks.AgentMessageQueued -= EnqueuePending;
        }
    }

    private static string FormatMessage(AgentMessage message) =>
        "<agent-message>\n<message-id>" + message.Id + "</message-id>\n<agent-id>"
        + System.Security.SecurityElement.Escape(message.AgentId) + "</agent-id>\n<text>"
        + System.Security.SecurityElement.Escape(message.Text) + "</text>\n</agent-message>";

    /// <summary>The parent's request body with the child's two markers added: it is a
    /// sub-agent, and it works in the parent's directory.</summary>
    private static Dictionary<string, object> ChildExtraBody(
        Dictionary<string, object>? parent,
        string? workspaceConversation,
        string agentId)
    {
        var body = parent is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(parent);

        var tools = body.TryGetValue(InternalExtraBodyKeys.EnabledTools, out var raw)
                    && raw is IDictionary<string, object?> dictionary
            ? new Dictionary<string, object?>(dictionary)
            : new Dictionary<string, object?>();
        tools["subagent"] = true;
        tools["agentId"] = agentId;
        if (!string.IsNullOrWhiteSpace(workspaceConversation))
            tools["workspaceConversation"] = workspaceConversation;
        body[InternalExtraBodyKeys.EnabledTools] = tools;
        return body;
    }
}
