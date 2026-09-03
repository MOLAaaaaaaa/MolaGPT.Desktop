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
public sealed record OneShotTarget(
    string Endpoint,
    Func<CancellationToken, Task<string?>> TokenProvider,
    string DisplayName,
    OneShotWireApi Api = OneShotWireApi.OpenAiCompletions,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null,
    IReadOnlyDictionary<string, JsonElement>? ExtraBody = null);

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
        CancellationToken ct = default)
    {
        var body = target.Api switch
        {
            OneShotWireApi.AnthropicMessages => BuildAnthropicBody(modelId, messages, temperature, maxTokens),
            OneShotWireApi.OpenAiResponses => BuildResponsesBody(modelId, messages, temperature, maxTokens),
            OneShotWireApi.GoogleGenerativeAi => BuildGoogleBody(messages, temperature, maxTokens),
            _ => BuildCompletionsBody(modelId, messages, temperature, maxTokens),
        };
        ApplyThinking(body, target.Api, modelId, useThinking, thinkingKind);
        CustomRequestParams.ApplyBody(body, target.ExtraBody);

        using var req = new HttpRequestMessage(HttpMethod.Post, target.Endpoint)
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
        OpenRouterAttribution.Apply(req, target.Endpoint, target.Headers);
        CustomRequestParams.ApplyHeaders(req, target.Headers);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, target.DisplayName, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        return target.Api switch
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
        ThinkingParamKind? thinkingKind)
    {
        if (useThinking is null) return;

        if (api == OneShotWireApi.AnthropicMessages)
        {
            body["thinking"] = useThinking == true
                ? new { type = "adaptive" }
                : (object)new { type = "disabled" };
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
            if (useThinking == false)
                body["reasoning"] = new { effort = ResponsesMinimumEffort };
            return;
        }

        if (api == OneShotWireApi.GoogleGenerativeAi)
        {
            var generation = body.TryGetValue("generationConfig", out var value)
                             && value is Dictionary<string, object?> existing
                ? existing
                : new Dictionary<string, object?>();
            if (useThinking == false && thinkingKind == ThinkingParamKind.GeminiThinkingLevel)
                generation["thinkingConfig"] = new { thinkingLevel = "LOW" };
            else if (useThinking == false && thinkingKind == ThinkingParamKind.GeminiBudget)
                generation["thinkingConfig"] = new { thinkingBudget = 0 };
            if (generation.Count > 0) body["generationConfig"] = generation;
            return;
        }

        ThinkingParams.Apply(body, new ChatRequest(
            modelId,
            Array.Empty<ChatMessage>(),
            UseThinking: useThinking,
            ThinkingParamKind: thinkingKind));
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

    private static string ReadGoogleText(JsonElement root)
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
        return text.ToString().Trim();
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
