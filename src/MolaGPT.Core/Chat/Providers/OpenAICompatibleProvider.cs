using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Providers;

/// <summary>
/// Calls any OpenAI-protocol-compatible endpoint:
///   - api.openai.com/v1/chat/completions (canonical)
///   - api.deepseek.com/v1/chat/completions
///   - dashscope.aliyuncs.com/compatible-mode/v1/chat/completions (Qwen)
///   - api.moonshot.cn/v1/chat/completions
///   - generativelanguage.googleapis.com/v1beta/openai/chat/completions (Gemini compat mode)
///   - any OneAPI deployment
///
/// Configured via <see cref="BaseUrl"/> + <see cref="ApiKey"/>; models are user-defined
/// (UI presents them via <see cref="Models"/>). This is the most common BYOK path.
/// </summary>
public sealed class OpenAICompatibleProvider : IChatProvider
{
    public string Id { get; }
    public string DisplayName { get; }
    public ProviderKind Kind { get; init; } = ProviderKind.OpenAICompatible;
    public IReadOnlyList<ProviderModel> Models { get; private set; }
    public string BaseUrl { get; }
    public string ApiKey { get; }
    public string ChatPath { get; init; } = "v1/chat/completions";

    private readonly HttpClient _http;

    public OpenAICompatibleProvider(
        string id,
        string displayName,
        string baseUrl,
        string apiKey,
        IReadOnlyList<ProviderModel> models,
        HttpClient http)
    {
        Id = id;
        DisplayName = displayName;
        BaseUrl = NetworkSecurity.RequireHttpsBaseUrl(baseUrl, $"{displayName} Base URL");
        ApiKey = apiKey;
        Models = models;
        _http = http;
    }

    public void UpdateModels(IReadOnlyList<ProviderModel> models) => Models = models;

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var localToolOptions = LocalToolOptions.FromExtraBody(request.ExtraBody);
        var localToolDefinitions = SupportsLocalTools(request.ModelId)
            ? LocalToolRegistry.BuildOpenAiToolDefinitions(localToolOptions)
            : Array.Empty<object>();
        var useLocalTools = localToolDefinitions.Count > 0;
        var wireMessages = request.Messages.Select(m => ToOpenAiWireMessage(m, request)).ToList();
        const int MaxLocalToolTurns = 64;
        var maxToolTurns = useLocalTools ? MaxLocalToolTurns : 1;

