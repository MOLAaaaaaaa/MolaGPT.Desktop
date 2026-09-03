using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools.ImageGeneration;
using MolaGPT.Core.Chat.Tools.Mcp;
using MolaGPT.Core.Chat.Tools.PythonExecution;
using MolaGPT.Core.Chat.Tools.Vision;

namespace MolaGPT.Core.Chat.Tools;

/// <summary>
/// Turns a tool call into the card the chat UI renders.
///
/// Lifted out of the streaming providers when the agent loop moved into Pi. It
/// was already shared between the two engines then; with the direct providers
/// gone it is simply the one description of what a tool call looks like, and it
/// belongs next to the tools rather than inside a transport.
/// </summary>
public static class ToolDeltaBuilder
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Build the tool card from raw values. Shared with the Pi path so a tool call
    /// looks the same whichever engine ran it — the labels, the summary, the
    /// duration and exit code lifted out of the result — rather than each path
    /// growing its own approximation of the card.
    ///
    /// The result goes in whole. It used to be cut to 1600 characters, which lost
    /// the tail of every search and every directory listing permanently, since
    /// the cut copy is what gets persisted with the conversation. How much of it
    /// a card shows at once is a layout question, and it is answered in the card.
    /// </summary>
    public static ToolCallDelta BuildToolDelta(
        string id,
        string name,
        string args,
        LocalToolOptions? options,
        string status,
        string? resultJson = null)
    {
        return new ToolCallDelta(
            id,
            name,
            status,
            LocalToolPendingLabel(name),
            BuildToolSummary(name, args),
            BuildToolDetail(name, args, options, status, resultJson),
            PrettyJson(args),
            resultJson is null ? null : PrettyJson(resultJson),
            name == "search_web" ? SearchProviderLabel(options?.SearchProvider) : null);
    }

    public static bool IsToolError(string resultJson)
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
            if (name == VisionProxyTool.ToolName)
                return ReadString(root, "query") ?? "查看图片";
            if (name == ImageAnalysisTool.ToolName)
                return ReadString(root, "path") ?? "分析图片";
            if (name == ImageGenerationTool.ToolName)
                return ReadString(root, "prompt") ?? "生成图片";
            if (name == PythonExecutionTool.ToolName)
                return ReadString(root, "description")
                       ?? FirstNonEmptyLine(ReadString(root, "code"))
                       ?? "执行 Python";
            if (McpToolName.TryDecode(name, out var server, out var tool))
                return $"{server} / {tool}";
        }
        catch (JsonException)
        {
            if (name == PythonExecutionTool.ToolName)
                return "正在生成 Python 代码";
        }

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
        if (name == VisionProxyTool.ToolName)
            return "通过视觉模型读取图片";
        if (name == ImageAnalysisTool.ToolName)
            return "通过视觉模型分析工作目录中的图片";
        if (name == ImageGenerationTool.ToolName)
            return "通过图像生成 API 创建图片";
        if (name == PythonExecutionTool.ToolName)
        {
            if (string.Equals(status, "preparing", StringComparison.OrdinalIgnoreCase))
                return "正在生成 Python 代码";
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
                return "本地 Python 执行环境";

            var pythonResult = ReadPythonResultMeta(resultJson);
            if (pythonResult is not null)
                return pythonResult;

            return "本地 Python 执行环境";
        }
        if (McpToolName.TryDecode(name, out var server, out _))
            return $"MCP: {server}";
        return null;
    }

    private static string? FirstNonEmptyLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var line = value
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (string.IsNullOrWhiteSpace(line)) return null;
        return line!.Length <= 96 ? line : line[..96] + "...";
    }

    private static string? ReadPythonResultMeta(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            var parts = new List<string>();
            if (root.TryGetProperty("permission", out var permission)
                && permission.ValueKind == JsonValueKind.Object
                && ReadString(permission, "mode") is { Length: > 0 } mode)
            {
                parts.Add(mode switch
                {
                    "Approval" => "审批权限",
                    "FullAccess" => "完全权限",
                    "Rules" => "规则模式",
                    _ => mode
                });
            }
            if (root.TryGetProperty("duration_ms", out var duration)
                && duration.ValueKind == JsonValueKind.Number
                && duration.TryGetInt64(out var durationMs))
            {
                parts.Add($"{durationMs} ms");
            }
            if (root.TryGetProperty("exit_code", out var exitCode)
                && exitCode.ValueKind == JsonValueKind.Number
                && exitCode.TryGetInt32(out var code))
            {
                parts.Add($"退出码 {code}");
            }
            if (root.TryGetProperty("artifacts", out var artifacts)
                && artifacts.ValueKind == JsonValueKind.Array)
            {
                var count = artifacts.GetArrayLength();
                if (count > 0) parts.Add($"{count} 个文件");
            }
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
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
            return JsonSerializer.Serialize(doc.RootElement, DisplayJsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
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
        "read_file" => "读取文件",
        "glob_files" => "查找文件",
        "grep_files" => "搜索内容",
        PythonExecutionTool.ToolName => "执行 Python",
        VisionProxyTool.ToolName => "查看图片",
        ImageAnalysisTool.ToolName => "图片分析",
        ImageGenerationTool.ToolName => "生成图片",
        _ => "调用工具"
    };

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var n) ? n : null;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
