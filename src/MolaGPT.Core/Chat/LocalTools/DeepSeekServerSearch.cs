using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Chat.Tools;
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
    private const string DeepSeekProModel = "deepseek-v4-pro";

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

    public static bool CanUseWireApi(BuiltinSearchVendor vendor, string? api, string? modelId = null)
    {
        var normalized = api?.Trim().ToLowerInvariant();
        return vendor switch
        {
            BuiltinSearchVendor.DeepSeek when NormalizeModelId(modelId) == DeepSeekProModel =>
                normalized == "openai-responses",
            BuiltinSearchVendor.DeepSeek =>
                normalized is "openai-completions" or "openai-responses" or "anthropic-messages",
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

    public static bool ForcesThinking(BuiltinSearchVendor vendor, string? modelId) =>
        vendor == BuiltinSearchVendor.DeepSeek && NormalizeModelId(modelId) != DeepSeekProModel;

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
        BuiltinSearchVendor.DeepSeek => "支持搜索与网页读取，仅部分模型可用，开启后默认启用深度思考。可能产生额外费用。",
        BuiltinSearchVendor.OpenAi => "支持搜索与网页浏览，仅部分模型可用。可能产生额外费用。",
        BuiltinSearchVendor.Bailian => "支持搜索，开启思考时含网页读取。仅部分模型可用，可能产生额外费用。",
        BuiltinSearchVendor.Anthropic => "支持搜索与网页读取，仅部分模型可用。可能产生额外费用。",
        BuiltinSearchVendor.Gemini => "支持搜索与网页读取，仅部分模型可用。可能产生额外费用。",
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
            BuiltinSearchVendor.DeepSeek => id == "deepseek-flash"
                || (id == DeepSeekProModel
                    && string.Equals(NormalizeProviderType(providerType), "openai-response", StringComparison.Ordinal)),
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
            || DetectVendor(endpoint) != BuiltinSearchVendor.DeepSeek
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return endpoint;
        }

        if (string.Equals(api, "openai-responses", StringComparison.OrdinalIgnoreCase))
        {
            const string versioned = "/v1/responses";
            var path = uri.AbsolutePath;
            if (!path.EndsWith(versioned, StringComparison.Ordinal)) return endpoint;

            return new UriBuilder(uri) { Path = path[..^versioned.Length] + "/responses" }.Uri.ToString();
        }

        if (string.Equals(api, "anthropic-messages", StringComparison.OrdinalIgnoreCase))
        {
            const string bareMessages = "/v1/messages";
            var path = uri.AbsolutePath;
            if (!path.EndsWith(bareMessages, StringComparison.Ordinal)
                || path.EndsWith("/anthropic/v1/messages", StringComparison.Ordinal))
            {
                return endpoint;
            }

            return new UriBuilder(uri) { Path = path[..^bareMessages.Length] + "/anthropic/v1/messages" }.Uri.ToString();
        }

        return endpoint;
    }

    public static string? BuildSystemHint(
        BuiltinSearchVendor vendor,
        string? api,
        string? modelId,
        bool enableWebFetch)
    {
        if (vendor != BuiltinSearchVendor.DeepSeek
            || !CanUseWireApi(vendor, api, modelId)
            || NormalizeModelId(modelId) == DeepSeekProModel)
        {
            return null;
        }

        var scope = enableWebFetch ? "联网搜索与网页读取" : "联网搜索";
        return "<联网搜索>\n"
               + $"当前会话已启用 DeepSeek 服务端内置{scope}（系统内置 search / open / find 工具，不在客户端 function 列表中）。\n"
               + "查询实时资讯、检索未知网址或核验事实时，直接在思考阶段发起内置检索并在检索完成后直接给出回答；不要在发起内置检索前先输出过渡回复，也不要误判为没有搜索工具或改用 browser / web_fetch 代替搜索引擎。\n"
               + "</联网搜索>";
    }

    public static bool ReplacesLocalWebFetch(
        BuiltinSearchVendor vendor,
        string? api,
        string? modelId,
        bool useThinking)
    {
        if (!CanUseWireApi(vendor, api, modelId)) return false;
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
                ApplyDeepSeek(body, normalizedApi, modelId, enableWebFetch);
                break;

            case BuiltinSearchVendor.OpenAi when normalizedApi == "openai-responses":
                AppendTypedTool(body, "web_search", "web_search", "web_search_preview");
                // Web search rejects Pi's minimal effort; leave other settings to the API.
                if (body["reasoning"] is JsonObject reasoning
                    && reasoning["effort"] is JsonValue value
                    && value.TryGetValue<string>(out var effort)
                    && effort == "minimal")
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
                EnsureGeminiServerToolInvocations(body);
                break;
        }
    }

    private static void ApplyDeepSeek(JsonObject body, string api, string? modelId, bool enableWebFetch)
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

            case "openai-responses" or "responses" when NormalizeModelId(modelId) == DeepSeekProModel:
                // Pro exposes web_search, not the Flash chatbot tools; declaring both is rejected.
                var tools = EnsureToolsArray(body);
                for (var i = tools.Count - 1; i >= 0; i--)
                {
                    if (tools[i] is JsonObject tool
                        && (string?)tool["type"] is DeepSeekToolName or DeepSeekReadUrlToolName)
                    {
                        tools.RemoveAt(i);
                    }
                }
                AppendTypedTool(body, "web_search", "web_search");
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

    private static void EnsureGeminiServerToolInvocations(JsonObject body)
    {
        if (body["toolConfig"] is not JsonObject toolConfig)
        {
            toolConfig = new JsonObject();
            body["toolConfig"] = toolConfig;
        }

        toolConfig["includeServerSideToolInvocations"] = true;
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

public sealed class ServerSearchSources(LocalToolOptions? options = null)
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class OpenCall(string id, string name, string args)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public string Args { get; set; } = args;
        public bool Done { get; set; }
        public bool HasResult { get; set; }
    }

    private sealed record AnthropicBlock(string Id, string Name, StringBuilder Json);

    private readonly LocalToolOptions? _options = options;
    private readonly Lock _gate = new();
    private readonly List<SourceReference> _sources = [];
    private readonly Dictionary<string, int> _byUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenCall> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<int, AnthropicBlock> _anthropicBlocks = [];
    private readonly List<ToolCallDelta> _pendingTools = [];
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private bool _dirty;
    private bool _sawExplicitSearchCall;
    private int _seq;

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
        lock (_gate)
        {
            ConsumeLine();
            var fallbackSourcesJson = _sources.Count > 0
                ? JsonSerializer.Serialize(new
                {
                    success = true,
                    results = _sources.Select(s => new { title = s.Title, url = s.Url })
                }, CompactJsonOptions)
                : null;
            foreach (var call in _calls.Values)
            {
                if (!call.Done)
                {
                    call.Done = true;
                    var result = call.Name == "search_web" ? fallbackSourcesJson : null;
                    call.HasResult = result is not null;
                    EnqueueTool(call.Id, call.Name, call.Args, failed: false, running: false, result);
                }
                else if (!call.HasResult && call.Name == "search_web" && fallbackSourcesJson is not null)
                {
                    call.HasResult = true;
                    EnqueueTool(call.Id, call.Name, call.Args, failed: false, running: false, fallbackSourcesJson);
                }
            }
        }
    }

    public bool TryTakeTools(out IReadOnlyList<ToolCallDelta> tools)
    {
        lock (_gate)
        {
            if (_pendingTools.Count == 0)
            {
                tools = [];
                return false;
            }
            tools = _pendingTools.ToArray();
            _pendingTools.Clear();
            return true;
        }
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

        using (doc)
        {
            Walk(doc.RootElement, source: false, groundingChunk: false);
            InspectToolEvents(doc.RootElement);
        }
    }

    private void InspectToolEvents(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var eventType = FirstString(root, "type");

        // 1. OpenAI / DeepSeek / Bailian Responses API
        if (eventType is "response.output_item.added" or "response.output_item.done"
            && root.TryGetProperty("item", out var item)
            && item.ValueKind == JsonValueKind.Object)
        {
            var itemType = FirstString(item, "type");
            if (itemType is "web_search_call" or "web_extractor" or "web_extractor_call")
            {
                var id = FirstString(item, "id") ?? $"srv_{++_seq}";
                var (toolName, argsJson) = DescribeActionCall(item, itemType);
                if (eventType == "response.output_item.added")
                {
                    EmitRunning(id, toolName, argsJson);
                }
                else
                {
                    var failed = string.Equals(FirstString(item, "status"), "failed", StringComparison.OrdinalIgnoreCase);
                    EmitFinished(id, toolName, argsJson, failed, BuildItemResultJson(item, toolName, failed));
                }
                return;
            }
        }

        // 2. Anthropic Messages API
        if (eventType == "content_block_start"
            && root.TryGetProperty("content_block", out var block)
            && block.ValueKind == JsonValueKind.Object)
        {
            var blockType = FirstString(block, "type");
            var index = root.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var parsedIdx) ? parsedIdx : -1;
            if (blockType == "server_tool_use")
            {
                var id = FirstString(block, "id") ?? $"srv_{++_seq}";
                var rawName = FirstString(block, "name") ?? "web_search";
                var (toolName, argsJson) = NormalizeAnthropicInput(
                    rawName,
                    block.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp : default);
                if (index >= 0) _anthropicBlocks[index] = new AnthropicBlock(id, rawName, new StringBuilder());
                EmitRunning(id, toolName, argsJson);
                return;
            }

            if (blockType is "web_search_tool_result" or "web_fetch_tool_result")
            {
                var toolUseId = FirstString(block, "tool_use_id");
                if (!string.IsNullOrWhiteSpace(toolUseId))
                {
                    var defaultName = blockType == "web_fetch_tool_result" ? "web_fetch" : "search_web";
                    var (failed, resultJson) = BuildAnthropicResultJson(block);
                    EmitFinished(toolUseId, defaultName, null, failed, resultJson);
                }
                return;
            }
        }
        else if (eventType == "content_block_delta"
                 && root.TryGetProperty("index", out var deltaIdxNode)
                 && deltaIdxNode.TryGetInt32(out var deltaIdx)
                 && _anthropicBlocks.TryGetValue(deltaIdx, out var openBlock)
                 && root.TryGetProperty("delta", out var deltaObj)
                 && deltaObj.ValueKind == JsonValueKind.Object
                 && string.Equals(FirstString(deltaObj, "type"), "input_json_delta", StringComparison.Ordinal)
                 && deltaObj.TryGetProperty("partial_json", out var partialNode)
                 && partialNode.ValueKind == JsonValueKind.String)
        {
            openBlock.Json.Append(partialNode.GetString());
            return;
        }
        else if (eventType == "content_block_stop"
                 && root.TryGetProperty("index", out var stopIdxNode)
                 && stopIdxNode.TryGetInt32(out var stopIdx)
                 && _anthropicBlocks.Remove(stopIdx, out var finishedBlock)
                 && finishedBlock.Json.Length > 0)
        {
            try
            {
                using var argDoc = JsonDocument.Parse(finishedBlock.Json.ToString());
                var (updatedName, updatedArgs) = NormalizeAnthropicInput(finishedBlock.Name, argDoc.RootElement);
                EmitRunning(finishedBlock.Id, updatedName, updatedArgs);
            }
            catch (JsonException)
            {
            }
            return;
        }

        // 3. DeepSeek Chat Completions server_tool_event
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object
                    || !choice.TryGetProperty("server_tool_event", out var serverEv)
                    || serverEv.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var evType = FirstString(serverEv, "type");
                if (evType is "call_started" or "call_completed"
                    && serverEv.TryGetProperty("call", out var call)
                    && call.ValueKind == JsonValueKind.Object)
                {
                    var id = FirstString(call, "id") ?? $"srv_{++_seq}";
                    var (toolName, argsJson) = DescribeActionCall(call, FirstString(call, "type"));
                    if (evType == "call_started")
                    {
                        EmitRunning(id, toolName, argsJson);
                    }
                    else
                    {
                        var failed = string.Equals(FirstString(call, "status"), "failed", StringComparison.OrdinalIgnoreCase);
                        EmitFinished(id, toolName, argsJson, failed, BuildItemResultJson(call, toolName, failed));
                    }
                }
            }
        }

        // 4. Gemini Native parts (toolCall / toolResponse) + groundingMetadata fallback
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var cand in candidates.EnumerateArray())
            {
                if (cand.ValueKind != JsonValueKind.Object) continue;
                if (cand.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Object
                    && content.TryGetProperty("parts", out var parts)
                    && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object) continue;
                        if (part.TryGetProperty("toolCall", out var tc) && tc.ValueKind == JsonValueKind.Object)
                        {
                            var toolType = FirstString(tc, "toolType", "tool_type");
                            var id = FirstString(tc, "id") ?? $"gem_{++_seq}";
                            var tcArgs = tc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                            if (string.Equals(toolType, "URL_CONTEXT", StringComparison.OrdinalIgnoreCase))
                            {
                                var urls = ReadStringList(tcArgs, "urls", "url");
                                EmitRunning(id, "web_fetch", BuildFetchArgs(urls.FirstOrDefault()));
                            }
                            else if (string.Equals(toolType, "GOOGLE_SEARCH_WEB", StringComparison.OrdinalIgnoreCase))
                            {
                                var queries = ReadStringList(tcArgs, "queries", "query");
                                EmitRunning(id, "search_web", BuildSearchArgs(queries));
                            }
                        }
                        else if (part.TryGetProperty("toolResponse", out var tr) && tr.ValueKind == JsonValueKind.Object)
                        {
                            var id = FirstString(tr, "id");
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            var toolType = FirstString(tr, "toolType", "tool_type");
                            if (string.Equals(toolType, "URL_CONTEXT", StringComparison.OrdinalIgnoreCase))
                            {
                                var (failed, resultJson) = BuildGeminiUrlContextResult(tr);
                                EmitFinished(id, "web_fetch", null, failed, resultJson);
                            }
                            else if (string.Equals(toolType, "GOOGLE_SEARCH_WEB", StringComparison.OrdinalIgnoreCase))
                            {
                                EmitFinished(id, "search_web", null, failed: false, resultJson: null);
                            }
                        }
                    }
                }

                if (!_sawExplicitSearchCall
                    && cand.TryGetProperty("groundingMetadata", out var gm)
                    && gm.ValueKind == JsonValueKind.Object)
                {
                    var queries = ReadStringList(gm, "webSearchQueries", "web_search_queries");
                    if (queries.Count > 0)
                    {
                        var id = $"gem_grounding_{++_seq}";
                        var argsJson = BuildSearchArgs(queries);
                        var resultJson = _sources.Count > 0
                            ? JsonSerializer.Serialize(new
                            {
                                success = true,
                                results = _sources.Select(s => new { title = s.Title, url = s.Url })
                            }, CompactJsonOptions)
                            : null;
                        EmitRunning(id, "search_web", argsJson);
                        EmitFinished(id, "search_web", argsJson, failed: false, resultJson);
                    }
                }
            }
        }

        // 5. Bailian Chat Completions search_info.search_results fallback
        if (!_sawExplicitSearchCall
            && root.TryGetProperty("search_info", out var searchInfo)
            && searchInfo.ValueKind == JsonValueKind.Object
            && searchInfo.TryGetProperty("search_results", out var searchResults)
            && searchResults.ValueKind == JsonValueKind.Array
            && searchResults.GetArrayLength() > 0)
        {
            var id = $"bailian_search_{++_seq}";
            var argsJson = "{}";
            var resultJson = JsonSerializer.Serialize(new
            {
                success = true,
                results = _sources.Select(s => new { title = s.Title, url = s.Url })
            }, CompactJsonOptions);
            EmitRunning(id, "search_web", argsJson);
            EmitFinished(id, "search_web", argsJson, failed: false, resultJson);
        }
    }

    private void EmitRunning(string id, string toolName, string argsJson)
    {
        if (toolName == "search_web") _sawExplicitSearchCall = true;
        if (_calls.TryGetValue(id, out var existing))
        {
            if (existing.Done) return;
            existing.Name = toolName;
            if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
                existing.Args = argsJson;
            EnqueueTool(id, existing.Name, existing.Args, failed: false, running: true, resultJson: null);
            return;
        }

        var call = new OpenCall(id, toolName, string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        _calls[id] = call;
        EnqueueTool(id, call.Name, call.Args, failed: false, running: true, resultJson: null);
    }

    private void EmitFinished(string id, string fallbackName, string? argsJson, bool failed, string? resultJson)
    {
        if (!_calls.TryGetValue(id, out var call))
        {
            if (fallbackName == "search_web") _sawExplicitSearchCall = true;
            call = new OpenCall(id, fallbackName, string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
            _calls[id] = call;
        }
        else if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}" && call.Args == "{}")
        {
            call.Args = argsJson!;
        }

        call.Done = true;
        call.HasResult = !string.IsNullOrWhiteSpace(resultJson);
        EnqueueTool(id, call.Name, call.Args, failed, running: false, resultJson);
    }

    private void EnqueueTool(string id, string name, string argsJson, bool failed, bool running, string? resultJson)
    {
        var builderStatus = running ? "running" : failed ? "error" : "completed";
        var delta = ToolDeltaBuilder.BuildToolDelta(id, name, argsJson, _options, builderStatus, resultJson);
        if (!running && failed)
            delta = delta with { Status = "failed" };
        _pendingTools.Add(delta);
    }

    private static (string ToolName, string ArgsJson) DescribeActionCall(JsonElement item, string? itemType)
    {
        if (string.Equals(itemType, "web_extractor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(itemType, "web_extractor_call", StringComparison.OrdinalIgnoreCase)
            || string.Equals(itemType, BuiltinWebSearch.DeepSeekReadUrlToolName, StringComparison.OrdinalIgnoreCase))
        {
            var directUrl = FirstString(item, "url")
                ?? (item.TryGetProperty("action", out var act) && act.ValueKind == JsonValueKind.Object
                    ? FirstString(act, "url")
                    : null);
            return ("web_fetch", BuildFetchArgs(directUrl));
        }

        if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            var actionType = FirstString(action, "type") ?? "search";
            if (actionType is "open_page" or "read_link" or "find_in_page")
                return ("web_fetch", BuildFetchArgs(FirstString(action, "url"), FirstString(action, "pattern")));

            var queries = ReadStringList(action, "queries", "query");
            // DeepSeek can include its call ID as a query entry; it is not search text.
            if (FirstString(item, "id") is { } id && queries.Contains($"ws_call_id={id}"))
                queries = queries.Where(query => query != $"ws_call_id={id}").ToArray();
            return ("search_web", BuildSearchArgs(queries));
        }

        return ("search_web", "{}");
    }

    private static (string ToolName, string ArgsJson) NormalizeAnthropicInput(string rawName, JsonElement input)
    {
        var defaultTool = rawName is "web_fetch" or BuiltinWebSearch.DeepSeekReadUrlToolName
            ? "web_fetch"
            : "search_web";
        if (input.ValueKind != JsonValueKind.Object) return (defaultTool, "{}");

        var action = FirstString(input, "action");
        if (defaultTool == "web_fetch" || action is "open_page" or "read_link" or "find_in_page")
            return ("web_fetch", BuildFetchArgs(FirstString(input, "url"), FirstString(input, "pattern")));

        var queries = ReadStringList(input, "queries", "query");
        return ("search_web", BuildSearchArgs(queries));
    }

    private static string BuildSearchArgs(IReadOnlyList<string> queries) =>
        queries.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(new { queries }, CompactJsonOptions);

    private static string BuildFetchArgs(string? url, string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return "{}";
        return string.IsNullOrWhiteSpace(pattern)
            ? JsonSerializer.Serialize(new { action = "scrape", url }, CompactJsonOptions)
            : JsonSerializer.Serialize(new { action = "scrape", url, pattern }, CompactJsonOptions);
    }

    private static string? BuildItemResultJson(JsonElement item, string toolName, bool failed)
    {
        if (failed)
        {
            var code = item.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                ? FirstString(err, "message", "code")
                : FirstString(item, "error");
            return JsonSerializer.Serialize(new { success = false, error = code ?? "服务端工具执行失败。" }, CompactJsonOptions);
        }

        if (toolName == "web_fetch")
        {
            if (item.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
            {
                var url = FirstString(page, "url");
                var title = FirstString(page, "title");
                if (!string.IsNullOrWhiteSpace(url))
                    return JsonSerializer.Serialize(new { success = true, url, title }, CompactJsonOptions);
            }
            return null;
        }

        if (item.TryGetProperty("action", out var action)
            && action.ValueKind == JsonValueKind.Object
            && action.TryGetProperty("sources", out var sources)
            && sources.ValueKind == JsonValueKind.Array)
        {
            var list = new List<object>();
            foreach (var entry in sources.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && TryReadSource(entry, allowUri: false, out var title, out var url, out _))
                {
                    list.Add(new { title, url });
                }
            }
            if (list.Count > 0)
                return JsonSerializer.Serialize(new { success = true, results = list }, CompactJsonOptions);
        }

        if (item.TryGetProperty("sources", out var directSources) && directSources.ValueKind == JsonValueKind.Array)
        {
            var list = new List<object>();
            foreach (var entry in directSources.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && TryReadSource(entry, allowUri: false, out var title, out var url, out _))
                {
                    list.Add(new { title, url });
                }
            }
            if (list.Count > 0)
                return JsonSerializer.Serialize(new { success = true, results = list }, CompactJsonOptions);
        }

        return null;
    }

    private static (bool Failed, string? ResultJson) BuildAnthropicResultJson(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return (false, null);
        if (content.ValueKind == JsonValueKind.Object)
        {
            var type = FirstString(content, "type");
            if (type?.EndsWith("_error", StringComparison.OrdinalIgnoreCase) == true)
            {
                var errCode = FirstString(content, "error_code", "message") ?? "服务端工具执行失败。";
                return (true, JsonSerializer.Serialize(new { success = false, error = errCode }, CompactJsonOptions));
            }
            if (TryReadSource(content, allowUri: false, out var singleTitle, out var singleUrl, out _))
                return (false, JsonSerializer.Serialize(new { success = true, title = singleTitle, url = singleUrl }, CompactJsonOptions));
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var list = new List<object>();
            foreach (var entry in content.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && TryReadSource(entry, allowUri: false, out var title, out var url, out _))
                {
                    list.Add(new { title, url });
                }
            }
            if (list.Count > 0)
                return (false, JsonSerializer.Serialize(new { success = true, results = list }, CompactJsonOptions));
        }

        return (false, null);
    }

    private static (bool Failed, string? ResultJson) BuildGeminiUrlContextResult(JsonElement tr)
    {
        if (!tr.TryGetProperty("response", out var resp)
            || resp.ValueKind != JsonValueKind.Object
            || !resp.TryGetProperty("url_metadata", out var meta)
            || meta.ValueKind != JsonValueKind.Array
            || meta.GetArrayLength() == 0)
        {
            return (false, null);
        }

        var urls = new List<object>();
        var anySuccess = false;
        foreach (var entry in meta.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var url = FirstString(entry, "retrieved_url", "retrievedUrl");
            var status = FirstString(entry, "url_retrieval_status", "urlRetrievalStatus");
            var ok = string.Equals(status, "URL_RETRIEVAL_STATUS_SUCCESS", StringComparison.OrdinalIgnoreCase);
            if (ok) anySuccess = true;
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(new { url, status });
        }

        if (!anySuccess)
        {
            return (true, JsonSerializer.Serialize(new
            {
                success = false,
                error = "目标网页无法访问或受限。",
                urls
            }, CompactJsonOptions));
        }

        return (false, JsonSerializer.Serialize(new { success = true, urls }, CompactJsonOptions));
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string arrayName, string singleName)
    {
        if (obj.ValueKind != JsonValueKind.Object) return [];
        var list = new List<string>();
        if (obj.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!);
            }
        }
        if (list.Count == 0 && FirstString(obj, singleName) is { } single)
            list.Add(single);
        return list;
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