        for (var turn = 0; turn < maxToolTurns; turn++)
        {
            var url = new Uri(new Uri(BaseUrl), ChatPath);
            var body = BuildRequestBody(request, wireMessages);
            if (useLocalTools)
            {
                body["tools"] = localToolDefinitions;
                body["tool_choice"] = "auto";
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await ChatApiErrorHelper.EnsureSuccessAsync(resp, DisplayName, ct).ConfigureAwait(false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var thinkSplitter = new InlineThinkSplitter();
            var toolSynthesizer = useLocalTools ? null : new ToolCallContentSynthesizer();
            var responseMapper = new ResponseStreamEventMapper();
            var localToolCalls = new SortedDictionary<int, PendingOpenAiToolCall>();
            var assistantText = new StringBuilder();
            var assistantReasoning = new StringBuilder();
            string? finishReason = null;

            await foreach (var ev in SseStreamReader.ReadAsync(stream, ct))
            {
                if (ev.IsDone)
                {
                    var toolTail = toolSynthesizer?.FinalizeOpenBlocks();
                    if (!string.IsNullOrEmpty(toolTail))
                        yield return new ChatChunk(DeltaText: toolTail);
                    var tail = thinkSplitter.Flush();
                    if (!string.IsNullOrEmpty(tail.Visible))
                    {
                        assistantText.Append(tail.Visible);
                    }
                    if (!string.IsNullOrEmpty(tail.Thinking))
                        assistantReasoning.Append(tail.Thinking);
                    if (!string.IsNullOrEmpty(tail.Visible) || !string.IsNullOrEmpty(tail.Thinking))
                        yield return new ChatChunk(
                            DeltaText: string.IsNullOrEmpty(tail.Visible) ? null : tail.Visible,
                            DeltaThinking: string.IsNullOrEmpty(tail.Thinking) ? null : tail.Thinking,
                            FinishReason: useLocalTools && localToolCalls.Count > 0 ? null : "stop");
                    break;
                }
                if (string.IsNullOrEmpty(ev.Data)) continue;
                ChatChunk? chunk = null;
                var handledEvent = false;
                try
                {
                    using var doc = JsonDocument.Parse(ev.Data);
                    var root = doc.RootElement;
                    if (ChatApiErrorHelper.TryExtractStreamingError(root, out var streamError))
                        throw new InvalidOperationException(streamError);
                    if (TryParseSources(root, out var sources))
                    {
                        chunk = new ChatChunk(Sources: sources, RawJson: ev.Data);
                    }
                    if (responseMapper.TryMap(root, out var responseText, out var responseThinking))
                    {
                        if (!string.IsNullOrEmpty(responseText))
                        {
                            var split = thinkSplitter.Feed(responseText);
                            responseText = string.IsNullOrEmpty(split.Visible) ? null : split.Visible;
                            if (!string.IsNullOrEmpty(split.Thinking))
                                responseThinking = string.IsNullOrEmpty(responseThinking) ? split.Thinking : responseThinking + split.Thinking;
                        }
                        if (!string.IsNullOrEmpty(responseText)) assistantText.Append(responseText);
                        if (!string.IsNullOrEmpty(responseThinking)) assistantReasoning.Append(responseThinking);

                        chunk = string.IsNullOrEmpty(responseText) && string.IsNullOrEmpty(responseThinking)
                            ? null
                            : new ChatChunk(DeltaText: responseText, DeltaThinking: responseThinking, RawJson: ev.Data);
                    }
                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("finish_reason", out var choiceFinish) && choiceFinish.ValueKind == JsonValueKind.String)
                            finishReason = choiceFinish.GetString();

                        if (choice.TryGetProperty("delta", out var delta))
                        {
                            if (useLocalTools && TryCollectLocalToolCalls(delta, localToolCalls, out var pending))
                            {
                                chunk = pending;
                                handledEvent = true;
                            }
                            else
                            {
                                var toolText = toolSynthesizer?.HandleToolCalls(delta);
                                if (toolText is not null)
                                {
                                    chunk = string.IsNullOrEmpty(toolText) ? null : new ChatChunk(DeltaText: toolText, RawJson: ev.Data);
                                }
                                else
                                {
                                    string? text = ExtractContentText(delta);
                                    // Same three-channel reasoning merge as MolaGptProxyProvider.
                                    string? thinking = ReasoningExtractor.Extract(delta);
                                    string? finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                                    var finalizeToolUi = finish == "tool_calls" || (toolSynthesizer?.HasOpenBlocks == true && (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(thinking)));
                                    if (finalizeToolUi)
                                    {
                                        var toolTail = toolSynthesizer?.FinalizeOpenBlocks();
                                        if (!string.IsNullOrEmpty(toolTail))
                                            text = string.IsNullOrEmpty(text) ? toolTail : toolTail + text;
                                    }

                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        var split = thinkSplitter.Feed(text);
                                        text = string.IsNullOrEmpty(split.Visible) ? null : split.Visible;
                                        if (!string.IsNullOrEmpty(split.Thinking))
                                            thinking = string.IsNullOrEmpty(thinking) ? split.Thinking : thinking + split.Thinking;
                                    }
                                    if (!string.IsNullOrEmpty(text)) assistantText.Append(text);
                                    if (!string.IsNullOrEmpty(thinking)) assistantReasoning.Append(thinking);

                                    Usage? usage = null;
                                    if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                                    {
                                        usage = new Usage(
                                            u.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : null,
                                            u.TryGetProperty("completion_tokens", out var ct1) && ct1.ValueKind == JsonValueKind.Number ? ct1.GetInt32() : null,
                                            u.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : null);
                                    }

                                    var chunkFinish = useLocalTools && finish == "tool_calls" ? null : finish;
                                    chunk = new ChatChunk(DeltaText: text, DeltaThinking: thinking, FinishReason: chunkFinish, Usage: usage, RawJson: ev.Data);
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    chunk = new ChatChunk(RawJson: ev.Data);
                }
                if (chunk is not null) yield return chunk;
                if (handledEvent) continue;
            }

            if (!useLocalTools || localToolCalls.Count == 0)
            {
                if (finishReason is null)
                    yield return new ChatChunk(FinishReason: "stop");
                yield break;
            }

            wireMessages.Add(BuildAssistantToolCallMessage(
                assistantText.ToString(),
                assistantReasoning.ToString(),
                request,
                localToolCalls.Values));

            foreach (var toolCall in localToolCalls.Values)
            {
                var name = string.IsNullOrWhiteSpace(toolCall.Name) ? "unknown" : toolCall.Name;
                yield return new ChatChunk(Tool: BuildToolDelta(toolCall, localToolOptions, "running"));
                var result = await LocalToolRegistry.ExecuteAsync(
                    name,
                    toolCall.Arguments.ToString(),
                    localToolOptions,
                    _http,
                    ct).ConfigureAwait(false);
                yield return new ChatChunk(Tool: BuildToolDelta(
                    toolCall,
                    localToolOptions,
                    IsToolError(result) ? "error" : "completed",
                    result));
                wireMessages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolCall.Id,
                    ["content"] = result
                });
            }
        }

        yield return new ChatChunk(FinishReason: "tool_turn_limit");
    }

