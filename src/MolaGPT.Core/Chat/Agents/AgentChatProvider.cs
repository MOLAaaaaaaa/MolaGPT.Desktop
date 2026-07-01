using System.Runtime.CompilerServices;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Bridges a persistent agent backend (Claude Code / Codex) into the stateless
/// <see cref="IChatProvider"/> abstraction the chat UI already speaks. The
/// persistent process keeps its own context, so only the latest user turn is
/// forwarded per request; the conversation id ties the request to its session.
/// One instance per backend (id "claude-code" / "codex").
/// </summary>
public sealed class AgentChatProvider : IChatProvider
{
    private readonly AgentSessionManager _manager;
    private readonly string _backendId;

    public string Id { get; }
    public string DisplayName { get; }
    public ProviderKind Kind => ProviderKind.Agent;
    public IReadOnlyList<ProviderModel> Models { get; }

    public AgentChatProvider(
        string backendId,
        string providerId,
        string displayName,
        IReadOnlyList<ProviderModel> models,
        AgentSessionManager manager)
    {
        _backendId = backendId;
        Id = providerId;
        DisplayName = displayName;
        Models = models;
        _manager = manager;
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var userText = ExtractLatestUserText(request.Messages);
        if (string.IsNullOrWhiteSpace(userText))
            yield break;

        var session = await _manager.GetOrCreateAsync(_backendId, request.ConversationId, ct).ConfigureAwait(false);

        await foreach (var ev in session.SendTurnAsync(AgentTurnInput.TextOnly(userText), ct).ConfigureAwait(false))
        {
            var chunk = MapEvent(ev);
            if (chunk is not null)
                yield return chunk;

            if (ev.Kind == AgentEventKind.Error)
                throw new InvalidOperationException(ev.ErrorMessage ?? "Agent turn failed.");
        }
    }

    private static ChatChunk? MapEvent(AgentEvent ev) => ev.Kind switch
    {
        AgentEventKind.TextDelta => new ChatChunk(DeltaText: ev.Text, RawJson: ev.RawJson),
        AgentEventKind.ThinkingDelta => new ChatChunk(DeltaThinking: ev.Text, RawJson: ev.RawJson),
        AgentEventKind.ToolCall when ev.Tool is not null => new ChatChunk(
            Tool: new ToolCallDelta(
                ev.Tool.Id,
                ev.Tool.Name,
                MapToolStatus(ev.Tool.Status),
                Label: ev.Tool.Title,
                ArgumentsJson: ev.Tool.ArgumentsJson,
                ResultPreviewJson: ev.Tool.ResultPreview),
            RawJson: ev.RawJson),
        AgentEventKind.TurnComplete => new ChatChunk(
            FinishReason: "stop",
            Usage: ev.Usage is null ? null : new Usage(ev.Usage.InputTokens, ev.Usage.OutputTokens, ev.Usage.TotalTokens),
            RawJson: ev.RawJson),
        // Error is converted to an exception by the caller; nothing to yield here.
        _ => null
    };

    private static string MapToolStatus(AgentToolStatus status) => status switch
    {
        AgentToolStatus.Started => "running",
        AgentToolStatus.Running => "running",
        AgentToolStatus.Completed => "completed",
        AgentToolStatus.Failed => "failed",
        _ => "running"
    };

    private static string? ExtractLatestUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessage.RoleUser)
                return messages[i].AsText();
        }
        return null;
    }
}
