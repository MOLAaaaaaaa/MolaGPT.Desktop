using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Models;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Providers;

/// <summary>The decoder for one official turn. It survives a switch from the live stream to resume.</summary>
public sealed class MolaGptStreamDecoder
{
    private readonly InlineThinkSplitter _thinking = new();
    private readonly StreamedToolCalls _tools = new();
    private readonly ResponseStreamEventMapper _responses = new();
    private string? _finishReason;

    public int ResumeOffset { get; private set; }

    internal async IAsyncEnumerable<ChatChunk> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in SseStreamReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (frame.IsDone)
            {
                List<ToolCallDelta>? finalTools = null;
                _tools.Finish(ref finalTools);
                if (finalTools is not null)
                    foreach (var tool in finalTools) yield return new ChatChunk(Tool: tool);
                var tail = _thinking.Flush();
                yield return new ChatChunk(
                    DeltaText: tail.Visible,
                    DeltaThinking: tail.Thinking,
                    FinishReason: _finishReason ?? "stop");
                yield break;
            }

            var chunk = Decode(frame, out var tools, out var parts);
            if (tools is not null)
                foreach (var tool in tools) yield return new ChatChunk(Tool: tool);
            if (chunk is not null)
            {
                if (parts is { Count: > 0 })
                {
                    if (chunk.DeltaThinking is { Length: > 0 } reasoning
                        && !parts.Any(part => part.Thinking == reasoning))
                        yield return new ChatChunk(DeltaThinking: reasoning);
                    for (var i = 0; i < parts.Count - 1; i++)
                        yield return new ChatChunk(DeltaText: parts[i].Visible, DeltaThinking: parts[i].Thinking);
                    chunk = chunk with { DeltaText = parts[^1].Visible, DeltaThinking = parts[^1].Thinking };
                }
                ResumeOffset += frame.DataLines;
                yield return chunk;
            }
        }
    }

    private ChatChunk? Decode(SsePayload frame, out List<ToolCallDelta>? tools,
        out List<InlineThinkSplitter.SplitResult>? parts)
    {
        tools = null;
        parts = null;
        JsonDocument? document = null;
        if (!string.IsNullOrWhiteSpace(frame.Data))
        {
            try { document = JsonDocument.Parse(frame.Data); }
            catch (JsonException) when (!frame.IsTerminated)
            {
                // The connection ended inside a JSON event. Resume must replay it.
                return null;
            }
            catch (JsonException) { }
        }

        using (document)
        {
            var root = document?.RootElement ?? default;
            if (ChatApiErrorHelper.ReadBusinessError(root, frame.EventName) is { } error)
                throw new ChatRequestException(error);
            if (frame.DataLines == 0 || string.IsNullOrWhiteSpace(frame.Data)) return null;
            if (root.ValueKind != JsonValueKind.Object)
                return new ChatChunk(RawJson: frame.Data);
            if (ReadString(root, "type") == "session_init") return null;

            _responses.TryMapControl(root, out var responseFinish, out var responseUsage, out var responseError, frame.EventName);
            if (responseError is not null)
                throw new ChatRequestException(ChatApiErrorHelper.ReadProviderError(responseError));
            _responses.TryMap(root, out var responseText, out var responseThinking, frame.EventName);

            string? choiceFinish = null, choiceText = null, choiceThinking = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0 && choices[0] is { ValueKind: JsonValueKind.Object } choice)
            {
                choiceFinish = ReadString(choice, "finish_reason");
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    choiceText = ReadContent(delta);
                    choiceThinking = ReasoningExtractor.Extract(delta);
                    _tools.Append(delta, ref tools);
                }
            }

            // A relay may include both wire dialects. Preserve independent deltas
            // and collapse an exact duplicate within the frame.
            var visible = JoinDistinct(responseText, choiceText);
            var thinking = JoinDistinct(responseThinking, choiceThinking);
            if (choiceFinish == "tool_calls") _tools.Finish(ref tools);
            if (visible is not null)
            {
                var collected = new List<InlineThinkSplitter.SplitResult>();
                _thinking.Feed(visible, collected.Add);
                parts = collected;
                visible = null;
            }

            var finish = choiceFinish is null or "tool_calls" ? responseFinish : choiceFinish;
            if (finish is not null && (_finishReason is null || finish != "stop"))
                _finishReason = finish;

            return new ChatChunk(
                DeltaText: visible,
                DeltaThinking: thinking,
                Usage: ReadUsage(root, responseUsage),
                Sources: ReadSources(root, "molagpt_sources"),
                RawJson: frame.Data,
                Subagents: ReadSubagents(root));
        }
    }

    private static JsonObject? ReadSubagents(JsonElement root)
    {
        if (!root.TryGetProperty("molagpt_subagents", out var batch) || batch.ValueKind != JsonValueKind.Object
            || string.IsNullOrEmpty(ReadString(batch, "batch"))
            || !batch.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
            return null;
        return batch.Deserialize<JsonObject>();
    }

    private static string? ReadContent(JsonElement delta)
    {
        if (!delta.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var text = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) text.Append(item.GetString());
            else if (item.ValueKind == JsonValueKind.Object && ReadString(item, "type") == "text")
                text.Append(ReadString(item, "text"));
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static Usage? ReadUsage(JsonElement root, Usage? responseUsage)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return responseUsage;
        var prompt = ReadInt(usage, "prompt_tokens") ?? responseUsage?.PromptTokens;
        var completion = ReadInt(usage, "completion_tokens") ?? responseUsage?.CompletionTokens;
        var total = ReadInt(usage, "total_tokens") ?? responseUsage?.TotalTokens;
        return prompt is null && completion is null && total is null ? null : new Usage(prompt, completion, total);
    }

    internal static IReadOnlyList<SourceReference>? ReadSources(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<SourceReference>();
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var date = ReadString(item, "published_date")?.Trim();
            list.Add(new SourceReference(ReadInt(item, "id") ?? list.Count + 1,
                ReadString(item, "title") ?? string.Empty, ReadString(item, "url") ?? string.Empty,
                string.IsNullOrEmpty(date) ? null : date));
        }
        return list.Count > 0 ? list : null;
    }

    internal static string? ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;

    private static string? JoinDistinct(string? first, string? second) => Join(first, first == second ? null : second);

    private static string? Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? string.IsNullOrEmpty(second) ? null : second
        : string.IsNullOrEmpty(second) ? first : first + second;
}
