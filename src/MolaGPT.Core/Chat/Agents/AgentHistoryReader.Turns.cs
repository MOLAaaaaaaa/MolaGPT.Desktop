using System.Text;
using System.Text.Json;
using MolaGPT.Core.Chat.Agents.Relay;

namespace MolaGPT.Core.Chat.Agents;

public sealed partial class AgentHistoryReader
{
    public async Task<IReadOnlyList<AgentHistoryTurn>> LoadTurnsAsync(
        AgentHistoryEntry entry,
        int maxTurns = 30,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.FilePath) || !File.Exists(entry.FilePath))
            return Array.Empty<AgentHistoryTurn>();

        return await Task.Run(() =>
        {
            var turns = entry.BackendId switch
            {
                ClaudeCodeBackend.BackendId => ReadClaudeTurns(entry.FilePath, ct),
                CodexBackend.BackendId => ReadCodexTurns(entry.FilePath, ct),
                _ => new List<AgentHistoryTurn>()
            };

            return turns.Count <= maxTurns
                ? turns
                : turns.Skip(turns.Count - maxTurns).ToList();
        }, ct).ConfigureAwait(false);
    }

    private static List<AgentHistoryTurn> ReadClaudeTurns(string path, CancellationToken ct)
    {
        var turns = new List<AgentHistoryTurn>();
        var events = new List<RelayTranscriptEvent>();
        var answer = new StringBuilder();
        var thinking = new StringBuilder();
        var toolsById = new Dictionary<string, AgentToolEvent>(StringComparer.Ordinal);
        var inTurn = false;

        foreach (var line in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch { continue; }

            var type = ReadString(root, "type");
            if (type == "user")
            {
                if (root.TryGetProperty("message", out var userMsg) &&
                    userMsg.TryGetProperty("content", out var userContent))
                {
                    AppendClaudeUserContent(userContent);
                }

                var text = ExtractClaudeUserText(root);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Commit();
                    inTurn = true;
                    events.Add(new UserPromptEvent(text!));
                }
                continue;
            }

            if (type == "assistant" && root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                if (!inTurn)
                    continue;
                AppendClaudeAssistantContent(content);
            }
            else if (type == "result" && inTurn)
            {
                FlushThinking();
                FlushAnswer();
                events.Add(new TurnDoneEvent(ExtractClaudeUsage(root)));
                Commit(alreadyTerminated: true);
            }
        }

        Commit();
        return turns;

        void AppendClaudeAssistantContent(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                AppendBlock(answer, content.GetString());
                return;
            }

            if (content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var blockType = ReadString(block, "type");
                switch (blockType)
                {
                    case "text":
                        AppendBlock(answer, ReadString(block, "text"));
                        break;
                    case "thinking":
                        AppendBlock(thinking, ReadString(block, "thinking") ?? ReadString(block, "text"));
                        break;
                    case "tool_use":
                        FlushThinking();
                        FlushAnswer();
                        AppendClaudeToolUse(block);
                        break;
                    case "tool_result":
                        FlushThinking();
                        FlushAnswer();
                        AppendClaudeToolResult(block);
                        break;
                }
            }
        }

        void AppendClaudeUserContent(JsonElement content)
        {
            if (!inTurn || content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (ReadString(block, "type") != "tool_result") continue;

                FlushThinking();
                FlushAnswer();
                AppendClaudeToolResult(block);
            }
        }

        void AppendClaudeToolUse(JsonElement block)
        {
            var id = ReadString(block, "id") ?? Guid.NewGuid().ToString("N");
            var name = ReadString(block, "name") ?? "tool";
            var argsJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : null;
            var tool = new AgentToolEvent(
                id,
                name,
                AgentToolStatus.Started,
                Title: name,
                ArgumentsJson: argsJson);
            toolsById[id] = tool;
            events.Add(new ToolProgressEvent(tool));
        }

        void AppendClaudeToolResult(JsonElement block)
        {
            var id = ReadString(block, "tool_use_id") ?? Guid.NewGuid().ToString("N");
            var isError = block.TryGetProperty("is_error", out var er) && er.ValueKind == JsonValueKind.True;
            var preview = ReadClaudeToolResultPreview(block);
            var existing = toolsById.GetValueOrDefault(id);
            var tool = new AgentToolEvent(
                id,
                existing?.Name ?? "tool",
                isError ? AgentToolStatus.Failed : AgentToolStatus.Completed,
                Title: existing?.Title,
                ArgumentsJson: existing?.ArgumentsJson,
                ResultPreview: preview);
            toolsById[id] = tool;
            events.Add(new ToolProgressEvent(tool));
        }

        void FlushThinking()
        {
            var text = CleanBody(thinking.ToString());
            if (text is null) return;
            events.Add(new ThinkingSnapshotEvent(text));
            thinking.Clear();
        }

        void FlushAnswer()
        {
            var text = CleanBody(answer.ToString());
            if (text is null) return;
            events.Add(new AnswerSnapshotEvent(text));
            answer.Clear();
        }

        void Commit(bool alreadyTerminated = false)
        {
            if (!inTurn) return;
            FlushThinking();
            FlushAnswer();
            if (!alreadyTerminated && events.LastOrDefault() is not TurnDoneEvent)
                events.Add(new TurnDoneEvent(null));
            if (events.Count > 0)
                turns.Add(new AgentHistoryTurn(events.ToArray()));
            events.Clear();
            answer.Clear();
            thinking.Clear();
            toolsById.Clear();
            inTurn = false;
        }
    }

    private static List<AgentHistoryTurn> ReadCodexTurns(string path, CancellationToken ct)
    {
        var turns = new List<AgentHistoryTurn>();
        var events = new List<RelayTranscriptEvent>();
        var toolsById = new Dictionary<string, AgentToolEvent>(StringComparer.Ordinal);
        var inTurn = false;

        foreach (var line in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch { continue; }

            var rootType = ReadString(root, "type");
            if (!root.TryGetProperty("payload", out var payload))
                continue;

            if (rootType == "event_msg")
            {
                AppendCodexEventMessage(payload);
            }
            else if (rootType == "response_item")
            {
                AppendCodexResponseItem(payload);
            }
        }

        Commit();
        return turns;

        void AppendCodexEventMessage(JsonElement payload)
        {
            var payloadType = ReadString(payload, "type");
            if (payloadType == "user_message")
            {
                StartUserTurn(ReadString(payload, "message"));
            }
            else if (payloadType == "agent_message" && inTurn)
            {
                AddAnswer(ReadString(payload, "message"));
            }
            else if (payloadType == "custom" && inTurn && payload.TryGetProperty("item", out var item))
            {
                if (TryMapCodexTool(item) is { } tool)
                    AddTool(tool);
            }
            else if (payloadType is "task_complete" or "turn_completed" or "turn_completed_success" && inTurn)
            {
                events.Add(new TurnDoneEvent(null));
                Commit(alreadyTerminated: true);
            }
        }

        void AppendCodexResponseItem(JsonElement payload)
        {
            var payloadType = ReadString(payload, "type");
            if (payloadType == "message")
            {
                var role = ReadString(payload, "role");
                var text = ReadCodexMessageText(payload);
                if (role == "user")
                    StartUserTurn(text);
                else if (role == "assistant" && inTurn)
                    AddAnswer(text);
            }
            else if (payloadType == "function_call" && inTurn)
            {
                var id = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? Guid.NewGuid().ToString("N");
                var name = ReadString(payload, "name") ?? "tool";
                var argsJson = ReadCodexArgumentsJson(payload);
                AddTool(new AgentToolEvent(
                    id,
                    name,
                    AgentToolStatus.Started,
                    Title: name,
                    ArgumentsJson: argsJson));
            }
            else if (payloadType == "function_call_output" && inTurn)
            {
                var id = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? Guid.NewGuid().ToString("N");
                var output = ReadString(payload, "output");
                var existing = toolsById.GetValueOrDefault(id);
                AddTool(new AgentToolEvent(
                    id,
                    existing?.Name ?? "tool",
                    CodexOutputLooksFailed(output) ? AgentToolStatus.Failed : AgentToolStatus.Completed,
                    Title: existing?.Title,
                    ArgumentsJson: existing?.ArgumentsJson,
                    ResultPreview: ClipPreview(output)));
            }
            else if (payloadType == "custom_tool_call" && inTurn)
            {
                var id = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? Guid.NewGuid().ToString("N");
                var name = ReadString(payload, "name") ?? "tool";
                AddTool(new AgentToolEvent(
                    id,
                    name,
                    AgentToolStatus.Started,
                    Title: name,
                    ArgumentsJson: ReadCodexInputJson(payload)));
            }
            else if (payloadType == "custom_tool_call_output" && inTurn)
            {
                var id = ReadString(payload, "call_id") ?? ReadString(payload, "id") ?? Guid.NewGuid().ToString("N");
                var output = ReadString(payload, "output");
                var existing = toolsById.GetValueOrDefault(id);
                AddTool(new AgentToolEvent(
                    id,
                    existing?.Name ?? "tool",
                    CodexOutputLooksFailed(output) ? AgentToolStatus.Failed : AgentToolStatus.Completed,
                    Title: existing?.Title,
                    ArgumentsJson: existing?.ArgumentsJson,
                    ResultPreview: ClipPreview(output)));
            }
        }

        void StartUserTurn(string? text)
        {
            if (ShouldSkipPrompt(text)) return;

            var prompt = text!.Trim();
            if (inTurn &&
                events.Count == 1 &&
                events[0] is UserPromptEvent existingPrompt &&
                string.Equals(existingPrompt.Text, prompt, StringComparison.Ordinal))
                return;

            Commit();
            inTurn = true;
            events.Add(new UserPromptEvent(prompt));
        }

        void AddAnswer(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var answer = text!.Trim();
            if (events.LastOrDefault() is AnswerSnapshotEvent existing &&
                string.Equals(existing.Text, answer, StringComparison.Ordinal))
                return;

            events.Add(new AnswerSnapshotEvent(answer));
        }

        void AddTool(AgentToolEvent tool)
        {
            var existing = toolsById.GetValueOrDefault(tool.Id);
            var merged = new AgentToolEvent(
                tool.Id,
                string.IsNullOrWhiteSpace(tool.Name) || tool.Name == "tool"
                    ? existing?.Name ?? tool.Name
                    : tool.Name,
                tool.Status,
                Title: tool.Title ?? existing?.Title,
                ArgumentsJson: tool.ArgumentsJson ?? existing?.ArgumentsJson,
                ResultPreview: tool.ResultPreview ?? existing?.ResultPreview);
            toolsById[tool.Id] = merged;
            events.Add(new ToolProgressEvent(merged));
        }

        void Commit(bool alreadyTerminated = false)
        {
            if (!inTurn) return;
            if (!alreadyTerminated && events.LastOrDefault() is not TurnDoneEvent)
                events.Add(new TurnDoneEvent(null));
            if (events.Count > 0)
                turns.Add(new AgentHistoryTurn(events.ToArray()));
            events.Clear();
            toolsById.Clear();
            inTurn = false;
        }
    }

    private static string? ReadCodexMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content))
            return null;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = ReadString(block, "type");
            if (type is not ("input_text" or "output_text" or "text")) continue;
            AppendBlock(sb, ReadString(block, "text"));
        }
        return CleanBody(sb.ToString());
    }

    private static string? ReadCodexArgumentsJson(JsonElement payload)
    {
        if (!payload.TryGetProperty("arguments", out var args))
            return null;
        return args.ValueKind == JsonValueKind.String
            ? args.GetString()
            : args.GetRawText();
    }

    private static string? ReadCodexInputJson(JsonElement payload)
    {
        if (!payload.TryGetProperty("input", out var input))
            return null;
        return input.ValueKind == JsonValueKind.String
            ? input.GetString()
            : input.GetRawText();
    }

    private static bool CodexOutputLooksFailed(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var text = output.TrimStart();
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return true;
        return text.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase) &&
               !text.StartsWith("Exit code: 0", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentToolEvent? TryMapCodexTool(JsonElement item)
    {
        var itemType = ReadString(item, "type") ?? ReadString(item, "itemType");
        if (itemType is not ("commandExecution" or "fileChange" or "mcpToolCall" or "command" or "patchApply"))
            return null;

        var id = ReadString(item, "id") ?? Guid.NewGuid().ToString("N");
        var argsJson = item.TryGetProperty("input", out var input) ? input.GetRawText()
            : (item.TryGetProperty("command", out var cmd) ? cmd.GetRawText() : null);
        var status = ReadString(item, "state") switch
        {
            "completed" => AgentToolStatus.Completed,
            "failed" => AgentToolStatus.Failed,
            "running" => AgentToolStatus.Running,
            _ => AgentToolStatus.Started
        };
        string? resultPreview = null;
        if (item.TryGetProperty("output", out var output))
        {
            resultPreview = output.ValueKind == JsonValueKind.String
                ? output.GetString()
                : output.GetRawText();
            resultPreview = ClipPreview(resultPreview);
        }

        return new AgentToolEvent(
            id,
            itemType ?? "tool",
            status,
            Title: itemType,
            ArgumentsJson: argsJson,
            ResultPreview: resultPreview);
    }

    private static string? ReadClaudeToolResultPreview(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
            return null;

        var preview = content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : content.GetRawText();
        return ClipPreview(preview);
    }

    private static string? ClipPreview(string? preview)
    {
        if (string.IsNullOrEmpty(preview)) return preview;
        return preview.Length > 2000 ? preview[..2000] + "..." : preview;
    }

    private static AgentUsage? ExtractClaudeUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        int? input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv) ? iv : null;
        int? output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov) ? ov : null;
        if (input is null && output is null) return null;
        return new AgentUsage(input, output, (input ?? 0) + (output ?? 0));
    }

    private static void AppendBlock(StringBuilder target, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (target.Length > 0) target.AppendLine().AppendLine();
        target.Append(text.Trim());
    }

    private static bool ShouldSkipPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('<') ||
               trimmed.StartsWith('[') ||
               trimmed.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("# System instructions", StringComparison.OrdinalIgnoreCase);
    }

    private static string? CleanBody(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
