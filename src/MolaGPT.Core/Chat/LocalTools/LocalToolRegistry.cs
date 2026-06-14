using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MolaGPT.Core.Auth;

namespace MolaGPT.Core.Chat.LocalTools;

public static partial class LocalToolRegistry
{
    private const int MaxSearchQueries = 5;
    private const int MaxPageCharacters = 12000;
    private static readonly JsonSerializerOptions ToolResultJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<object> BuildOpenAiToolDefinitions(LocalToolOptions options)
    {
        var tools = new List<object>();
        if (options.Network)
        {
            tools.Add(new
            {
                type = "function",
                function = new
                {
                    name = "search_web",
                    description = "在网络上搜索最新信息。适合查新闻、实时资料、产品/库/文档状态、事实核验。一次可提交 1 到 5 个查询。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            queries = new
                            {
                                type = "array",
                                description = "搜索查询列表，每项包含 query，可选 topic/news/general。",
                                minItems = 1,
                                maxItems = 5,
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        query = new { type = "string", description = "搜索关键词" },
                                        topic = new { type = "string", @enum = new[] { "general", "news" }, description = "搜索主题" }
                                    },
                                    required = new[] { "query" }
                                }
                            },
                            query = new
                            {
                                type = "string",
                                description = "兼容旧格式：单个搜索关键词。优先使用 queries。"
                            }
                        }
                    }
                }
            });
        }

        if (options.WebPage)
        {
            tools.Add(new
            {
                type = "function",
                function = new
                {
                    name = "web_fetch",
                    description = "本地轻量网页访问工具。用于打开具体 URL 并提取页面标题、正文文本和链接。当前桌面客户端仅支持 action=scrape。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new
                            {
                                type = "string",
                                @enum = new[] { "scrape" },
                                description = "操作类型。当前支持 scrape。"
                            },
                            url = new { type = "string", description = "目标网页 URL，必须是 http 或 https。" },
                            options = new
                            {
                                type = "object",
                                description = "可选参数。maxCharacters 控制返回正文长度。",
                                properties = new
                                {
                                    maxCharacters = new { type = "integer", description = "最多返回多少字符，默认 12000。" }
                                }
                            }
                        },
                        required = new[] { "action", "url" }
                    }
                }
            });
        }

        if (options.FileTools)
        {
            tools.Add(new
            {
                type = "function",
                function = new
                {
                    name = "read_file",
                    description = "读取本地文件的文本内容。优先用本工具读取文件，而不是写 Python 代码去 open()。可选按行范围读取大文件。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "文件的绝对路径或相对路径" },
                            offset = new { type = "integer", description = "起始行号（1 基，可选）" },
                            limit = new { type = "integer", description = "读取行数（可选，默认整文件，最多 2000 行）" }
                        },
                        required = new[] { "path" }
                    }
                }
            });
            tools.Add(new
            {
                type = "function",
                function = new
                {
                    name = "glob_files",
                    description = "按通配模式查找文件（支持 ** 和 *，如 **/*.cs）。优先用本工具找文件，而不是写 Python 遍历目录。结果按修改时间倒序。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            pattern = new { type = "string", description = "通配模式，如 **/*.md 或 src/**/*.cs" },
                            path = new { type = "string", description = "搜索根目录（可选，默认当前工作目录）" },
                            limit = new { type = "integer", description = "最多返回多少条（可选，默认 200）" }
                        },
                        required = new[] { "pattern" }
                    }
                }
            });
            tools.Add(new
            {
                type = "function",
                function = new
                {
                    name = "grep_files",
                    description = "在文件内容中按正则搜索（类似 grep/ripgrep）。优先用本工具搜内容，而不是写 Python。返回命中的文件、行号、行文本。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            pattern = new { type = "string", description = "正则表达式" },
                            path = new { type = "string", description = "搜索根目录（可选，默认当前工作目录）" },
                            glob = new { type = "string", description = "限定文件名/路径的通配，如 *.cs（可选）" },
                            ignore_case = new { type = "boolean", description = "是否忽略大小写（可选）" },
                            max_matches = new { type = "integer", description = "最多命中条数（可选，默认 100）" }
                        },
                        required = new[] { "pattern" }
                    }
                }
            });
        }

        return tools;
    }

    public static async Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        LocalToolOptions options,
        HttpClient http,
        CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "search_web" => await SearchWebAsync(argumentsJson, options, http, ct).ConfigureAwait(false),
                "web_fetch" => await ScrapeWebPageAsync(argumentsJson, options, http, ct).ConfigureAwait(false),
                "read_file" => await Task.Run(() => ExecuteReadFile(argumentsJson, options, ct), ct).ConfigureAwait(false),
                "glob_files" => await Task.Run(() => ExecuteGlob(argumentsJson, options, ct), ct).ConfigureAwait(false),
                "grep_files" => await Task.Run(() => ExecuteGrep(argumentsJson, options, ct), ct).ConfigureAwait(false),
                _ => SerializeToolResult(new
                {
                    success = false,
                    error = $"Unknown local tool: {toolName}"
                })
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SerializeToolResult(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    private static string ExecuteReadFile(string argumentsJson, LocalToolOptions options, CancellationToken ct)
    {
        using var doc = ParseArgs(argumentsJson);
        var root = doc?.RootElement;
        var path = ReadArgString(root, "path");
        var offset = ReadArgInt(root, "offset");
        var limit = ReadArgInt(root, "limit");
        return SerializeToolResult(FileToolset.ReadFile(path, offset, limit, options.DeniedPathPrefixList, ct));
    }

    private static string ExecuteGlob(string argumentsJson, LocalToolOptions options, CancellationToken ct)
    {
        using var doc = ParseArgs(argumentsJson);
        var root = doc?.RootElement;
        var pattern = ReadArgString(root, "pattern");
        var path = ReadArgString(root, "path");
        var limit = ReadArgInt(root, "limit");
        return SerializeToolResult(FileToolset.Glob(pattern, path, limit, options.DeniedPathPrefixList, ct));
    }

    private static string ExecuteGrep(string argumentsJson, LocalToolOptions options, CancellationToken ct)
    {
        using var doc = ParseArgs(argumentsJson);
        var root = doc?.RootElement;
        var pattern = ReadArgString(root, "pattern");
        var path = ReadArgString(root, "path");
        var glob = ReadArgString(root, "glob");
        var ignoreCase = ReadArgBool(root, "ignore_case");
        var max = ReadArgInt(root, "max_matches");
        return SerializeToolResult(FileToolset.Grep(pattern, path, glob, ignoreCase, max, options.DeniedPathPrefixList, ct));
    }

    private static JsonDocument? ParseArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try { return JsonDocument.Parse(argumentsJson); }
        catch (JsonException) { return null; }
    }

    private static string? ReadArgString(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? ReadArgInt(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var i)
            ? i
            : null;

    private static bool ReadArgBool(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static async Task<string> SearchWebAsync(
        string argumentsJson,
        LocalToolOptions options,
        HttpClient http,
        CancellationToken ct)
    {
        var queries = ParseSearchQueries(argumentsJson);
        var provider = NormalizeSearchProvider(options.SearchProvider);

        // Provider-specific concurrency budgets (see research notes):
        //   tavily/exa: 5 in flight is well within published per-minute caps.
        //   duckduckgo: HTML scraping; community consensus is ~5-10 req/min before
        //   IP throttling kicks in, so we serialize and stagger.
        var maxConcurrency = provider switch
        {
            "tavily" or "exa" => Math.Min(5, queries.Count),
            _ => 1
        };
        var interQueryDelay = provider == "duckduckgo" && queries.Count > 1
            ? TimeSpan.FromMilliseconds(1500)
            : TimeSpan.Zero;

        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new Task<object>[queries.Count];
        for (var i = 0; i < queries.Count; i++)
        {
            var index = i;
            var query = queries[i];
            tasks[i] = RunOneSearchAsync(index, query, provider, options, http, gate, interQueryDelay, ct);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return SerializeToolResult(new
        {
            success = true,
            source = "local_search_web",
            provider,
            queries = results
        });
    }

    private static async Task<object> RunOneSearchAsync(
        int index,
        string query,
        string provider,
        LocalToolOptions options,
        HttpClient http,
        SemaphoreSlim gate,
        TimeSpan interQueryDelay,
        CancellationToken ct)
    {
        // Stagger the start of each DDG request so we don't fire two HTTP
        // connections in the same 100ms window — even with a semaphore of 1
        // the requests would otherwise back up tightly when the previous one
        // finishes.
        if (interQueryDelay > TimeSpan.Zero && index > 0)
        {
            try { await Task.Delay(interQueryDelay * index, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var hits = await SearchWithConfiguredProviderAsync(query, options, http, ct).ConfigureAwait(false);
            return new
            {
                query,
                provider,
                results = hits
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new
            {
                query,
                provider,
                results = Array.Empty<object>(),
                error = ex.Message
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> ScrapeWebPageAsync(
        string argumentsJson,
        LocalToolOptions localOptions,
        HttpClient http,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;
        var action = ReadString(root, "action") ?? "scrape";
        if (!action.Equals("scrape", StringComparison.OrdinalIgnoreCase))
        {
            return SerializeToolResult(new
            {
                success = false,
                error = "Local BYOK web_fetch currently supports only action=scrape."
            });
        }

        var rawUrl = ReadString(root, "url");
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https"))
        {
            return SerializeToolResult(new
            {
                success = false,
                error = "A valid http/https url is required."
            });
        }

        var maxChars = localOptions.WebPageMaxCharacters > 0
            ? localOptions.WebPageMaxCharacters
            : MaxPageCharacters;
        if (root.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("maxCharacters", out var maxNode)
            && maxNode.ValueKind == JsonValueKind.Number
            && maxNode.TryGetInt32(out var parsed))
        {
            maxChars = Math.Clamp(parsed, 1000, 30000);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgentProvider.FixedUa);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.8");
        var html = await SendTextAsync(http, req, ct).ConfigureAwait(false);
        var title = ExtractTitle(html);
        var links = ExtractLinks(html, url).Take(20).ToArray();
        var text = HtmlToText(html);
        if (text.Length > maxChars)
            text = text[..maxChars] + "\n[content truncated]";

        return SerializeToolResult(new
        {
            success = true,
            source = "local_web_fetch",
            action = "scrape",
            url = url.ToString(),
            title,
            content = text,
            links
        });
    }

    private static async Task<string> SendTextAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<object>> SearchWithConfiguredProviderAsync(
        string query,
        LocalToolOptions options,
        HttpClient http,
        CancellationToken ct)
    {
        return NormalizeSearchProvider(options.SearchProvider) switch
        {
            "tavily" => await SearchTavilyAsync(query, options, http, ct).ConfigureAwait(false),
            "exa" => await SearchExaAsync(query, options, http, ct).ConfigureAwait(false),
            _ => await SearchDuckDuckGoAsync(query, options, http, ct).ConfigureAwait(false)
        };
    }

    private static async Task<IReadOnlyList<object>> SearchDuckDuckGoAsync(
        string query,
        LocalToolOptions options,
        HttpClient http,
        CancellationToken ct)
    {
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgentProvider.FixedUa);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        var html = await SendTextAsync(http, req, ct).ConfigureAwait(false);
        return ParseDuckDuckGoResults(html).Take(options.SearchMaxResults).ToArray();
    }

    private static async Task<IReadOnlyList<object>> SearchTavilyAsync(
        string query,
        LocalToolOptions options,
        HttpClient http,
        CancellationToken ct)
    {
        var apiKey = RequireApiKey(options, "Tavily");
        var endpoint = CombineEndpoint(options.SearchBaseUrl, "https://api.tavily.com", "search");
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        req.Content = JsonContent.Create(new
        {
            query,
            search_depth = "basic",
            max_results = options.SearchMaxResults,
            include_answer = false,
            include_raw_content = false
        });

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return Array.Empty<object>();

        return results.EnumerateArray()
            .Take(options.SearchMaxResults)
            .Select(result => new
            {
                title = ReadString(result, "title") ?? ReadString(result, "url") ?? "Untitled",
                url = ReadString(result, "url") ?? string.Empty,
                snippet = ReadString(result, "content") ?? string.Empty
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.url))
            .Cast<object>()
            .ToArray();
    }

    private static async Task<IReadOnlyList<object>> SearchExaAsync(
        string query,
        LocalToolOptions options,
        HttpClient http,
        CancellationToken ct)
    {
        var apiKey = RequireApiKey(options, "Exa");
        var endpoint = CombineEndpoint(options.SearchBaseUrl, "https://api.exa.ai", "search");
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Content = JsonContent.Create(new
        {
            query,
            numResults = options.SearchMaxResults,
            contents = new { text = true }
        });

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return Array.Empty<object>();

        return results.EnumerateArray()
            .Take(options.SearchMaxResults)
            .Select(result => new
            {
                title = ReadString(result, "title") ?? ReadString(result, "url") ?? "Untitled",
                url = ReadString(result, "url") ?? string.Empty,
                snippet = ReadString(result, "text") ?? string.Empty
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.url))
            .Cast<object>()
            .ToArray();
    }

    private static string RequireApiKey(LocalToolOptions options, string providerName)
    {
        if (!string.IsNullOrWhiteSpace(options.SearchApiKey))
            return options.SearchApiKey!;

        throw new InvalidOperationException($"{providerName} search requires an API key in Settings > Web Search.");
    }

    private static Uri CombineEndpoint(string? baseUrl, string defaultBaseUrl, string relative)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? defaultBaseUrl : baseUrl.Trim();
        if (!root.EndsWith("/", StringComparison.Ordinal)) root += "/";
        return new Uri(new Uri(root), relative);
    }

    private static string NormalizeSearchProvider(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
            ? "duckduckgo"
            : provider.Trim().ToLowerInvariant() switch
            {
                "tavily" => "tavily",
                "exa" => "exa",
                _ => "duckduckgo"
            };

    private static IReadOnlyList<string> ParseSearchQueries(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;
        var queries = new List<string>();

        if (root.TryGetProperty("queries", out var queryArray) && queryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in queryArray.EnumerateArray())
            {
                var query = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : ReadString(item, "query");
                if (!string.IsNullOrWhiteSpace(query))
                    queries.Add(query!.Trim());
                if (queries.Count >= MaxSearchQueries) break;
            }
        }

        if (queries.Count == 0)
        {
            var legacy = ReadString(root, "query");
            if (!string.IsNullOrWhiteSpace(legacy))
                queries.Add(legacy!.Trim());
        }

        if (queries.Count == 0)
            throw new InvalidOperationException("search_web requires queries[0].query or query.");

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSearchQueries).ToArray();
    }

    private static IReadOnlyList<object> ParseDuckDuckGoResults(string html)
    {
        var list = new List<object>();
        foreach (Match match in DuckResultRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            var title = CleanInlineText(match.Groups["title"].Value);
            var snippet = CleanInlineText(match.Groups["snippet"].Value);
            var url = NormalizeDuckDuckGoUrl(href);
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new { title, url, snippet });
        }
        return list;
    }

    private static string NormalizeDuckDuckGoUrl(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)
            && absolute.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = absolute.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in query)
            {
                var pieces = part.Split('=', 2);
                if (pieces.Length == 2 && pieces[0] == "uddg")
                    return Uri.UnescapeDataString(pieces[1].Replace('+', ' '));
            }
        }

        if (href.StartsWith("//", StringComparison.Ordinal))
            return "https:" + href;
        return href;
    }

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        return match.Success ? CleanInlineText(match.Groups["title"].Value) : string.Empty;
    }

    private static IReadOnlyList<object> ExtractLinks(string html, Uri baseUri)
    {
        var links = new List<object>();
        foreach (Match match in LinkRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (!Uri.TryCreate(baseUri, href, out var url)) continue;
            if (url.Scheme is not ("http" or "https")) continue;
            var label = CleanInlineText(match.Groups["label"].Value);
            if (string.IsNullOrWhiteSpace(label)) label = url.Host;
            links.Add(new { text = label, url = url.ToString() });
        }
        return links;
    }

    private static string HtmlToText(string html)
    {
        var text = ScriptStyleRegex().Replace(html, " ");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return DecodeUnicodeEscapes(WhitespaceRegex().Replace(text, " ").Trim());
    }

    private static string CleanInlineText(string value)
    {
        var text = TagRegex().Replace(value, " ");
        text = WebUtility.HtmlDecode(text);
        return DecodeUnicodeEscapes(WhitespaceRegex().Replace(text, " ").Trim());
    }

    private static string SerializeToolResult<T>(T value) =>
        JsonSerializer.Serialize(value, ToolResultJsonOptions);

    private static string DecodeUnicodeEscapes(string text)
    {
        if (string.IsNullOrEmpty(text)
            || (!text.Contains(@"\u", StringComparison.Ordinal)
                && !text.Contains(@"\U", StringComparison.Ordinal)))
        {
            return text;
        }

        return UnicodeEscapeRegex().Replace(text, match =>
        {
            var isLong = match.Groups["long"].Success;
            var hex = isLong ? match.Groups["long"].Value : match.Groups["short"].Value;
            try
            {
                var value = Convert.ToInt32(hex, 16);
                return isLong ? char.ConvertFromUtf32(value) : ((char)value).ToString();
            }
            catch (ArgumentException)
            {
                return match.Value;
            }
            catch (OverflowException)
            {
                return match.Value;
            }
            catch (FormatException)
            {
                return match.Value;
            }
        });
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"<(script|style|noscript)\b[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\\(?:u(?<short>[0-9a-fA-F]{4})|U(?<long>[0-9a-fA-F]{8}))", RegexOptions.CultureInvariant)]
    private static partial Regex UnicodeEscapeRegex();

    [GeneratedRegex("<title[^>]*>(?<title>[\\s\\S]*?)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<a[^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<label>[\\s\\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("<a[^>]+class=[\"'][^\"']*result__a[^\"']*[\"'][^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<title>[\\s\\S]*?)</a>[\\s\\S]*?(?:<a[^>]+class=[\"'][^\"']*result__snippet[^\"']*[\"'][^>]*>|<div[^>]+class=[\"'][^\"']*result__snippet[^\"']*[\"'][^>]*>)(?<snippet>[\\s\\S]*?)(?:</a>|</div>)", RegexOptions.IgnoreCase)]
    private static partial Regex DuckResultRegex();
}
