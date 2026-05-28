using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MolaGPT.Core.Models;
using MolaGPT.Core.Net;
using MolaGPT.Core.Sse;

namespace MolaGPT.Core.Chat.Providers;

/// <summary>
/// Anthropic Claude provider. Talks to <c>https://api.anthropic.com/v1/messages</c>.
/// Differences from OpenAI:
///   - "system" goes to a top-level field (not in <c>messages</c>)
///   - SSE uses <c>event: &lt;type&gt;</c> + <c>data: &lt;json&gt;</c> double lines
///   - Auth header is <c>x-api-key</c>, plus <c>anthropic-version: 2023-06-01</c>
///   - Streaming events: message_start / content_block_start / content_block_delta /
///     content_block_stop / message_delta / message_stop; thinking blocks separate.
/// </summary>
public sealed class AnthropicProvider : IChatProvider
{
    public string Id { get; }
    public string DisplayName { get; }
    public ProviderKind Kind => ProviderKind.Anthropic;
    public IReadOnlyList<ProviderModel> Models { get; private set; }

    public const string DefaultBaseUrl = "https://api.anthropic.com/";
    public const string AnthropicVersion = "2023-06-01";

    public string BaseUrl { get; }
    public string ApiKey { get; }
    private readonly HttpClient _http;

    public AnthropicProvider(string id, string displayName, string apiKey,
        IReadOnlyList<ProviderModel> models, HttpClient http, string? baseUrl = null)
    {
        Id = id;
        DisplayName = displayName;
        ApiKey = apiKey;
        Models = models;
        BaseUrl = NetworkSecurity.RequireHttpsBaseUrl(baseUrl ?? DefaultBaseUrl, $"{displayName} Base URL");
        _http = http;
    }

    public void UpdateModels(IReadOnlyList<ProviderModel> models) => Models = models;

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Split out system from messages
        string? systemPrompt = null;
        var convo = new List<object>();
        foreach (var m in request.Messages)
        {
            if (m.Role == ChatMessage.RoleSystem)
            {
                if (systemPrompt is null) systemPrompt = m.AsText();
                else systemPrompt += "\n\n" + m.AsText();
            }
            else
            {
                convo.Add(new { role = m.Role, content = BuildAnthropicContent(m) });
            }
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = convo,
            ["stream"] = true,
            ["max_tokens"] = request.MaxTokens ?? 8192,
        };
        if (systemPrompt is not null) body["system"] = systemPrompt;
        if (request.Temperature is not null) body["temperature"] = request.Temperature;
        if (request.UseThinking == true)
        {
            if (request.ThinkingParamKind == ThinkingParamKind.AnthropicBudget)
            {
                body["thinking"] = new { type = "enabled", budget_tokens = request.ThinkingBudgetTokens ?? 10000 };
            }
            else
            {
                var thinkingObj = new Dictionary<string, object> { ["type"] = "adaptive" };
                if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
                    thinkingObj["effort"] = request.ReasoningEffort;
                body["thinking"] = thinkingObj;
            }
        }
        else if (request.UseThinking == false)
        {
            body["thinking"] = new { type = "disabled" };
        }
        if (request.ExtraBody is not null)
            foreach (var kv in request.ExtraBody) body[kv.Key] = kv.Value;

        var url = new Uri(new Uri(BaseUrl), "v1/messages");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await ChatApiErrorHelper.EnsureSuccessAsync(resp, DisplayName, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var ev in SseStreamReader.ReadAsync(stream, ct))
        {
            if (string.IsNullOrEmpty(ev.Data)) continue;
            ChatChunk? chunk = null;
            try
            {
                using var doc = JsonDocument.Parse(ev.Data);
                var root = doc.RootElement;
                if (ChatApiErrorHelper.TryExtractStreamingError(root, out var streamError))
                    throw new InvalidOperationException(streamError);
                var type = ev.EventName ?? (root.TryGetProperty("type", out var t) ? t.GetString() : null);
                switch (type)
                {
                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta))
                        {
                            var dtype = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                            if (dtype == "text_delta" && delta.TryGetProperty("text", out var txt))
                                chunk = new ChatChunk(DeltaText: txt.GetString(), RawJson: ev.Data);
                            else if (dtype == "thinking_delta" && delta.TryGetProperty("thinking", out var th))
                                chunk = new ChatChunk(DeltaThinking: th.GetString(), RawJson: ev.Data);
                        }
                        break;
                    case "message_delta":
                        if (root.TryGetProperty("delta", out var md) &&
                            md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                            chunk = new ChatChunk(FinishReason: sr.GetString(), RawJson: ev.Data);
                        break;
                    case "message_stop":
                        yield break;
                }
            }
            catch (JsonException)
            {
                chunk = new ChatChunk(RawJson: ev.Data);
            }
            if (chunk is not null) yield return chunk;
        }
    }

    private static object BuildAnthropicContent(ChatMessage message)
    {
        if (message.Attachments is null || message.Attachments.Count == 0)
            return message.Content;

        var parts = new List<object>();
        var text = message.AsText();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(new { type = "text", text });

        foreach (var attachment in message.Attachments)
        {
            if (attachment.Kind == AttachmentKind.Image)
            {
                parts.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = attachment.MimeType,
                        data = Convert.ToBase64String(attachment.Bytes)
                    }
                });
                continue;
            }

            parts.Add(new
            {
                type = "text",
                text = OpenAiMessageContentBuilder.BuildFileTextPart(attachment)
            });
        }

        return parts;
    }
}
