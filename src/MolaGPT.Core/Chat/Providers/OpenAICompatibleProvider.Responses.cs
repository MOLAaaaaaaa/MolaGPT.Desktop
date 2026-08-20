using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Providers;

/// <summary>
/// OpenAI Responses API (/v1/responses) wire path for <see cref="OpenAICompatibleProvider"/>.
///
/// Differences from Chat Completions:
///   - request body uses <c>input</c> (array of typed items) instead of <c>messages</c>;
///     system turns move to a top-level <c>instructions</c> string.
///   - content parts are role-typed: <c>input_text</c>/<c>input_image</c> for user/system,
///     <c>output_text</c> for assistant history.
///   - reasoning is expressed as <c>reasoning: { effort }</c> (no chat-completions
///     <c>reasoning_effort</c> / <c>"none"</c> dialect).
///   - output cap is <c>max_output_tokens</c>.
///   - tool definitions are flat (<c>{ type, name, description, parameters }</c>), and
///     tool turns round-trip via <c>function_call</c> / <c>function_call_output</c> items.
///   - the SSE stream has NO <c>[DONE]</c> terminator — it ends at EOF with a
///     <c>response.completed</c> event (tolerated via an EOF stop fallback).
///
/// Mirrors the proven MolaGPT-Mobile <c>ByokChatService</c> Responses path, but fixes
/// its assistant-history bug by emitting <c>output_text</c> parts.
/// </summary>
public sealed partial class OpenAICompatibleProvider
{
    public const string DefaultResponsesPath = "v1/responses";

    /// <summary>Selects the outgoing OpenAI wire format. Defaults to Chat Completions;
    /// BYOK entries typed "openai-response" set this to <see cref="OpenAiWireApi.Responses"/>.</summary>
    public OpenAiWireApi WireApi { get; init; } = OpenAiWireApi.ChatCompletions;

    private async IAsyncEnumerable<ChatChunk> StreamResponsesAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var localToolOptions = LocalToolOptions.FromExtraBody(request.ExtraBody);
        localToolOptions = WithConversationWorkspace(localToolOptions, request);
        var modelSupportsTools = SupportsLocalTools(request.ModelId);
        var modelSupportsVision = SupportsVision(request.ModelId);
        var toolContext = new ChatToolContext(request, Id, request.ModelId, modelSupportsVision, Models, _http);

        var localToolDefinitions = modelSupportsTools
            ? LocalToolRegistry.BuildOpenAiToolDefinitions(localToolOptions)
            : Array.Empty<object>();
        var extendedToolDefinitions = modelSupportsTools && _toolHost is not null
            ? await _toolHost.BuildToolDefinitionsAsync(toolContext, localToolOptions, ct).ConfigureAwait(false)
            : Array.Empty<object>();
        var chatToolDefinitions = localToolDefinitions.Concat(extendedToolDefinitions).ToArray();
        var useLocalTools = chatToolDefinitions.Length > 0;
        var responsesToolDefinitions = useLocalTools
            ? ToResponsesToolDefinitions(chatToolDefinitions)
            : Array.Empty<object>();

        var replaceImagesWithText = !modelSupportsVision && localToolOptions.Vision?.Enabled == true;
        var attachmentOptions = AttachmentPromptOptions.From(localToolOptions, modelSupportsTools);
        var instructions = BuildInstructions(request);
        // Off the calling thread for the same reason as the chat-completions path:
        // the iterator body runs on the UI thread until the first real suspension,
        // and base64-encoding history images is not free.
        var inputItems = await Task.Run(() =>
        {
            var imageOrdinal = 0;
            var built = new List<object>(request.Messages.Count);
            foreach (var m in request.Messages)
            {
                if (m.Role == ChatMessage.RoleSystem) continue;
                if (m.Role == ChatMessage.RoleAssistant
                    && OpenAiWireHistory.TryRead(
                        m.OpenAiWireHistoryJson,
                        OpenAiWireApi.Responses,
                        Id,
                        request.ModelId,
                        out var preservedItems))
                {
                    built.AddRange(preservedItems.Cast<object>());
                    continue;
                }
                built.Add(ToResponsesInputItem(m, replaceImagesWithText, ref imageOrdinal, attachmentOptions));
            }
            return built;
        }, ct).ConfigureAwait(false);
        var turnInputItems = new List<object>();

