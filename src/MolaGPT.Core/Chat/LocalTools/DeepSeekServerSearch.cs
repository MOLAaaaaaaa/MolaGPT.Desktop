using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.LocalTools;

/// <summary>
/// DeepSeek 的「服务端搜索」(<c>deepseek_web_search</c>) 适配层。
///
/// 和别的服务商不一样，这里没有本地 HTTP 调用：搜索由模型端在做，客户端能做的
/// 只有三件事——在请求里声明这个工具、确认它被允许使用（必须开思考），以及从
/// 流里把命中的来源捡出来。三件事都按端点协议分叉，因为同一个工具在三种协议里
/// 的形状并不一样（详见 https://api-docs.deepseek.com/zh-cn/guides/web_tools ）。
///
/// 它不按次计费，Token 才是成本，所以这里不加任何「用了几次」的账。
/// </summary>
public static class DeepSeekServerSearch
{
    /// <summary>会话级的服务商标识。只出现在 <c>ConversationRoleContext</c> 里，
    /// 全局设置不接受这个值。</summary>
    public const string ProviderId = "deepseek-server";

    /// <summary>DeepSeek 在请求里认的服务端工具名。</summary>
    public const string ToolName = "deepseek_web_search";

    /// <summary>
    /// DeepSeek Web Tools 当前支持 deepseek-flash。
    /// </summary>
    public static bool IsSupportedModel(string? modelId)
    {
        return string.Equals(modelId?.Trim(), "deepseek-flash", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>仅对 DeepSeek 官方端点启用其专有工具；接受 Base URL 或完整端点。</summary>
    public static bool IsSupportedTarget(string? modelId, string? endpoint) =>
        IsSupportedModel(modelId) && IsOfficialEndpoint(endpoint);

    /// <summary>判断 Base URL 或完整请求地址是否为 DeepSeek 官方 HTTPS 端点。</summary>
    public static bool IsOfficialEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 端点类型（设置里那一列的 openai-compat / anthropic / openai-response）能不能
    /// 承载这个工具。Gemini 的原生协议没有对应的服务端工具字段，ChatGPT 套餐更不
    /// 可能认 DeepSeek 的工具，两者都不在此列。
    /// </summary>
    public static bool IsSupportedEndpointType(string? providerType) =>
        providerType?.Trim().ToLowerInvariant() is "openai-compat" or "openai-response" or "anthropic";

    /// <summary>
    /// 端点协议 → 注入形状。<c>chat</c> / <c>responses</c> / <c>messages</c>；null 表示
    /// 这个协议没有可放服务端工具的位置。
    /// </summary>
    public static string? ResolveProtocol(string? api) => api?.Trim().ToLowerInvariant() switch
    {
        "openai-completions" => "chat",
        "openai-responses" => "responses",
        "anthropic-messages" => "messages",
        _ => null
    };

    /// <summary>
    /// 端点地址里那条只影响服务端搜索的规矩：DeepSeek 的 Responses 端点在根路径
    /// （<c>/responses</c>）。<c>/v1/responses</c> 这个 OpenAI 兼容别名收得下请求、
    /// 也照常回话，但会把 <c>deepseek_web_search</c> 当不认识的工具丢掉——同一个
    /// 请求体换成 <c>/responses</c> 就真的去搜了，来源和引用都会回来。所以只有在
    /// 要用这个工具、且地址确实是 DeepSeek 官方的时候才改，别的时候一个字节不动。
    /// </summary>
    public static string AdjustEndpoint(string endpoint, string protocol)
    {
        if (!string.Equals(protocol, "responses", StringComparison.Ordinal)) return endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return endpoint;
        if (!IsOfficialEndpoint(endpoint)) return endpoint;

        const string versioned = "/v1/responses";
        var path = uri.AbsolutePath;
        if (!path.EndsWith(versioned, StringComparison.Ordinal)) return endpoint;

        return new UriBuilder(uri) { Path = path[..^versioned.Length] + "/responses" }.Uri.ToString();
    }

    /// <summary>
    /// 把注入包在别的 <c>AdaptBody</c> 外面。内层先改（它认识的那点特殊字段），
    /// 我们再补服务端搜索要的——顺序反过来的话，内层可能把刚放进去的工具又冲掉。
    /// </summary>
    public static Action<JsonObject> Wrap(string protocol, Action<JsonObject>? inner)
    {
        ArgumentException.ThrowIfNullOrEmpty(protocol);
        return body =>
        {
            inner?.Invoke(body);
            Apply(body, protocol);
        };
    }

    /// <summary>按协议把服务端工具与配套字段写进请求体。</summary>
    public static void Apply(JsonObject body, string protocol)
    {
        ArgumentNullException.ThrowIfNull(body);
        switch (protocol)
        {
            case "chat":
                AppendTool(body, new JsonObject { ["type"] = ToolName });
                EnableThinking(body, thinkingDialect: true);
                // 这几个和 deepseek_web_search 同时出现会被拒：网页搜索选项重复，
                // logprobs 与结构化输出都跟服务端搜索结果对不上。
                body.Remove("web_search_options");
                body.Remove("logprobs");
                body.Remove("top_logprobs");
                NormalizeResponseFormat(body);
                FixToolChoice(body, protocol);
                break;

            case "responses":
                AppendTool(body, new JsonObject { ["type"] = ToolName });
                EnableThinking(body, thinkingDialect: false);
                body.Remove("top_logprobs");
                if (body["text"] is JsonObject text && text["format"] is JsonObject format
                    && !string.Equals((string?)format["type"], "text", StringComparison.OrdinalIgnoreCase))
                    text.Remove("format");
                if (body["tools"] is JsonArray responseTools)
                    for (var i = responseTools.Count - 1; i >= 0; i--)
                        if (responseTools[i] is JsonObject entry
                            && (string?)entry["type"] is "web_search" or "custom" or "namespace" or "tool_search")
                            responseTools.RemoveAt(i);
                FixToolChoice(body, protocol);
                break;

            case "messages":
                // Messages 协议的工具对象要 name，且不要外层的 function 包装。
                AppendTool(body, new JsonObject
                {
                    ["type"] = ToolName,
                    ["name"] = ToolName
                });
                EnableThinking(body, thinkingDialect: true);
                FixToolChoice(body, protocol);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "未知的端点协议。");
        }
    }

    private static void AppendTool(JsonObject body, JsonObject tool)
    {
        if (body["tools"] is not JsonArray tools)
        {
            tools = [];
            body["tools"] = tools;
        }

        // 多轮里同一份请求可能被重放（重试、续写），重复声明会被拒。
        foreach (var existing in tools)
        {
            if (existing is JsonObject obj
                && string.Equals((string?)obj["type"], ToolName, StringComparison.Ordinal))
                return;
        }

        tools.Add(tool);
    }

    /// <summary>
    /// 本客户端的服务端搜索采用深度思考模式，并覆盖请求中的关闭值。
    /// </summary>
    private static void EnableThinking(JsonObject body, bool thinkingDialect)
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

    /// <summary>
    /// 保留明确关闭工具的意图，并将不兼容的指定工具选择转为自动。
    /// </summary>
    private static void FixToolChoice(JsonObject body, string protocol)
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
                && (protocol == "chat" || string.Equals((string?)choice["type"], ToolName, StringComparison.OrdinalIgnoreCase)):
                body["tool_choice"] = "auto";
                break;
        }
    }
}

