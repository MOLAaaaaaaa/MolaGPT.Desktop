using System.Net.Http.Json;
using System.Text.Json;
using MolaGPT.Core.Chat.Attachments;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat;

/// <summary>Wire shapes a one-shot completion can speak. Deliberately the same
/// four the Pi sidecar can be pointed at, so a provider that works for a real
/// turn also works for a title or a vision call.</summary>
public enum OneShotWireApi
{
    OpenAiCompletions,
    AnthropicMessages,
    OpenAiResponses,
    GoogleGenerativeAi,
}

/// <summary>
/// Everything needed to reach one endpoint once. Produced by the provider that
/// owns the credential (<see cref="IOneShotTarget"/>) so no caller ever has to
/// know how a given provider authenticates.
/// </summary>
/// <param name="TokenProvider">Resolved per call rather than captured, because the
/// MolaGPT account JWT rotates.</param>
/// <param name="DropBodyKeys">Fields the endpoint rejects.</param>
/// <param name="StreamOnly">The endpoint answers streamed requests only (Sign in
/// with ChatGPT), so the answer is read from the event stream.</param>
public sealed record OneShotTarget(
    string Endpoint,
    Func<CancellationToken, Task<string?>> TokenProvider,
    string DisplayName,
    OneShotWireApi Api = OneShotWireApi.OpenAiCompletions,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
    IReadOnlyDictionary<string, JsonElement>? ExtraBody = null,
    IReadOnlyList<string>? DropBodyKeys = null,
    bool StreamOnly = false);

/// <summary>
/// A provider that can describe how to reach its upstream endpoint for a single
/// tool-less completion.
///
/// This exists because some of the app's LLM calls are not conversations: naming
/// a conversation and asking a vision model about one picture are one-shot, and
/// running them through the agent runtime costs a sidecar spawn each
/// (~2.7s / ~95MB measured). The vision case is worse than wasteful — it is
/// dispatched from inside a running turn's tool callback, so pointing it at the
/// provider already driving that turn deadlocks on the runtime's turn gate.
/// </summary>
public interface IOneShotTarget
{
    /// <summary>Null when this provider cannot serve <paramref name="modelId"/>
    /// as a one-shot call.</summary>
    OneShotTarget? DescribeOneShot(string modelId);
}

/// <summary>
/// One HTTP request, one answer, no tools and no state.
///
/// Not a fallback path for the agent: there is no tool loop here and nothing to
/// resume, so it cannot stand in for the Pi runtime even by accident. It exists
/// only for the calls that were never conversations in the first place.
/// </summary>
public sealed class OneShotCompletionClient
{
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>Anthropic requires max_tokens. Nothing that reaches this client
    /// wants a long answer, so the default is small rather than generous.</summary>
    private const int DefaultAnthropicMaxTokens = 2048;

    /// <summary>The cheapest <c>reasoning.effort</c> that every Responses-capable
    /// reasoning model accepts. Known exception: <c>gpt-5-pro</c> takes only
    /// <c>high</c> — it would reject any level we could name, so there is no value
    /// that is universally safe and this is the one that fails least often.</summary>
    private const string ResponsesMinimumEffort = "low";

    /// <summary>
    /// System.Text.Json escapes every non-ASCII character by default, which turns
    /// a Chinese prompt into <c>\uXXXX</c> escapes and roughly doubles the bytes.
    /// Both callers here are Chinese-first — a title prompt and a vision
    /// instruction — so this is the majority case, not an edge one.
    /// </summary>
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;

    public OneShotCompletionClient(HttpClient http) => _http = http;