    private Dictionary<string, object?> BuildRequestBody(ChatRequest request, IReadOnlyList<object> wireMessages)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = wireMessages,
            ["stream"] = true,
        };
        if (request.Temperature is not null) body["temperature"] = request.Temperature;
        if (request.MaxTokens is not null) body["max_tokens"] = request.MaxTokens;
        if (request.UseThinking == true)
        {
            if (request.ThinkingParamKind == ThinkingParamKind.DeepSeekV4)
            {
                body["thinking"] = new { type = "enabled" };
                body["reasoning_effort"] = request.ReasoningEffort ?? "high";
            }
            else if (request.ThinkingParamKind == ThinkingParamKind.QwenThinkingBudget)
            {
                body["enable_thinking"] = true;
                if (request.ThinkingBudgetTokens is { } budget)
                    body["thinking_budget"] = budget;
            }
            else if (request.ThinkingParamKind == ThinkingParamKind.GeminiBudget)
            {
                body["reasoning_effort"] = request.ReasoningEffort ?? "medium";
            }
            else if (request.ThinkingParamKind == ThinkingParamKind.GeminiThinkingLevel)
            {
                body["reasoning_effort"] = request.ReasoningEffort ?? "high";
            }
            else if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            {
                body["reasoning_effort"] = request.ReasoningEffort;
            }
        }
        else if (request.UseThinking == false)
        {
            if (request.ThinkingParamKind == ThinkingParamKind.DeepSeekV4)
                body["thinking"] = new { type = "disabled" };
            else if (request.ThinkingParamKind == ThinkingParamKind.QwenThinkingBudget)
                body["enable_thinking"] = false;
            else if (request.ThinkingParamKind is ThinkingParamKind.OpenAiReasoningEffort
                     or ThinkingParamKind.GeminiBudget
                     or ThinkingParamKind.GeminiThinkingLevel)
                body["reasoning_effort"] = "none";
        }
        if (request.ExtraBody is not null)
            foreach (var kv in request.ExtraBody)
                if (kv.Key != "enabled_tools") body[kv.Key] = kv.Value;

        return body;
    }

    private bool SupportsLocalTools(string modelId) =>
        Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))?.SupportsToolCalling == true;

    private static bool ShouldPassReasoningContent(ChatRequest request, string role, string? reasoningContent) =>
        request.UseThinking == true
        && request.ThinkingParamKind == ThinkingParamKind.DeepSeekV4
        && role == ChatMessage.RoleAssistant
        && !string.IsNullOrWhiteSpace(reasoningContent);

    private static object ToOpenAiWireMessage(ChatMessage message, ChatRequest request)
    {
        var wire = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = OpenAiMessageContentBuilder.Build(message)
        };
        if (ShouldPassReasoningContent(request, message.Role, message.ReasoningContent))
            wire["reasoning_content"] = message.ReasoningContent;
        return wire;
    }

    private static Dictionary<string, object?> BuildAssistantToolCallMessage(
        string assistantText,
        string assistantReasoning,
        ChatRequest request,
        IEnumerable<PendingOpenAiToolCall> toolCalls)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = string.IsNullOrWhiteSpace(assistantText) ? null : assistantText,
            ["tool_calls"] = toolCalls.Select(t => new
            {
                id = t.Id,
                type = "function",
                function = new
                {
                    name = t.Name,
                    arguments = t.Arguments.ToString()
                }
            }).ToArray()
        };
        if (ShouldPassReasoningContent(request, ChatMessage.RoleAssistant, assistantReasoning))
            message["reasoning_content"] = assistantReasoning;
        return message;
    }

    private static bool TryCollectLocalToolCalls(
        JsonElement delta,
        SortedDictionary<int, PendingOpenAiToolCall> calls,
        out ChatChunk? pending)
    {
        pending = null;
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            var idx = ReadInt(toolCall, "index") ?? 0;
            if (!calls.TryGetValue(idx, out var state))
            {
                state = new PendingOpenAiToolCall();
                calls[idx] = state;
            }

            var id = ReadString(toolCall, "id");
            if (!string.IsNullOrWhiteSpace(id)) state.Id = id!;

            if (toolCall.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                var name = ReadString(fn, "name");
                if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(state.Name))
                {
                    state.Name = name!;
                }
                var args = ReadString(fn, "arguments");
                if (!string.IsNullOrEmpty(args)) state.Arguments.Append(args);
            }
        }

        return true;
    }

    private static ToolCallDelta BuildToolDelta(
        PendingOpenAiToolCall toolCall,
        LocalToolOptions? options,
        string status,
        string? resultJson = null)
    {
        var name = string.IsNullOrWhiteSpace(toolCall.Name) ? "unknown" : toolCall.Name;
        var args = toolCall.Arguments.ToString();
        return new ToolCallDelta(
            toolCall.Id,
            name,
            status,
            LocalToolPendingLabel(name),
            BuildToolSummary(name, args),
            BuildToolDetail(name, args, options, status, resultJson),
            PrettyJson(args),
            resultJson is null ? null : BuildResultPreview(resultJson),
            name == "search_web" ? SearchProviderLabel(options?.SearchProvider) : null);
    }

    private static bool IsToolError(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.TryGetProperty("success", out var success)
                   && success.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractToolErrorMessage(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return ReadString(doc.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? BuildToolSummary(string name, string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            var root = doc.RootElement;
            if (name == "search_web")
            {
                var queries = ReadSearchQueries(root);
                return queries.Count > 0 ? string.Join(" / ", queries.Take(3)) : "等待搜索关键词";
            }

            if (name == "web_fetch")
                return ReadString(root, "url") ?? "等待网页地址";
        }
        catch (JsonException) { }

        return string.IsNullOrWhiteSpace(args) ? null : args;
    }

    private static string? BuildToolDetail(string name, string args, LocalToolOptions? options, string status, string? resultJson)
    {
        // Errors take over the meta line so the user sees the actual failure
        // (e.g. "A valid http/https url is required.") instead of the generic
        // "读取页面标题、正文和链接" / provider hint.
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
        {
            var err = ExtractToolErrorMessage(resultJson);
            if (!string.IsNullOrWhiteSpace(err)) return err;
        }

        if (name == "search_web")
        {
            var provider = SearchProviderLabel(options?.SearchProvider);
            var count = CountSearchQueries(args);
            return count > 0 ? $"{count} 条查询 · 通过 {provider}" : $"通过 {provider}";
        }
        if (name == "web_fetch")
            return "读取页面标题、正文和链接";
        return null;
    }

    private static int CountSearchQueries(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(args);
            return ReadSearchQueries(doc.RootElement).Count;
        }
        catch (JsonException) { return 0; }
    }

    private static IReadOnlyList<string> ReadSearchQueries(JsonElement root)
    {
        var queries = new List<string>();
        if (root.TryGetProperty("queries", out var queryArray) && queryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in queryArray.EnumerateArray())
            {
                var query = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : ReadString(item, "query");
                if (!string.IsNullOrWhiteSpace(query)) queries.Add(query!);
            }
        }
        if (queries.Count == 0 && ReadString(root, "query") is { Length: > 0 } legacy)
            queries.Add(legacy);
        return queries;
    }

    private static string PrettyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string BuildResultPreview(string json)
    {
        var preview = PrettyJson(json);
        return preview.Length <= 1600 ? preview : preview[..1600] + "\n...";
    }

    private static string SearchProviderLabel(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
            ? "DuckDuckGo"
            : provider.Trim().ToLowerInvariant() switch
            {
                "tavily" => "Tavily",
                "exa" => "Exa",
                _ => "DuckDuckGo"
            };

    private static string LocalToolPendingLabel(string toolName) => toolName switch
    {
        "search_web" => "联网搜索",
        "web_fetch" => "网页阅读",
        _ => "调用工具"
    };

    private sealed class PendingOpenAiToolCall
    {
        public string Id { get; set; } = "call_" + Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var n) ? n : null;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ExtractContentText(JsonElement delta)
    {
        if (!delta.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return content.ToString();

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                parts.Add(item.GetString() ?? string.Empty);
            else if (item.ValueKind != JsonValueKind.Null && item.ValueKind != JsonValueKind.Undefined)
                parts.Add(item.ToString());
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static bool TryParseSources(JsonElement root, out IReadOnlyList<SourceReference> sources)
    {
        sources = Array.Empty<SourceReference>();
        if (!root.TryGetProperty("molagpt_sources", out var node) || node.ValueKind != JsonValueKind.Array)
            return false;

        var list = new List<SourceReference>();
        var fallbackId = 1;
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = item.TryGetProperty("id", out var idNode)
                     && idNode.ValueKind == JsonValueKind.Number
                     && idNode.TryGetInt32(out var parsedId)
                ? parsedId
                : fallbackId;
            var title = item.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String
                ? titleNode.GetString() ?? string.Empty
                : string.Empty;
            var url = item.TryGetProperty("url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String
                ? urlNode.GetString() ?? string.Empty
                : string.Empty;
            list.Add(new SourceReference(id, title, url));
            fallbackId++;
        }

        sources = list;
        return list.Count > 0;
    }
}
