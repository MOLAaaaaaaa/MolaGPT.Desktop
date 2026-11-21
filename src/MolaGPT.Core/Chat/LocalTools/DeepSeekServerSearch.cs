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
    /// 只有 DeepSeek 的模型能用：id 里带 deepseek，但 <c>deepseek-v4-pro</c> 除外。
    /// 后者是给内部压测留的口子，不对普通会话开放服务端搜索。
    /// </summary>
    public static bool IsSupportedModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var id = modelId.Trim();
        if (id.Contains("deepseek-v4-pro", StringComparison.OrdinalIgnoreCase)) return false;
        return id.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    }

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
        if (!uri.Host.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase)) return endpoint;

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
                FixToolChoice(body);
                break;

            case "responses":
                AppendTool(body, new JsonObject { ["type"] = ToolName });
                EnableThinking(body, thinkingDialect: false);
                FixToolChoice(body);
                break;

            case "messages":
                // Messages 协议的工具对象要 name，且不要外层的 function 包装。
                AppendTool(body, new JsonObject
                {
                    ["type"] = ToolName,
                    ["name"] = ToolName
                });
                EnableThinking(body, thinkingDialect: true);
                FixToolChoice(body);
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
    /// 服务端搜索只在思考模式下允许：不带思考时 DeepSeek 连客户端工具都一起拒掉
    /// (400)。请求侧已经强制开思考了，这里只是把字段补到位——没开的话 Pi 发出去的
    /// 请求体里根本没有这个键。
    /// </summary>
    private static void EnableThinking(JsonObject body, bool thinkingDialect)
    {
        if (thinkingDialect)
        {
            if (body["thinking"] is not JsonObject)
                body["thinking"] = new JsonObject { ["type"] = "enabled" };
            return;
        }

        if (body["reasoning"] is not JsonObject)
            body["reasoning"] = new JsonObject { ["effort"] = "high" };
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
    /// <c>tool_choice</c> 不能点名服务端工具（400），<c>"required"</c> 也不行——
    /// 必选一个不存在的函数同样无解。两种都退回 <c>auto</c>；<c>"none"</c> 保持，
    /// 那是用户明确要求这一轮不调工具。
    /// </summary>
    private static void FixToolChoice(JsonObject body)
    {
        switch (body["tool_choice"])
        {
            case JsonValue value when string.Equals(value.ToString(), "required", StringComparison.OrdinalIgnoreCase):
                body["tool_choice"] = "auto";
                break;

            case JsonObject choice:
                // Anthropic 的 {"type":"tool","name":...} 和 OpenAI 的
                // {"type":"function","function":{"name":...}} 都是在点名。
                body["tool_choice"] = "auto";
                break;
        }
    }
}

/// <summary>
/// 从响应流里收集服务端搜索命中的来源。
///
/// 三种协议把结果放在完全不同的位置（Chat Completions 的 <c>annotations</c>、
/// Messages 的 <c>web_search_tool_result</c> 块、Responses 的 <c>output_item</c>），
/// 但落到最后都是一个带 url 与 title 的小对象。所以这里不按协议各写一套解析，
/// 而是深度遍历每个 SSE <c>data</c> 负载，认这种对象即可——协议再多一种也照样
/// 认得出来。
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

        // UTF-8 的一个字符可能跨 chunk，必须用增量解码器，不能逐段 GetString。
        var chars = new char[_decoder.GetCharCount(chunk.Span, flush: false)];
        _decoder.GetChars(chunk.Span, chars, flush: false);

        lock (_gate)
        {
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

        using (doc) Walk(doc.RootElement);
    }

    private void Walk(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item);
                break;

            case JsonValueKind.Object:
                if (TryReadSource(element, out var title, out var url, out var date))
                {
                    Add(title, url, date);
                    return;
                }
                foreach (var property in element.EnumerateObject()) Walk(property.Value);
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
        title = FirstString(obj, "title", "site_name") ?? href;
        date = FirstString(obj, "published_date", "date", "page_age");
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
            if (at >= 0 && _sources[at].Title != title)
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
