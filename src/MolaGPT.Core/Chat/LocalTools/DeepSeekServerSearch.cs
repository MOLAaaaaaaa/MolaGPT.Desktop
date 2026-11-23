using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.LocalTools;

public enum BuiltinSearchVendor
{
    DeepSeek,
    OpenAi,
    Bailian,
    Anthropic,
    Gemini
}

public static class BuiltinWebSearch
{
    public const string ProviderPrefix = "builtin:";
    public const string DeepSeekToolName = "deepseek_web_search";
    public const string DeepSeekReadUrlToolName = "deepseek_read_url";
    public const string AnthropicToolType = "web_search_20250305";
    public const string AnthropicFetchToolType = "web_fetch_20250910";

    public static string ProviderId(BuiltinSearchVendor vendor) =>
        ProviderPrefix + vendor.ToString().ToLowerInvariant();

    public static bool TryParseProviderId(string? provider, out BuiltinSearchVendor vendor)
    {
        vendor = default;
        if (string.IsNullOrWhiteSpace(provider)) return false;

        var trimmed = provider.Trim();
        if (string.Equals(trimmed, DeepSeekServerSearch.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            vendor = BuiltinSearchVendor.DeepSeek;
            return true;
        }

        if (!trimmed.StartsWith(ProviderPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return Enum.TryParse(trimmed[ProviderPrefix.Length..], ignoreCase: true, out vendor)
               && Enum.IsDefined(vendor);
    }

    public static bool IsBuiltinProviderId(string? provider) => TryParseProviderId(provider, out _);

    public static BuiltinSearchVendor? DetectVendor(string? endpointOrBaseUrl)
    {
        if (!Uri.TryCreate(endpointOrBaseUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "api.deepseek.com") return BuiltinSearchVendor.DeepSeek;
        if (host == "api.openai.com") return BuiltinSearchVendor.OpenAi;
        if (host == "api.anthropic.com") return BuiltinSearchVendor.Anthropic;
        if (host == "generativelanguage.googleapis.com") return BuiltinSearchVendor.Gemini;
        if (host is "dashscope.aliyuncs.com" or "dashscope-intl.aliyuncs.com" or "dashscope-us.aliyuncs.com"
            || host.EndsWith(".maas.aliyuncs.com", StringComparison.Ordinal))
        {
            return BuiltinSearchVendor.Bailian;
        }

        return null;
    }

    public static bool IsSupportedProviderType(BuiltinSearchVendor vendor, string? providerType)
    {
        var normalized = NormalizeProviderType(providerType);
        return vendor switch
        {
            BuiltinSearchVendor.DeepSeek => normalized is "openai-compat" or "openai-response" or "anthropic",
            BuiltinSearchVendor.OpenAi => normalized == "openai-response",
            BuiltinSearchVendor.Bailian => normalized is "openai-compat" or "openai-response",
            BuiltinSearchVendor.Anthropic => normalized == "anthropic",
            BuiltinSearchVendor.Gemini => normalized == "gemini",
            _ => false
        };
    }

    public static bool CanUseWireApi(BuiltinSearchVendor vendor, string? api)
    {
        var normalized = api?.Trim().ToLowerInvariant();
        return vendor switch
        {
            BuiltinSearchVendor.DeepSeek => normalized is "openai-completions" or "openai-responses" or "anthropic-messages",
            BuiltinSearchVendor.OpenAi => normalized == "openai-responses",
            BuiltinSearchVendor.Bailian => normalized is "openai-completions" or "openai-responses",
            BuiltinSearchVendor.Anthropic => normalized == "anthropic-messages",
            BuiltinSearchVendor.Gemini => normalized == "google-generative-ai",
            _ => false
        };
    }

    public static bool IsEnabledOnProvider(string? providerType, string? baseUrl, bool enabled) =>
        enabled
        && DetectVendor(baseUrl) is { } vendor
        && IsSupportedProviderType(vendor, providerType);

    public static bool ForcesThinking(BuiltinSearchVendor vendor) =>
        vendor == BuiltinSearchVendor.DeepSeek;

    public static string FullName(BuiltinSearchVendor vendor) => vendor switch
    {
        BuiltinSearchVendor.DeepSeek => "DeepSeek",
        BuiltinSearchVendor.OpenAi => "OpenAI",
        BuiltinSearchVendor.Bailian => "阿里云百炼",
        BuiltinSearchVendor.Anthropic => "Anthropic",
        BuiltinSearchVendor.Gemini => "Google Gemini",
        _ => vendor.ToString()
    };

    public static string Label(BuiltinSearchVendor vendor) => vendor switch
    {
        BuiltinSearchVendor.DeepSeek => "DeepSeek 内置搜索",
        BuiltinSearchVendor.OpenAi => "OpenAI 内置搜索",
        BuiltinSearchVendor.Bailian => "百炼内置搜索",
        BuiltinSearchVendor.Anthropic => "Anthropic 内置搜索",
        BuiltinSearchVendor.Gemini => "Gemini 内置搜索",
        _ => "内置搜索"
    };

    public static string DescribeToolHint(BuiltinSearchVendor vendor) => vendor switch
    {
        BuiltinSearchVendor.DeepSeek => "支持搜索与网页读取，仅部分模型可用。使用时开启深度思考，搜索不单独计费。",
        BuiltinSearchVendor.OpenAi => "支持搜索与网页浏览，仅部分模型可用。搜索按次计费，模型用量另计。",
        BuiltinSearchVendor.Bailian => "支持搜索，开启思考时支持网页读取，仅部分模型可用。搜索可能产生额外费用，模型用量另计。",
        BuiltinSearchVendor.Anthropic => "支持搜索与网页读取，仅部分模型可用。搜索按次计费，网页读取仅计模型用量。",
        BuiltinSearchVendor.Gemini => "支持搜索与网页读取，仅部分模型可用，使用时可能产生额外费用。",
        _ => string.Empty
    };

    public static string DescribeProtocolRequirement(BuiltinSearchVendor vendor) => vendor switch
    {
        BuiltinSearchVendor.DeepSeek => "需使用 OpenAI Chat Completions、OpenAI Responses 或 Anthropic 协议。",
        BuiltinSearchVendor.OpenAi => "需使用 OpenAI Responses 协议。",
        BuiltinSearchVendor.Bailian => "需使用 OpenAI Chat Completions 或 OpenAI Responses 协议。",
        BuiltinSearchVendor.Anthropic => "需使用 Anthropic 协议。",
        BuiltinSearchVendor.Gemini => "需使用 Gemini 原生协议。",
        _ => string.Empty
    };

    public static bool DefaultSupportsModel(BuiltinSearchVendor vendor, string? modelId, string? providerType = null)
    {
        var id = NormalizeModelId(modelId);
        if (id.Length == 0 || IsNonChatModel(id)) return false;

        return vendor switch
        {
            BuiltinSearchVendor.DeepSeek => id == "deepseek-flash",
            BuiltinSearchVendor.OpenAi => DefaultSupportsOpenAiModel(id),
            BuiltinSearchVendor.Bailian => DefaultSupportsBailianModel(id, providerType),
            BuiltinSearchVendor.Anthropic => DefaultSupportsAnthropicModel(id),
            BuiltinSearchVendor.Gemini => GeminiModelPolicy.UsesThinkingLevel(id)
                                          && !id.Contains("-image", StringComparison.Ordinal),
            _ => false
        };
    }

    public static string AdjustEndpoint(BuiltinSearchVendor vendor, string endpoint, string api)
    {
        if (vendor != BuiltinSearchVendor.DeepSeek
            || !string.Equals(api, "openai-responses", StringComparison.OrdinalIgnoreCase)
            || DetectVendor(endpoint) != BuiltinSearchVendor.DeepSeek
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return endpoint;
        }

        const string versioned = "/v1/responses";
        var path = uri.AbsolutePath;
        if (!path.EndsWith(versioned, StringComparison.Ordinal)) return endpoint;

        return new UriBuilder(uri) { Path = path[..^versioned.Length] + "/responses" }.Uri.ToString();
    }

    public static bool ReplacesLocalWebFetch(
        BuiltinSearchVendor vendor,
        string? api,
        string? modelId,
        bool useThinking)
    {
        if (!CanUseWireApi(vendor, api)) return false;
        return vendor switch
        {
            BuiltinSearchVendor.Anthropic => true,
            BuiltinSearchVendor.Gemini => true,
            BuiltinSearchVendor.Bailian => useThinking && !IsBailianOmniModel(modelId),
            _ => false
        };
    }

    public static Action<JsonObject> Wrap(
        BuiltinSearchVendor vendor,
        string api,
        string? modelId,
        Action<JsonObject>? inner,
        bool enableWebFetch = true,
        bool useThinking = false) => body =>
    {
        inner?.Invoke(body);
        Apply(body, vendor, api, modelId, enableWebFetch, useThinking);
    };

    public static void Apply(
        JsonObject body,
        BuiltinSearchVendor vendor,
        string api,
        string? modelId = null,
        bool enableWebFetch = true,
        bool useThinking = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        var normalizedApi = api.Trim().ToLowerInvariant();

        switch (vendor)
        {
            case BuiltinSearchVendor.DeepSeek:
                ApplyDeepSeek(body, normalizedApi, enableWebFetch);
                break;

            case BuiltinSearchVendor.OpenAi when normalizedApi == "openai-responses":
                AppendTypedTool(body, "web_search", "web_search", "web_search_preview");
                if (string.Equals(NormalizeModelId(modelId), "gpt-5", StringComparison.Ordinal)
                    && body["reasoning"] is JsonObject reasoning
                    && (string?)reasoning["effort"] is "minimal" or "none")
                {
                    reasoning["effort"] = "low";
                }
                break;

            case BuiltinSearchVendor.Bailian when normalizedApi == "openai-responses":
                AppendTypedTool(body, "web_search", "web_search");
                if (enableWebFetch && useThinking && !IsBailianOmniModel(modelId))
                {
                    body["enable_thinking"] = true;
                    AppendTypedTool(body, "web_extractor", "web_extractor");
                }
                break;

            case BuiltinSearchVendor.Bailian when normalizedApi == "openai-completions":
                body["enable_search"] = true;
                if (IsBailianOmniModel(modelId))
                {
                    EnsureSearchOptions(body)["search_strategy"] ??= "agent";
                }
                else if (enableWebFetch && useThinking)
                {
                    body["enable_thinking"] = true;
                    EnsureSearchOptions(body)["search_strategy"] = "agent_max";
                }
                break;

            case BuiltinSearchVendor.Anthropic when normalizedApi == "anthropic-messages":
                AppendAnthropicWebSearch(body);
                if (enableWebFetch) AppendAnthropicWebFetch(body);
                break;

            case BuiltinSearchVendor.Gemini when normalizedApi == "google-generative-ai":
                AppendGeminiGoogleSearch(body);
                if (enableWebFetch) AppendGeminiUrlContext(body);
                break;
        }
    }

    private static void ApplyDeepSeek(JsonObject body, string api, bool enableWebFetch)
    {
        switch (api)
        {
            case "openai-completions" or "chat":
                AppendTypedTool(body, DeepSeekToolName, DeepSeekToolName);
                if (enableWebFetch) AppendTypedTool(body, DeepSeekReadUrlToolName, DeepSeekReadUrlToolName);
                EnableDeepSeekThinking(body, thinkingDialect: true);
                body.Remove("web_search_options");
                body.Remove("logprobs");
                body.Remove("top_logprobs");
                NormalizeResponseFormat(body);
                FixDeepSeekToolChoice(body, "chat");
                break;

            case "openai-responses" or "responses":
                AppendTypedTool(body, DeepSeekToolName, DeepSeekToolName);
                if (enableWebFetch) AppendTypedTool(body, DeepSeekReadUrlToolName, DeepSeekReadUrlToolName);
                EnableDeepSeekThinking(body, thinkingDialect: false);
                body.Remove("top_logprobs");
                if (body["text"] is JsonObject text && text["format"] is JsonObject format
                    && !string.Equals((string?)format["type"], "text", StringComparison.OrdinalIgnoreCase))
                {
                    text.Remove("format");
                }
                if (body["tools"] is JsonArray responseTools)
                {
                    for (var i = responseTools.Count - 1; i >= 0; i--)
                    {
                        if (responseTools[i] is JsonObject entry
                            && (string?)entry["type"] is "web_search" or "custom" or "namespace" or "tool_search")
                        {
                            responseTools.RemoveAt(i);
                        }
                    }
                }
                FixDeepSeekToolChoice(body, "responses");
                break;

            case "anthropic-messages" or "messages":
                AppendNamedServerTool(body, DeepSeekToolName, DeepSeekToolName);
                if (enableWebFetch) AppendNamedServerTool(body, DeepSeekReadUrlToolName, DeepSeekReadUrlToolName);
                EnableDeepSeekThinking(body, thinkingDialect: true);
                FixDeepSeekToolChoice(body, "messages");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(api), api, "未知的端点协议。");
        }
    }

    private static void AppendTypedTool(JsonObject body, string type, params string[] matchTypes)
    {
        var tools = EnsureToolsArray(body);
        foreach (var existing in tools)
        {
            if (existing is JsonObject obj && (string?)obj["type"] is { } existingType
                && matchTypes.Any(candidate => string.Equals(existingType, candidate, StringComparison.Ordinal)))
            {
                return;
            }
        }

        tools.Add(new JsonObject { ["type"] = type });
    }

    private static void AppendNamedServerTool(JsonObject body, string type, string name)
    {
        var tools = EnsureToolsArray(body);
        foreach (var existing in tools)
        {
            if (existing is JsonObject obj
                && (string.Equals((string?)obj["type"], type, StringComparison.Ordinal)
                    || string.Equals((string?)obj["name"], name, StringComparison.Ordinal)))
            {
                return;
            }
        }

        tools.Add(new JsonObject { ["type"] = type, ["name"] = name });
    }

    private static void AppendAnthropicWebSearch(JsonObject body)
    {
        var tools = EnsureToolsArray(body);
        foreach (var existing in tools)
        {
            if (existing is JsonObject obj
                && (string.Equals((string?)obj["name"], "web_search", StringComparison.Ordinal)
                    || ((string?)obj["type"])?.StartsWith("web_search", StringComparison.Ordinal) == true))
            {
                return;
            }
        }

        tools.Add(new JsonObject
        {
            ["type"] = AnthropicToolType,
            ["name"] = "web_search"
        });
    }

    private static void AppendAnthropicWebFetch(JsonObject body)
    {
        var tools = EnsureToolsArray(body);
        for (var i = tools.Count - 1; i >= 0; i--)
        {
            if (tools[i] is not JsonObject obj) continue;
            if (((string?)obj["type"])?.StartsWith("web_fetch", StringComparison.Ordinal) == true)
                return;
            if (string.Equals((string?)obj["name"], "web_fetch", StringComparison.Ordinal))
                tools.RemoveAt(i);
        }

        tools.Add(new JsonObject
        {
            ["type"] = AnthropicFetchToolType,
            ["name"] = "web_fetch"
        });
    }

    private static void AppendGeminiGoogleSearch(JsonObject body)
    {
        var tools = EnsureToolsArray(body);
        foreach (var existing in tools)
        {
            if (existing is JsonObject obj && (obj.ContainsKey("googleSearch") || obj.ContainsKey("google_search")))
                return;
        }

        tools.Add(new JsonObject { ["googleSearch"] = new JsonObject() });
    }

    private static void AppendGeminiUrlContext(JsonObject body)
    {
        var tools = EnsureToolsArray(body);
        foreach (var existing in tools)
        {
            if (existing is JsonObject obj && (obj.ContainsKey("urlContext") || obj.ContainsKey("url_context")))
                return;
        }

        tools.Add(new JsonObject { ["urlContext"] = new JsonObject() });
    }

    private static JsonObject EnsureSearchOptions(JsonObject body)
    {
        if (body["search_options"] is JsonObject searchOptions) return searchOptions;
        searchOptions = new JsonObject();
        body["search_options"] = searchOptions;
        return searchOptions;
    }

    private static bool IsBailianOmniModel(string? modelId) =>
        NormalizeModelId(modelId).Contains("omni", StringComparison.Ordinal);

    private static JsonArray EnsureToolsArray(JsonObject body)
    {
        if (body["tools"] is JsonArray tools) return tools;
        tools = [];
        body["tools"] = tools;
        return tools;
    }

    private static void EnableDeepSeekThinking(JsonObject body, bool thinkingDialect)
    {
        if (thinkingDialect)
        {
            if (body["thinking"] is not JsonObject)
                body["thinking"] = new JsonObject();
            ((JsonObject)body["thinking"]!)["type"] = "enabled";
            return;
        }

        if (body["reasoning"] is not JsonObject)
            body["reasoning"] = new JsonObject();
        var reasoning = (JsonObject)body["reasoning"]!;
        if (reasoning["effort"] is null || string.Equals((string?)reasoning["effort"], "none", StringComparison.OrdinalIgnoreCase))
            reasoning["effort"] = "high";
    }

    private static void NormalizeResponseFormat(JsonObject body)
    {
        if (body["response_format"] is JsonObject format
            && format["type"] is JsonValue type
            && !string.Equals(type.ToString(), "text", StringComparison.OrdinalIgnoreCase))
        {
            body.Remove("response_format");
        }
    }

    private static void FixDeepSeekToolChoice(JsonObject body, string protocol)
    {
        switch (body["tool_choice"])
        {
            case JsonValue value when protocol == "messages":
                var type = value.ToString();
                body["tool_choice"] = new JsonObject
                {
                    ["type"] = string.Equals(type, "required", StringComparison.OrdinalIgnoreCase) ? "auto" : type
                };
                break;

            case JsonValue value when string.Equals(value.ToString(), "required", StringComparison.OrdinalIgnoreCase):
                body["tool_choice"] = "auto";
                break;

            case JsonObject choice when protocol == "messages"
                && string.Equals((string?)choice["type"], "tool", StringComparison.OrdinalIgnoreCase):
                body["tool_choice"] = new JsonObject { ["type"] = "auto" };
                break;

            case JsonObject choice when protocol != "messages"
                && !string.Equals((string?)choice["type"], "none", StringComparison.OrdinalIgnoreCase)
                && (protocol == "chat"
                    || string.Equals((string?)choice["type"], DeepSeekToolName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals((string?)choice["type"], DeepSeekReadUrlToolName, StringComparison.OrdinalIgnoreCase)):
                body["tool_choice"] = "auto";
                break;
        }
    }

    private static bool DefaultSupportsOpenAiModel(string id) =>
        (id.StartsWith("gpt-6", StringComparison.Ordinal)
         || id.StartsWith("gpt-5", StringComparison.Ordinal)
         || id.StartsWith("gpt-4.1", StringComparison.Ordinal)
         || id.StartsWith("gpt-4o", StringComparison.Ordinal)
         || id.StartsWith("chatgpt-4o", StringComparison.Ordinal)
         || id.StartsWith("o3", StringComparison.Ordinal)
         || id.StartsWith("o1", StringComparison.Ordinal))
        && !id.StartsWith("gpt-4.1-nano", StringComparison.Ordinal)
        && !id.Contains("-search-", StringComparison.Ordinal)
        && !id.Contains("-audio", StringComparison.Ordinal)
        && !id.Contains("-transcribe", StringComparison.Ordinal);

    private static bool DefaultSupportsAnthropicModel(string id) =>
        id.StartsWith("claude-", StringComparison.Ordinal)
        && !id.StartsWith("claude-2", StringComparison.Ordinal)
        && !id.StartsWith("claude-instant", StringComparison.Ordinal)
        && !id.StartsWith("claude-3-opus", StringComparison.Ordinal)
        && !id.StartsWith("claude-3-sonnet", StringComparison.Ordinal)
        && !id.StartsWith("claude-3-haiku", StringComparison.Ordinal);

    private static bool DefaultSupportsBailianModel(string id, string? providerType)
    {
        var isResponses = string.Equals(NormalizeProviderType(providerType), "openai-response", StringComparison.Ordinal);

        if (MatchesPrefixOrExact(id, "qwen4-max")
            || MatchesPrefixOrExact(id, "qwen3.8-max")
            || MatchesPrefixOrExact(id, "qwen3.8-flash")
            || id is "qwen3.8-2.4t-a95b" or "qwen3.8-27b" or "qwen3.8-omni-flash"
            || MatchesPrefixOrExact(id, "qwen3.7-max")
            || MatchesPrefixOrExact(id, "qwen3.7-plus")
            || MatchesPrefixOrExact(id, "qwen3.7-flash")
            || MatchesPrefixOrExact(id, "qwen3.6-plus")
            || MatchesPrefixOrExact(id, "qwen3.6-flash")
            || id == "qwen3.6-35b-a3b"
            || MatchesPrefixOrExact(id, "qwen3.5-plus")
            || MatchesPrefixOrExact(id, "qwen3.5-flash")
            || id is "qwen3.5-397b-a17b" or "qwen3.5-122b-a10b" or "qwen3.5-35b-a3b" or "qwen3.5-27b"
            || MatchesPrefixOrExact(id, "deepseek-v4.1-flash")
            || MatchesPrefixOrExact(id, "deepseek-v4-pro")
            || MatchesPrefixOrExact(id, "deepseek-v4-flash"))
        {
            return true;
        }

        if (isResponses)
        {
            return MatchesPrefixOrExact(id, "glm-5.3")
                   || MatchesPrefixOrExact(id, "glm-5.2")
                   || id == "kimi-k3"
                   || MatchesPrefixOrExact(id, "kimi-k3.1-pro");
        }

        return id == "qwen3.6-27b"
               || MatchesPrefixOrExact(id, "qwen3.5-omni-plus")
               || MatchesPrefixOrExact(id, "qwen3.5-omni-flash")
               || MatchesPrefixOrExact(id, "qwen-plus-character")
               || MatchesPrefixOrExact(id, "qwen-flash-character")
               || IsSupportedDatedQwenFlagship(id, "qwen-max", "2025-07-01")
               || IsSupportedDatedQwenFlagship(id, "qwen-plus", "2025-07-14")
               || IsSupportedDatedQwenFlagship(id, "qwen-flash", "2025-07-28");
    }

    private static bool MatchesPrefixOrExact(string id, string prefix) =>
        string.Equals(id, prefix, StringComparison.Ordinal)
        || id.StartsWith(prefix + "-", StringComparison.Ordinal);

    private static bool IsSupportedDatedQwenFlagship(string id, string family, string minSnapshotDate)
    {
        if (id == family || id == family + "-latest") return true;
        var prefix = family + "-";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = id[prefix.Length..];
        return suffix.Length == 10
               && suffix[4] == '-' && suffix[7] == '-'
               && string.CompareOrdinal(suffix, minSnapshotDate) >= 0;
    }

    private static bool IsNonChatModel(string id) =>
        id.Contains("embedding", StringComparison.Ordinal)
        || id.Contains("rerank", StringComparison.Ordinal)
        || id.Contains("moderation", StringComparison.Ordinal)
        || id.Contains("whisper", StringComparison.Ordinal)
        || id.Contains("dall-e", StringComparison.Ordinal)
        || id.Contains("gpt-image", StringComparison.Ordinal)
        || id.Contains("imagen", StringComparison.Ordinal)
        || id.Contains("veo", StringComparison.Ordinal)
        || id.Contains("wanx", StringComparison.Ordinal)
        || id.Contains("paraformer", StringComparison.Ordinal)
        || id.Contains("cosyvoice", StringComparison.Ordinal)
        || id.Contains("sambert", StringComparison.Ordinal)
        || id.Contains("qwen-ocr", StringComparison.Ordinal)
        || id.Contains("qwen-vl-ocr", StringComparison.Ordinal)
        || id.StartsWith("qwen-mt", StringComparison.Ordinal)
        || id.EndsWith("-realtime", StringComparison.Ordinal)
        || id.Contains("-realtime-", StringComparison.Ordinal)
        || id.EndsWith("-tts", StringComparison.Ordinal)
        || id.Contains("-tts-", StringComparison.Ordinal)
        || id.EndsWith("-asr", StringComparison.Ordinal)
        || id.Contains("-asr-", StringComparison.Ordinal);

    private static string NormalizeProviderType(string? providerType)
    {
        var normalized = providerType?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized == "openai" ? "openai-compat" : normalized;
    }

    private static string NormalizeModelId(string? modelId)
    {
        var value = (modelId ?? string.Empty).Trim().ToLowerInvariant();
        if (value.StartsWith("models/", StringComparison.Ordinal)) value = value[7..];
        return value;
    }
}

public static class DeepSeekServerSearch
{
    public const string ProviderId = "deepseek-server";
    public const string ToolName = BuiltinWebSearch.DeepSeekToolName;

    public static bool IsSupportedModel(string? modelId) =>
        BuiltinWebSearch.DefaultSupportsModel(BuiltinSearchVendor.DeepSeek, modelId);

    public static bool IsSupportedTarget(string? modelId, string? endpoint) =>
        IsSupportedModel(modelId) && IsOfficialEndpoint(endpoint);

    public static bool IsOfficialEndpoint(string? endpoint) =>
        BuiltinWebSearch.DetectVendor(endpoint) == BuiltinSearchVendor.DeepSeek;

    public static bool IsSupportedEndpointType(string? providerType) =>
        BuiltinWebSearch.IsSupportedProviderType(BuiltinSearchVendor.DeepSeek, providerType);
}

public sealed class ServerSearchSources
{
    private readonly Lock _gate = new();
    private readonly List<SourceReference> _sources = [];
    private readonly Dictionary<string, int> _byUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private bool _dirty;

    public void Feed(ReadOnlyMemory<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        lock (_gate)
        {
            var chars = new char[_decoder.GetCharCount(chunk.Span, flush: false)];
            _decoder.GetChars(chunk.Span, chars, flush: false);
            foreach (var c in chars)
            {
                if (c == '\n')
                {
                    ConsumeLine();
                    continue;
                }
                if (c == '\r') continue;
                _line.Append(c);
            }
        }
    }

    public void Complete()
    {
        lock (_gate) ConsumeLine();
    }

    public bool TryTake(out IReadOnlyList<SourceReference> sources)
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                sources = [];
                return false;
            }
            _dirty = false;
            sources = _sources.ToArray();
            return true;
        }
    }

    private void ConsumeLine()
    {
        if (_line.Length == 0) return;
        var line = _line.ToString();
        _line.Clear();

        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var payload = line[5..].Trim();
        if (payload.Length == 0 || payload == "[DONE]") return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException) { return; }

        using (doc) Walk(doc.RootElement, source: false, groundingChunk: false);
    }

