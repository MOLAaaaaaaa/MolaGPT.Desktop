using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

    private const int MaxResponsesToolTurns = 64;

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
        var imageOrdinal = 0;
        var instructions = BuildInstructions(request);
        var inputItems = new List<object>(request.Messages.Count);
        foreach (var m in request.Messages)
        {
            if (m.Role == ChatMessage.RoleSystem) continue;
            inputItems.Add(ToResponsesInputItem(m, replaceImagesWithText, ref imageOrdinal));
        }

        // Non-streaming tool rounds until the model stops calling tools, then a
        // streaming final turn. This mirrors the Mobile path. NOTE: when tools are
        // enabled, the final (no-tool-call) turn is generated once non-streamed and
        // discarded, then re-streamed — a known cost trade-off; a future optimization
        // can parse function_call items from the stream directly.
        if (useLocalTools)
        {
            for (var turn = 0; turn < MaxResponsesToolTurns; turn++)
            {
                var batch = await FetchResponsesToolCallsAsync(
                    request, inputItems, instructions, responsesToolDefinitions, ct).ConfigureAwait(false);
                if (batch is null) break;

                foreach (var preamble in batch.Preamble)
                    yield return preamble;

                foreach (var call in batch.Calls)
                {
                    var name = string.IsNullOrWhiteSpace(call.Name) ? "unknown" : call.Name;
                    inputItems.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.Id,
                        ["name"] = name,
                        ["arguments"] = call.Arguments.ToString()
                    });

                    yield return new ChatChunk(Tool: BuildToolDelta(call, localToolOptions, "running"));
                    var result = await ExecuteToolAsync(
                        name, call.Arguments.ToString(), toolContext, localToolOptions, ct).ConfigureAwait(false);
                    yield return new ChatChunk(Tool: BuildToolDelta(
                        call, localToolOptions, IsToolError(result) ? "error" : "completed", result));

                    inputItems.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = call.Id,
                        ["output"] = result
                    });
                }
            }
        }

        await foreach (var chunk in StreamResponsesFinalAsync(request, inputItems, instructions, ct).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>Streaming final turn: no tools (already resolved), maps response.* SSE
    /// events into text/thinking/usage/finish chunks.</summary>
    private async IAsyncEnumerable<ChatChunk> StreamResponsesFinalAsync(
        ChatRequest request,
        IReadOnlyList<object> inputItems,
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

                if (mapper.TryMapControl(root, out var controlFinish, out var usage, out var controlError))
                {
                    if (!string.IsNullOrEmpty(controlError))
                        throw new InvalidOperationException(controlError);
                    if (!string.IsNullOrEmpty(controlFinish)) finishReason = controlFinish;
                    if (!string.IsNullOrEmpty(controlFinish) || usage is not null)
                        chunk = new ChatChunk(FinishReason: controlFinish, Usage: usage, RawJson: ev.Data);
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
        if (!string.IsNullOrEmpty(tail.Visible) || !string.IsNullOrEmpty(tail.Thinking))
            yield return new ChatChunk(
                DeltaText: string.IsNullOrEmpty(tail.Visible) ? null : tail.Visible,
                DeltaThinking: string.IsNullOrEmpty(tail.Thinking) ? null : tail.Thinking);

        // EOF without response.completed (e.g. a proxy that dropped it) still finalizes.
        if (finishReason is null)
            yield return new ChatChunk(FinishReason: "stop");
    }

    /// <summary>Non-streaming tool round: returns the model's tool calls plus any
    /// assistant preamble text/reasoning, or null when the model made no tool calls.</summary>
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

        if (calls.Count == 0)
            return null;

        var preamble = new List<ChatChunk>(2);
        if (reasoningText.Length > 0)
            preamble.Add(new ChatChunk(DeltaThinking: reasoningText.ToString()));
        if (messageText.Length > 0)
            preamble.Add(new ChatChunk(DeltaText: messageText.ToString()));

        return new ResponsesToolCallBatch(preamble, calls);
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
    private static object ToResponsesInputItem(ChatMessage message, bool replaceImagesWithText, ref int imageOrdinal)
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
            if (attachment.Kind == AttachmentKind.Image)
            {
                imageOrdinal++;
                if (replaceImagesWithText)
                {
                    var label = string.IsNullOrWhiteSpace(attachment.FileName)
                        ? $"[图片#{imageOrdinal}]"
                        : $"[图片#{imageOrdinal}: {attachment.FileName}]";
                    parts.Add(new { type = textType, text = label });
                    continue;
                }

                var url = !string.IsNullOrWhiteSpace(attachment.RemoteUrl)
                    ? attachment.RemoteUrl!
                    : $"data:{attachment.MimeType};base64,{Convert.ToBase64String(attachment.Bytes)}";
                parts.Add(new { type = "input_image", image_url = url });
                continue;
            }

            parts.Add(new { type = textType, text = OpenAiMessageContentBuilder.BuildFileTextPart(attachment) });
        }

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
        IReadOnlyList<PendingOpenAiToolCall> Calls);
}

/// <summary>OpenAI-protocol wire format for <see cref="OpenAICompatibleProvider"/>.</summary>
public enum OpenAiWireApi
{
    ChatCompletions,
    Responses
}