        // Non-streaming tool rounds until the model stops calling tools. The first
        // no-call response is already the final answer, so surface it directly rather
        // than discarding it and paying for a duplicate streaming request.
        if (useLocalTools)
        {
            while (true)
            {
                var batch = await FetchResponsesToolCallsAsync(
                    request, inputItems, instructions, responsesToolDefinitions, ct).ConfigureAwait(false);
                if (batch is null)
                    throw new InvalidOperationException("Responses API 响应缺少 output 数组。");

                foreach (var preamble in batch.Preamble)
                    yield return preamble;

                foreach (var outputItem in batch.OutputItems)
                {
                    inputItems.Add(outputItem);
                    turnInputItems.Add(outputItem);
                }

                if (batch.Calls.Count == 0)
                {
                    yield return new ChatChunk(
                        FinishReason: "stop",
                        Usage: batch.Usage,
                        OpenAiWireHistoryJson: OpenAiWireHistory.Serialize(
                            OpenAiWireApi.Responses,
                            Id,
                            request.ModelId,
                            turnInputItems));
                    yield break;
                }

                foreach (var call in batch.Calls)
                {
                    var name = string.IsNullOrWhiteSpace(call.Name) ? "unknown" : call.Name;
                    yield return new ChatChunk(Tool: BuildToolDelta(call, localToolOptions, "running"));
                    var result = await ExecuteToolAsync(
                        name, call.Arguments.ToString(), toolContext, localToolOptions, ct).ConfigureAwait(false);
                    yield return new ChatChunk(Tool: BuildToolDelta(
                        call, localToolOptions, IsToolError(result) ? "error" : "completed", result));

                    var functionOutput = new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = call.Id,
                        ["output"] = result
                    };
                    inputItems.Add(functionOutput);
                    turnInputItems.Add(functionOutput);
                }
            }
        }

        await foreach (var chunk in StreamResponsesFinalAsync(
            request,
            inputItems,
            turnInputItems,
            instructions,
            ct).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>Streaming final turn: no tools (already resolved), maps response.* SSE
    /// events into text/thinking/usage/finish chunks.</summary>
    private async IAsyncEnumerable<ChatChunk> StreamResponsesFinalAsync(
        ChatRequest request,
        IReadOnlyList<object> inputItems,
        IReadOnlyList<object> precedingTurnItems,
        string? instructions,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildResponsesRequestBody(request, inputItems, instructions, toolDefinitions: null, stream: true);
        var url = NetworkSecurity.CombineEndpoint(BaseUrl, ChatPath, DisplayName);
        var apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"{DisplayName}缺少可用的访问令牌。");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyCustomHeaders(req);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized && UnauthorizedHandler is not null)
            await UnauthorizedHandler(ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, DisplayName, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var mapper = new ResponseStreamEventMapper();
        var thinkSplitter = new InlineThinkSplitter();
        string? finishReason = null;
        Usage? finalUsage = null;
        string? finishRawJson = null;
        var finalOutputItems = new List<object>();
        var assistantText = new StringBuilder();

        await foreach (var ev in SseStreamReader.ReadAsync(stream, ct))
        {
            // Responses streams have no [DONE] terminator, but tolerate one from proxies.
            if (ev.IsDone) break;
            if (string.IsNullOrEmpty(ev.Data)) continue;

            ChatChunk? chunk = null;
            try
            {
                using var doc = JsonDocument.Parse(ev.Data);
                var root = doc.RootElement;
                if (ChatApiErrorHelper.TryExtractStreamingError(root, out var streamError))
                    throw new InvalidOperationException(streamError);
                CollectResponsesOutputItems(root, finalOutputItems);

                if (mapper.TryMapControl(root, out var controlFinish, out var usage, out var controlError))
                {
                    if (!string.IsNullOrEmpty(controlError))
                        throw new InvalidOperationException(controlError);
                    if (!string.IsNullOrEmpty(controlFinish))
                    {
                        finishReason = controlFinish;
                        finishRawJson = ev.Data;
                    }
                    if (usage is not null)
                    {
                        finalUsage = usage;
                        chunk = new ChatChunk(Usage: usage, RawJson: ev.Data);
                    }
                }
                else if (mapper.TryMap(root, out var text, out var thinking))
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        var split = thinkSplitter.Feed(text);
                        text = string.IsNullOrEmpty(split.Visible) ? null : split.Visible;
                        if (!string.IsNullOrEmpty(split.Thinking))
                            thinking = string.IsNullOrEmpty(thinking) ? split.Thinking : thinking + split.Thinking;
                    }
                    if (!string.IsNullOrEmpty(text)) assistantText.Append(text);
                    chunk = string.IsNullOrEmpty(text) && string.IsNullOrEmpty(thinking)
                        ? null
                        : new ChatChunk(DeltaText: text, DeltaThinking: thinking, RawJson: ev.Data);
                }
            }
            catch (JsonException)
            {
                chunk = new ChatChunk(RawJson: ev.Data);
            }
            if (chunk is not null) yield return chunk;
        }

        var tail = thinkSplitter.Flush();
        if (!string.IsNullOrEmpty(tail.Visible)) assistantText.Append(tail.Visible);
        if (!string.IsNullOrEmpty(tail.Visible) || !string.IsNullOrEmpty(tail.Thinking))
            yield return new ChatChunk(
                DeltaText: string.IsNullOrEmpty(tail.Visible) ? null : tail.Visible,
                DeltaThinking: string.IsNullOrEmpty(tail.Thinking) ? null : tail.Thinking);

        if (!finalOutputItems.Any(IsResponsesMessageItem) && assistantText.Length > 0)
            finalOutputItems.Add(BuildSyntheticResponsesMessage(assistantText.ToString()));

        var turnItems = new List<object>(precedingTurnItems.Count + finalOutputItems.Count);
        turnItems.AddRange(precedingTurnItems);
        turnItems.AddRange(finalOutputItems);

        // Defer the terminal chunk until every output item and inline-thinking tail
        // has been captured. Callers stop enumerating as soon as FinishReason arrives.
        yield return new ChatChunk(
            FinishReason: finishReason ?? "stop",
            Usage: finalUsage,
            RawJson: finishRawJson,
            OpenAiWireHistoryJson: OpenAiWireHistory.Serialize(
                OpenAiWireApi.Responses,
                Id,
                request.ModelId,
                turnItems));
    }

    private static void CollectResponsesOutputItems(JsonElement root, List<object> outputItems)
    {
        var type = ReadString(root, "type");
        if (string.Equals(type, "response.output_item.done", StringComparison.Ordinal)
            && root.TryGetProperty("item", out var item)
            && item.ValueKind == JsonValueKind.Object)
        {
            outputItems.Add(item.Clone());
            return;
        }

        if (type is not ("response.completed" or "response.incomplete")) return;
        if (!root.TryGetProperty("response", out var response)
            || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // The terminal response.output is authoritative and avoids duplicates when
        // output_item.done events were also present. Some compatible proxies send
        // an empty terminal array, so retain already completed items in that case.
        var authoritative = output.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => (object)item.Clone())
            .ToArray();
        if (authoritative.Length == 0 && outputItems.Count > 0) return;
        outputItems.Clear();
        outputItems.AddRange(authoritative);
    }

    private static bool IsResponsesMessageItem(object item) =>
        item is JsonElement element
        && element.ValueKind == JsonValueKind.Object
        && string.Equals(ReadString(element, "type"), "message", StringComparison.Ordinal);

    private static object BuildSyntheticResponsesMessage(string text) =>
        new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["role"] = ChatMessage.RoleAssistant,
            ["content"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "output_text",
                    ["text"] = text
                }
            }
        };

    /// <summary>Non-streaming tool round: returns the raw output items, tool calls,
    /// assistant preamble text/reasoning, and usage reported by the response.</summary>
    private async Task<ResponsesToolCallBatch?> FetchResponsesToolCallsAsync(
        ChatRequest request,
        IReadOnlyList<object> inputItems,
        string? instructions,
        object[] toolDefinitions,
        CancellationToken ct)
    {
        var body = BuildResponsesRequestBody(request, inputItems, instructions, toolDefinitions, stream: false);
        var url = NetworkSecurity.CombineEndpoint(BaseUrl, ChatPath, DisplayName);
        var apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"{DisplayName}缺少可用的访问令牌。");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ApplyCustomHeaders(req);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized && UnauthorizedHandler is not null)
            await UnauthorizedHandler(ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, DisplayName, ct).ConfigureAwait(false);

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        var outputItems = output.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => (object)item.Clone())
            .ToArray();
        var calls = new List<PendingOpenAiToolCall>();
        var messageText = new StringBuilder();
        var reasoningText = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            switch (ReadString(item, "type"))
            {
                case "function_call":
                {
                    var call = new PendingOpenAiToolCall
                    {
                        Id = ReadString(item, "call_id") ?? ReadString(item, "id") ?? ("call_" + Guid.NewGuid().ToString("N")),
                        Name = ReadString(item, "name") ?? "unknown"
                    };
                    call.Arguments.Append(ReadString(item, "arguments") ?? string.Empty);
                    calls.Add(call);
                    break;
                }
                case "message":
                {
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var part in content.EnumerateArray())
                            if (ReadString(part, "type") == "output_text" && ReadString(part, "text") is { Length: > 0 } t)
                                messageText.Append(t);
                    break;
                }
                case "reasoning":
                {
                    if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
                        foreach (var s in summary.EnumerateArray())
                            if (ReadString(s, "text") is { Length: > 0 } t)
                                reasoningText.Append(t);
                    break;
                }
            }
        }

        var preamble = new List<ChatChunk>(2);
        if (reasoningText.Length > 0)
            preamble.Add(new ChatChunk(DeltaThinking: reasoningText.ToString()));
        if (messageText.Length > 0)
            preamble.Add(new ChatChunk(DeltaText: messageText.ToString()));

        return new ResponsesToolCallBatch(
            preamble,
            calls,
            outputItems,
            ParseResponsesUsage(root));
    }

    private static Usage? ParseResponsesUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var input = usage.TryGetProperty("input_tokens", out var inputNode) && inputNode.ValueKind == JsonValueKind.Number
            ? inputNode.GetInt32()
            : (int?)null;
        var output = usage.TryGetProperty("output_tokens", out var outputNode) && outputNode.ValueKind == JsonValueKind.Number
            ? outputNode.GetInt32()
            : (int?)null;
        var total = usage.TryGetProperty("total_tokens", out var totalNode) && totalNode.ValueKind == JsonValueKind.Number
            ? totalNode.GetInt32()
            : (int?)null;
        return input is null && output is null && total is null
            ? null
            : new Usage(input, output, total);
    }

    private Dictionary<string, object?> BuildResponsesRequestBody(
        ChatRequest request,
        IReadOnlyList<object> inputItems,
        string? instructions,
        object[]? toolDefinitions,
        bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["input"] = inputItems,
            ["stream"] = stream,
        };
        if (!string.IsNullOrWhiteSpace(instructions))
            body["instructions"] = instructions;
        if (request.MaxTokens is not null)
            body["max_output_tokens"] = request.MaxTokens;

        // Reasoning gate mirrors the chat path: only send reasoning when thinking is
        // on AND the model has a reasoning dialect. When explicitly off, omit reasoning
        // entirely (Responses has no chat-completions "none" sentinel).
        if (request.UseThinking == true
            && request.ThinkingParamKind is not (null or ThinkingParamKind.None))
        {
            body["reasoning"] = new
            {
                effort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? "medium" : request.ReasoningEffort
            };
        }

        if (toolDefinitions is { Length: > 0 })
        {
            body["tools"] = toolDefinitions;
            body["tool_choice"] = "auto";
        }

        // Internal tool flags (enabled_tools) never go on the wire; any other ExtraBody
        // keys merge last, mirroring BuildRequestBody.
        ApplyCustomBody(body, request.ModelId);

        if (request.ExtraBody is not null)
            foreach (var kv in request.ExtraBody)
                if (kv.Key != "enabled_tools") body[kv.Key] = kv.Value;

        return body;
    }

    /// <summary>Builds one Responses <c>input</c> item. Assistant history uses
    /// <c>output_text</c>; user/system use <c>input_text</c>/<c>input_image</c>.</summary>
    private static object ToResponsesInputItem(
        ChatMessage message,
        bool replaceImagesWithText,
        ref int imageOrdinal,
        AttachmentPromptOptions attachmentOptions)
    {
        var textType = message.Role == ChatMessage.RoleAssistant ? "output_text" : "input_text";

        if (message.Attachments is null || message.Attachments.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.AsText()
            };
        }

        var parts = new List<object>();
        var text = message.AsText();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(new { type = textType, text });

        foreach (var attachment in message.Attachments)
        {
            if (attachment.Kind != AttachmentKind.Image) continue;

            imageOrdinal++;
            if (attachment.IsUnavailable)
            {
                parts.Add(new
                {
                    type = textType,
                    text = OpenAiMessageContentBuilder.UnavailableImageNote(attachment, imageOrdinal)
                });
                continue;
            }

            if (replaceImagesWithText)
            {
                parts.Add(new
                {
                    type = textType,
                    text = OpenAiMessageContentBuilder.ImagePlaceholder(attachment, imageOrdinal)
                });
                continue;
            }

            var url = !string.IsNullOrWhiteSpace(attachment.RemoteUrl)
                ? attachment.RemoteUrl!
                : $"data:{attachment.MimeType};base64,{Convert.ToBase64String(attachment.Bytes)}";
            parts.Add(new { type = "input_image", image_url = url });
        }

        var fileSection = AttachedFilePrompt.Build(
            message.Attachments.Where(a => a.Kind == AttachmentKind.File).ToList(),
            attachmentOptions);
        if (!string.IsNullOrWhiteSpace(fileSection))
            parts.Add(new { type = textType, text = fileSection });

        return new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = parts
        };
    }

    /// <summary>Flattens Chat-Completions tool defs (<c>{type, function:{...}}</c>) into
    /// the Responses shape (<c>{type:"function", name, description, parameters}</c>).</summary>
    private static object[] ToResponsesToolDefinitions(IReadOnlyList<object> chatToolDefinitions)
    {
        var result = new List<object>(chatToolDefinitions.Count);
        foreach (var def in chatToolDefinitions)
        {
            var element = JsonSerializer.SerializeToElement(def);
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("function", out var fn)
                && fn.ValueKind == JsonValueKind.Object)
            {
                var flat = new Dictionary<string, object?> { ["type"] = "function" };
                if (fn.TryGetProperty("name", out var name)) flat["name"] = name.Clone();
                if (fn.TryGetProperty("description", out var desc)) flat["description"] = desc.Clone();
                if (fn.TryGetProperty("parameters", out var pars)) flat["parameters"] = pars.Clone();
                result.Add(flat);
            }
            else
            {
                result.Add(element.Clone());
            }
        }
        return result.ToArray();
    }

    private static string? BuildInstructions(ChatRequest request)
    {
        string? instructions = null;
        foreach (var m in request.Messages)
        {
            if (m.Role != ChatMessage.RoleSystem) continue;
            var text = m.AsText();
            if (string.IsNullOrWhiteSpace(text)) continue;
            instructions = instructions is null ? text : instructions + "\n\n" + text;
        }
        return instructions;
    }

    private sealed record ResponsesToolCallBatch(
        IReadOnlyList<ChatChunk> Preamble,
        IReadOnlyList<PendingOpenAiToolCall> Calls,
        IReadOnlyList<object> OutputItems,
        Usage? Usage);
}

/// <summary>OpenAI-protocol wire format for <see cref="OpenAICompatibleProvider"/>.</summary>
public enum OpenAiWireApi
{
    ChatCompletions,
    Responses
}