    private void Walk(JsonElement element, bool source, bool groundingChunk)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, source, groundingChunk);
                break;

            case JsonValueKind.Object:
                var type = FirstString(element, "type");
                var isSource = source || type is "web_search_result" or "web_fetch_result"
                    or "web_search_result_location" or "url_citation";
                if (isSource && TryReadSource(element, allowUri: groundingChunk, out var title, out var url, out var date))
                {
                    Add(title, url, date);
                }
                else if (TryReadUrlMetadataSource(element, out var retrievedUrl))
                {
                    Add(retrievedUrl, retrievedUrl, null);
                }
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("action"))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (property.Value.TryGetProperty("sources", out var results))
                                Walk(results, source: true, groundingChunk: false);
                            else if (string.Equals(FirstString(property.Value, "type"), "open_page", StringComparison.Ordinal)
                                     && TryReadSource(property.Value, allowUri: false, out var openTitle, out var openUrl, out var openDate))
                                Add(openTitle, openUrl, openDate);
                        }
                        continue;
                    }
                    if (property.NameEquals("input")) continue;
                    if (property.NameEquals("groundingChunks"))
                    {
                        Walk(property.Value, source: false, groundingChunk: true);
                        continue;
                    }
                    if (groundingChunk && property.NameEquals("web"))
                    {
                        Walk(property.Value, source: true, groundingChunk: true);
                        continue;
                    }
                    var childIsSource = property.Name is "sources" or "search_results" or "page"
                        or "annotations" or "annotation" or "citations" or "citation" or "url_citation";
                    Walk(property.Value, childIsSource, groundingChunk: false);
                }
                break;
        }
    }

    private static bool TryReadSource(JsonElement obj, bool allowUri, out string title, out string url, out string? date)
    {
        title = url = string.Empty;
        date = null;

        var href = FirstString(obj, "url") ?? (allowUri ? FirstString(obj, "uri") : null);
        if (string.IsNullOrWhiteSpace(href)) return false;
        if (!href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        title = FirstString(obj, "title", "site_name")
            ?? (obj.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                ? FirstString(content, "title") : null)
            ?? href;
        date = FirstString(obj, "published_at", "published_date", "date", "page_age");
        url = href;
        return true;
    }

    private static bool TryReadUrlMetadataSource(JsonElement obj, out string url)
    {
        url = string.Empty;
        var retrieved = FirstString(obj, "retrievedUrl", "retrieved_url");
        if (string.IsNullOrWhiteSpace(retrieved)
            || (!retrieved.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !retrieved.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var status = FirstString(obj, "urlRetrievalStatus", "url_retrieval_status", "status");
        if (!string.Equals(status, "URL_RETRIEVAL_STATUS_SUCCESS", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = retrieved;
        return true;
    }

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }
        return null;
    }

    private void Add(string title, string url, string? date)
    {
        if (_byUrl.TryGetValue(url, out var existing))
        {
            var at = _sources.FindIndex(s => s.Id == existing);
            if (at >= 0 && _sources[at].Title != title && title != url)
            {
                _sources[at] = _sources[at] with { Title = title };
                _dirty = true;
            }
            return;
        }

        var id = _sources.Count + 1;
        _byUrl[url] = id;
        _sources.Add(new SourceReference(id, title, url, date));
        _dirty = true;
    }
}