/// <summary>
/// 从响应流里收集服务端搜索命中的来源。
///
/// 只收集结果、页面和引用中的网址，不把工具调用的 action 当作成功结果。
/// 原始引用位置不会随 Pi 的文本事件转发，因此这里仅提供来源列表。
///
/// 只在转发线程上被写入、在流的循环里被读取，一把锁足够。
/// </summary>
public sealed class ServerSearchSources
{
    private readonly Lock _gate = new();
    private readonly List<SourceReference> _sources = [];
    private readonly Dictionary<string, int> _byUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    // 一行可能被拆在两个 chunk 里，尤其是中文标题。
    private readonly StringBuilder _line = new();
    private bool _dirty;

    /// <summary>喂一段原始响应字节（SSE 或一整个 JSON）。线程安全。</summary>
    public void Feed(ReadOnlyMemory<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        lock (_gate)
        {
            // UTF-8 的一个字符可能跨 chunk，必须用增量解码器。
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

    /// <summary>流结束时把最后一行（通常没有换行）也读掉。</summary>
    public void Complete()
    {
        lock (_gate) ConsumeLine();
    }

    /// <summary>列表长了就交出去一次，和 <c>SearchCitations</c> 一个规矩：给的是全量。</summary>
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

        // 注释行（": ping"）和 event: 行没有负载；只认 data:。
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var payload = line[5..].Trim();
        if (payload.Length == 0 || payload == "[DONE]") return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException) { return; }

        using (doc) Walk(doc.RootElement, false);
    }

    private void Walk(JsonElement element, bool source)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, source);
                break;

            case JsonValueKind.Object:
                var type = FirstString(element, "type");
                var isSource = source || type is "web_search_result" or "web_fetch_result"
                    or "web_search_result_location" or "url_citation";
                if (isSource && TryReadSource(element, out var title, out var url, out var date))
                {
                    Add(title, url, date);
                }
                foreach (var property in element.EnumerateObject())
                {
                    // Responses 的搜索结果在 action.sources；action.url 则只是请求。
                    if (property.NameEquals("action"))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object
                            && property.Value.TryGetProperty("sources", out var results))
                            Walk(results, true);
                        continue;
                    }
                    if (property.NameEquals("input")) continue;
                    var childIsSource = property.Name is "sources" or "page" or "annotations"
                        or "annotation" or "citations" or "citation" or "url_citation";
                    Walk(property.Value, childIsSource);
                }
                break;
        }
    }

    private static bool TryReadSource(JsonElement obj, out string title, out string url, out string? date)
    {
        title = url = string.Empty;
        date = null;

        if (!obj.TryGetProperty("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
            return false;

        var href = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(href)) return false;
        if (!href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        // title 是引用条目和结果条目都有的；没有就退到站点名，再没有就用地址。
        title = FirstString(obj, "title", "site_name")
            ?? (obj.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                ? FirstString(content, "title") : null)
            ?? href;
        date = FirstString(obj, "published_at", "published_date", "date", "page_age");
        url = href;
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
            // 同一地址可能先以站点名出现、后补上标题；标题更具体，值得替换。
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