    /// <param name="useThinking">Usually <see langword="false"/> here. These calls
    /// are internal errands, not answers the user reads, so paying for reasoning
    /// tokens on them is waste — and a reasoning preamble is one more thing the
    /// caller has to strip back out of a conversation title.</param>
    /// <param name="thinkingKind">Which dialect the "off" switch has to be spoken
    /// in. Providers disagree, so the flag alone is not enough.</param>
    public async Task<string> CompleteAsync(
        OneShotTarget target,
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        double? temperature = null,
        int? maxTokens = null,
        bool? useThinking = null,
        ThinkingParamKind? thinkingKind = null,


        CancellationToken ct = default,

        string? reasoningEffort = null,
        ThinkingConfig? thinkingConfig = null)
    {
        using var resp = await SendRequestAsync(target, modelId, messages, stream: false,
            temperature, maxTokens, useThinking, thinkingKind, reasoningEffort, thinkingConfig, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, target.DisplayName, ct).ConfigureAwait(false);

        if (target.StreamOnly)
            return await ReadResponsesStreamAsync(resp, target.DisplayName, onDelta: null, ct).ConfigureAwait(false);

        return await ReadJsonAnswerAsync(resp, target.Api, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Same call as <see cref="CompleteAsync"/>, but asks the endpoint for a
    /// streamed answer and reports each text piece through <paramref name="onDelta"/>
    /// as it arrives. The callback fires on a thread-pool thread — callers showing
    /// deltas in UI must marshal them. Returns the assembled answer, identical to
    /// what the non-streamed call would have produced.
    ///
    /// An endpoint that silently ignores <c>stream</c> (some gateways do) answers
    /// with an ordinary JSON document; that is detected by content type and the
    /// whole text is delivered as a single delta, so the caller never stalls
    /// waiting for events that do not exist.
    /// </summary>
    public async Task<string> CompleteStreamAsync(
        OneShotTarget target,
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onDelta = null,
        double? temperature = null,
        int? maxTokens = null,
        bool? useThinking = null,
        ThinkingParamKind? thinkingKind = null,


        CancellationToken ct = default,

        string? reasoningEffort = null,
        ThinkingConfig? thinkingConfig = null)
    {
        using var resp = await SendRequestAsync(target, modelId, messages, stream: true,
            temperature, maxTokens, useThinking, thinkingKind, reasoningEffort, thinkingConfig, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, target.DisplayName, ct).ConfigureAwait(false);

        if (!IsEventStream(resp))
        {
            var text = await ReadJsonAnswerAsync(resp, target.Api, ct, validateCompletion: true).ConfigureAwait(false);
            if (text.Length > 0) onDelta?.Invoke(text);
            return text;
        }

        return target.Api switch
        {
            OneShotWireApi.OpenAiResponses => await ReadResponsesStreamAsync(resp, target.DisplayName, onDelta, ct).ConfigureAwait(false),
            _ => await ReadEventStreamAsync(resp, target.Api, target.DisplayName, onDelta, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Build the body and send the request. <paramref name="stream"/> asks
    /// for the wire's streaming dialect: a flag for the OpenAI/Anthropic shapes,
    /// the <c>:streamGenerateContent?alt=sse</c> method swap for Google.</summary>
    private async Task<HttpResponseMessage> SendRequestAsync(
        OneShotTarget target,
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        bool stream,
        double? temperature,
        int? maxTokens,
        bool? useThinking,
        ThinkingParamKind? thinkingKind,
        string? reasoningEffort,
        ThinkingConfig? thinkingConfig,
        CancellationToken ct)
    {
        var body = target.Api switch
        {
            OneShotWireApi.AnthropicMessages => BuildAnthropicBody(modelId, messages, temperature, maxTokens),
            OneShotWireApi.OpenAiResponses => BuildResponsesBody(modelId, messages, temperature, maxTokens),
            OneShotWireApi.GoogleGenerativeAi => BuildGoogleBody(messages, temperature, maxTokens),
            _ => BuildCompletionsBody(modelId, messages, temperature, maxTokens),
        };
        ApplyThinking(body, target.Api, modelId, useThinking, thinkingKind, reasoningEffort, thinkingConfig, target.Endpoint);
        CustomRequestParams.ApplyBody(body, target.ExtraBody);
        foreach (var key in target.DropBodyKeys ?? []) body.Remove(key);
        if (stream || target.StreamOnly)
        {
            // Google's dialect lives in the URL, not the body.
            if (target.Api != OneShotWireApi.GoogleGenerativeAi) body["stream"] = true;
            if (target.Api == OneShotWireApi.OpenAiResponses || target.StreamOnly) body["store"] = false;
        }
        var nativeGoogle = target.Api == OneShotWireApi.GoogleGenerativeAi;
        if (GeminiRequestParams.Applies(modelId, nativeGoogle, target.Endpoint))
            GeminiRequestParams.Apply(body, modelId, nativeGoogle, effort: useThinking == true ? reasoningEffort : null, useThinking: useThinking);

        var endpoint = target.Endpoint;
        if (stream && target.Api == OneShotWireApi.GoogleGenerativeAi)
        {
            if (!endpoint.Contains(":streamGenerateContent", StringComparison.Ordinal))
                endpoint = endpoint.Replace(":generateContent", ":streamGenerateContent", StringComparison.Ordinal);
            endpoint += endpoint.Contains('?', StringComparison.Ordinal) ? "&alt=sse" : "?alt=sse";
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: RelaxedJson)
        };

        var token = await target.TokenProvider(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            if (target.Api == OneShotWireApi.AnthropicMessages)
            {
                req.Headers.TryAddWithoutValidation("x-api-key", token);
                req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            }
            else if (target.Api == OneShotWireApi.GoogleGenerativeAi)
            {
                req.Headers.TryAddWithoutValidation("x-goog-api-key", token);
            }
            else
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }
        }
        OpenRouterAttribution.Apply(req, endpoint, target.Headers);
        CustomRequestParams.ApplyHeaders(req, target.Headers);

        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    private static bool IsEventStream(HttpResponseMessage resp) =>
        resp.Content.Headers.ContentType?.MediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string> ReadJsonAnswerAsync(
        HttpResponseMessage resp, OneShotWireApi api, CancellationToken ct, bool validateCompletion = false)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (validateCompletion)
        {
            var root = doc.RootElement;
            if (api == OneShotWireApi.OpenAiResponses && root.TryGetProperty("status", out var status)
                && status.GetString() != "completed")
                throw new InvalidOperationException("回答未完整生成，请重试或提高输出上限。");
            if (api == OneShotWireApi.AnthropicMessages && root.TryGetProperty("stop_reason", out var stop))
                ValidateStopReason(stop.GetString(), "模型");
            var key = api == OneShotWireApi.GoogleGenerativeAi ? "candidates" : "choices";
            var reasonKey = api == OneShotWireApi.GoogleGenerativeAi ? "finishReason" : "finish_reason";
            if (root.TryGetProperty(key, out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                    if (choice.TryGetProperty(reasonKey, out var reason) && reason.ValueKind == JsonValueKind.String)
                        ValidateStopReason(reason.GetString(), "模型");
        }
        return api switch
        {
            OneShotWireApi.AnthropicMessages => ReadAnthropicText(doc.RootElement),
            OneShotWireApi.OpenAiResponses => ReadResponsesText(doc.RootElement),
            OneShotWireApi.GoogleGenerativeAi => ReadGoogleText(doc.RootElement),
            _ => ReadCompletionsText(doc.RootElement),
        };
    }

    /// <summary>
    /// Express the thinking setting in the target's dialect. Reuses the shared
    /// <see cref="ThinkingParams"/> table for the OpenAI-family shapes rather than
    /// re-deriving it, because a second copy drifts silently — the symptom is a
    /// model that keeps reasoning on an errand nobody wanted it to think about.
    /// Anthropic is spelled out here since it carries the switch in the body
    /// itself rather than as a top-level parameter.
    /// </summary>
    private static void ApplyThinking(
        IDictionary<string, object?> body,
        OneShotWireApi api,
        string modelId,
        bool? useThinking,
        ThinkingParamKind? thinkingKind,
        string? reasoningEffort,
        ThinkingConfig? thinkingConfig,
        string? endpoint = null)
    {
        if (useThinking is null) return;

        if (api == OneShotWireApi.AnthropicMessages)
        {
            if (useThinking == true)
            {
                if (thinkingKind == ThinkingParamKind.AnthropicBudget)
                {
                    body["thinking"] = new { type = "enabled", budget_tokens = thinkingConfig?.DefaultBudget ?? 8192 };
                    body.Remove("temperature");
                }
                else
                {
                    body["thinking"] = new { type = "adaptive" };
                    if (!string.IsNullOrWhiteSpace(reasoningEffort))
                        body["output_config"] = new { effort = reasoningEffort };
                }
            }
            else if (thinkingConfig?.Mandatory != true)
                body["thinking"] = new { type = "disabled" };


            return;
        }

        if (api == OneShotWireApi.OpenAiResponses)
        {
            // "low", not "none". Both mean "spend as little as possible", but the
            // vocabulary is model-dependent and "none" only arrived with GPT-5.1 —
            // every earlier reasoning model (o-series, the original GPT-5) accepts
            // only low/medium/high and answers 400 to anything else. A rejected
            // errand is worse than a slightly more expensive one, because the
            // callers here treat a failure as "leave the title alone".
            if (useThinking == true && !string.IsNullOrWhiteSpace(reasoningEffort))
                body["reasoning"] = new { effort = reasoningEffort };
            else if (useThinking == false && thinkingConfig?.Mandatory != true)
                body["reasoning"] = new { effort = ResponsesMinimumEffort };
            return;
        }

        if (api == OneShotWireApi.GoogleGenerativeAi)
        {
            var generation = body.TryGetValue("generationConfig", out var value)
                             && value is Dictionary<string, object?> existing
                ? existing
                : new Dictionary<string, object?>();
            if (useThinking == true && thinkingKind == ThinkingParamKind.GeminiThinkingLevel && !string.IsNullOrWhiteSpace(reasoningEffort))
                generation["thinkingConfig"] = new { thinkingLevel = reasoningEffort.ToUpperInvariant() };
            else if (useThinking == true && thinkingKind == ThinkingParamKind.GeminiBudget)
                generation["thinkingConfig"] = new { thinkingBudget = thinkingConfig?.DefaultBudget ?? 8192 };
            else if (useThinking == false && thinkingConfig?.Mandatory != true && thinkingKind == ThinkingParamKind.GeminiThinkingLevel)
                generation["thinkingConfig"] = new { thinkingLevel = "LOW" };
            else if (useThinking == false && thinkingKind == ThinkingParamKind.GeminiBudget)
                generation["thinkingConfig"] = new { thinkingBudget = 0 };
            if (generation.Count > 0) body["generationConfig"] = generation;
            return;
        }

        ThinkingParams.Apply(
            body,
            new ChatRequest(
                modelId,
                Array.Empty<ChatMessage>(),
                UseThinking: useThinking,
                ThinkingParamKind: thinkingKind,
                ReasoningEffort: reasoningEffort,
                ThinkingBudgetTokens: thinkingConfig?.DefaultBudget),
            endpoint,
            thinkingConfig);

    }

    // ── request bodies ──────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildCompletionsBody(
        string modelId, IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens)
    {
        var wire = new List<object>(messages.Count);
        foreach (var m in messages)
        {
            var ordinal = 0;
            wire.Add(new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = OpenAiMessageContentBuilder.Build(
                    m, replaceImagesWithText: false, ref ordinal, AttachmentPromptOptions.Default)
            });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = wire,
            ["stream"] = false,
        };
        if (temperature is not null) body["temperature"] = temperature;
        if (maxTokens is not null) body["max_tokens"] = maxTokens;
        return body;
    }

    private static Dictionary<string, object?> BuildAnthropicBody(
        string modelId, IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens)
    {
        string? system = null;
        var convo = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == ChatMessage.RoleSystem)
            {
                system = system is null ? m.AsText() : system + "\n\n" + m.AsText();
                continue;
            }
            convo.Add(new { role = m.Role, content = AnthropicMessageContentBuilder.Build(m) });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = convo,
            ["stream"] = false,
            ["max_tokens"] = maxTokens ?? DefaultAnthropicMaxTokens,
        };
        if (system is not null) body["system"] = system;
        if (temperature is not null) body["temperature"] = temperature;
        return body;
    }

    private static Dictionary<string, object?> BuildResponsesBody(
        string modelId, IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens)
    {
        string? instructions = null;
        var input = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == ChatMessage.RoleSystem)
            {
                instructions = instructions is null ? m.AsText() : instructions + "\n\n" + m.AsText();
                continue;
            }

            var parts = new List<object>();
            var text = m.AsText();
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(new { type = "input_text", text });

            foreach (var attachment in m.Attachments ?? [])
            {
                if (attachment.Kind != AttachmentKind.Image || attachment.IsUnavailable) continue;
                var url = !string.IsNullOrWhiteSpace(attachment.RemoteUrl)
                    ? attachment.RemoteUrl!
                    : $"data:{attachment.MimeType};base64,{Convert.ToBase64String(attachment.Bytes)}";
                parts.Add(new { type = "input_image", image_url = url });
            }

            input.Add(new { role = m.Role, content = parts });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["input"] = input,
            ["stream"] = false,
        };
        if (instructions is not null) body["instructions"] = instructions;
        if (temperature is not null) body["temperature"] = temperature;
        if (maxTokens is not null) body["max_output_tokens"] = maxTokens;
        return body;
    }

    private static Dictionary<string, object?> BuildGoogleBody(
        IReadOnlyList<ChatMessage> messages, double? temperature, int? maxTokens)
    {
        string? system = null;
        var contents = new List<object>();
        foreach (var message in messages)
        {
            if (message.Role == ChatMessage.RoleSystem)
            {
                system = system is null ? message.AsText() : system + "\n\n" + message.AsText();
                continue;
            }

            var parts = new List<object>();
            var text = message.AsText();
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(new { text });
            foreach (var attachment in message.Attachments ?? [])
            {
                if (attachment.Kind != AttachmentKind.Image || attachment.IsUnavailable) continue;
                parts.Add(new
                {
                    inlineData = new
                    {
                        mimeType = attachment.MimeType,
                        data = Convert.ToBase64String(attachment.Bytes),
                    }
                });
            }

            if (parts.Count > 0)
                contents.Add(new { role = message.Role == ChatMessage.RoleAssistant ? "model" : "user", parts });
        }

        var body = new Dictionary<string, object?> { ["contents"] = contents };
        if (system is not null) body["systemInstruction"] = new { parts = new[] { new { text = system } } };
        var generation = new Dictionary<string, object?>();
        if (temperature is not null) generation["temperature"] = temperature;
        if (maxTokens is not null) generation["maxOutputTokens"] = maxTokens;
        if (generation.Count > 0) body["generationConfig"] = generation;
        return body;
    }

    // ── response readers ────────────────────────────────────────────────────

    private static string ReadCompletionsText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message)) continue;
            if (!message.TryGetProperty("content", out var content)) continue;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString()?.Trim() ?? string.Empty;

            // Some gateways answer with the multimodal part array even for text.
            if (content.ValueKind == JsonValueKind.Array)
                return ConcatTextParts(content, "text", "text").Trim();
        }
        return string.Empty;
    }

    private static string ReadAnthropicText(JsonElement root) =>
        root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? ConcatTextParts(content, "text", "text").Trim()
            : string.Empty;

    /// <summary>
    /// The answer from a Responses event stream. A stream that ends without
    /// <c>response.completed</c> did not finish, whatever text it had produced.
    ///
    /// The text is collected from the deltas as well, because the final response
    /// object is not always complete: the Codex backend sends it with an empty
    /// <c>output</c>, having streamed every item already.
    /// </summary>
    private static async Task<string> ReadResponsesStreamAsync(
        HttpResponseMessage response, string displayName, Action<string>? onDelta, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var streamed = new System.Text.StringBuilder();
        await foreach (var ev in Sse.SseStreamReader.ReadAsync(stream, ct))
        {
            if (ev.IsDone || string.IsNullOrEmpty(ev.Data)) continue;
            var doc = JsonDocument.Parse(ev.Data);
            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "response.output_text.delta"
                    && root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                {
                    var piece = delta.GetString()!;
                    streamed.Append(piece);
                    onDelta?.Invoke(piece);
                }
                if (type == "response.completed" && root.TryGetProperty("response", out var completed))
                {
                    var text = ReadResponsesText(completed);
                    return text.Length > 0 ? text : streamed.ToString().Trim();
                }
                if (type == "response.incomplete")
                    throw new InvalidOperationException($"{displayName}的回答未完整生成，请重试或提高输出上限。");
                if (type is "response.failed" or "error")
                {
                    var error = root.TryGetProperty("response", out var failed) && failed.TryGetProperty("error", out var e) ? e
                        : root.TryGetProperty("error", out var direct) ? direct : root;
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    throw new InvalidOperationException($"{displayName}失败：{message ?? ev.Data}");
                }
            }
        }
        throw new InvalidOperationException($"{displayName}的回答没有结束就中断了。");
    }

    /// <summary>
    /// The streamed answer for the dialects whose events carry only increments:
    /// each <c>data:</c> frame contributes its text to the running total. The
    /// Responses dialect has its own reader (<see cref="ReadResponsesStreamAsync"/>)
    /// because the terminal frame, not the tail of the stream, is authoritative.
    /// </summary>
    private static async Task<string> ReadEventStreamAsync(
        HttpResponseMessage response, OneShotWireApi api, string displayName,
        Action<string>? onDelta, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var streamed = new System.Text.StringBuilder();
        await foreach (var ev in Sse.SseStreamReader.ReadAsync(stream, ct))
        {
            if (ev.IsDone && api == OneShotWireApi.OpenAiCompletions)
                return streamed.ToString();
            if (ev.IsDone || string.IsNullOrEmpty(ev.Data)) continue;
            var doc = JsonDocument.Parse(ev.Data);
            using (doc)
            {
                var root = doc.RootElement;
                if (ChatApiErrorHelper.TryExtractStreamingError(root, out var streamError))
                    throw new InvalidOperationException($"{displayName}失败：{streamError}");
                var piece = api switch
                {
                    // A stream event is a whole GenerateContentResponse in
                    // miniature — the non-streamed reader walks the same shape.
                    OneShotWireApi.GoogleGenerativeAi => ReadGoogleText(root, trim: false),
                    OneShotWireApi.AnthropicMessages => ExtractAnthropicDelta(root),
                    _ => ExtractCompletionsDelta(root),
                };
                if (!string.IsNullOrEmpty(piece))
                {
                    streamed.Append(piece);
                    onDelta?.Invoke(piece);
                }

                if (api == OneShotWireApi.AnthropicMessages)
                {
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "message_delta" && root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("stop_reason", out var stopReason)
                        && stopReason.GetString() is { } reason && reason is not ("end_turn" or "stop_sequence"))
                        ValidateStopReason(reason, displayName);
                    if (type == "message_stop") return streamed.ToString();
                }
                else
                {
                    var key = api == OneShotWireApi.GoogleGenerativeAi ? "candidates" : "choices";
                    var finishKey = api == OneShotWireApi.GoogleGenerativeAi ? "finishReason" : "finish_reason";
                    if (root.TryGetProperty(key, out var choices) && choices.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var choice in choices.EnumerateArray())
                        {
                            if (!choice.TryGetProperty(finishKey, out var finish) || finish.ValueKind != JsonValueKind.String) continue;
                            var reason = finish.GetString();
                            if (string.IsNullOrEmpty(reason) || reason == "FINISH_REASON_UNSPECIFIED") continue;
                            ValidateStopReason(reason, displayName);
                            return streamed.ToString();
                        }
                    }
                }
            }
        }
        throw new InvalidOperationException($"{displayName}的回答没有结束就中断了。");
    }

    private static void ValidateStopReason(string? reason, string displayName)
    {
        if (reason is null or "stop" or "STOP" or "end_turn" or "stop_sequence") return;
        throw new InvalidOperationException($"{displayName}的回答未完整生成（{reason}）。");
    }

    private static string? ExtractCompletionsDelta(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;
        var sb = new System.Text.StringBuilder();
        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
            if (!delta.TryGetProperty("content", out var content)) continue;
            if (content.ValueKind == JsonValueKind.String) sb.Append(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array) sb.Append(ConcatTextParts(content, "text", "text"));
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string? ExtractAnthropicDelta(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "content_block_delta") return null;
        if (!root.TryGetProperty("delta", out var delta)) return null;
        if (!delta.TryGetProperty("type", out var dt) || dt.GetString() != "text_delta") return null;
        return delta.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
    }

    private static string ReadResponsesText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            var text = ConcatTextParts(content, "output_text", "text");
            if (text.Length > 0) parts.Add(text);
        }
        return string.Concat(parts).Trim();
    }

    private static string ReadGoogleText(JsonElement root, bool trim = true)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var text = new System.Text.StringBuilder();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                    continue;
                if (part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    text.Append(value.GetString());
            }
        }
        return trim ? text.ToString().Trim() : text.ToString();
    }

    /// <summary>Concatenate the <paramref name="textField"/> of every part whose
    /// <c>type</c> is <paramref name="wantedType"/>. Skips reasoning/thinking
    /// blocks, which every one of these shapes carries as a different type.</summary>
    private static string ConcatTextParts(JsonElement parts, string wantedType, string textField)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (!part.TryGetProperty("type", out var type) || type.GetString() != wantedType) continue;
            if (part.TryGetProperty(textField, out var text) && text.ValueKind == JsonValueKind.String)
                sb.Append(text.GetString());
        }
        return sb.ToString();
    }
}
