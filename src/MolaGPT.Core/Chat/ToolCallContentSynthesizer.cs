using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat;

/// <summary>
/// Converts OpenAI tool_call deltas into MolaGPT custom markup blocks that
/// the markdown layer can render as tool progress cards.
/// </summary>
public sealed class ToolCallContentSynthesizer
{
    private sealed class State
    {
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
        public bool UiStarted { get; set; }
        public bool UiClosed { get; set; }
        public int EmittedCodeLength { get; set; }
    }

    private readonly Dictionary<int, State> _toolCalls = new();

    public string? HandleToolCalls(JsonElement delta)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
            return null;

        var output = new StringBuilder();
        var handled = false;

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            handled = true;
            var idx = ReadInt(toolCall, "index") ?? 0;
            var state = EnsureState(idx);

            if (toolCall.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                var name = ReadString(fn, "name");
                if (!string.IsNullOrWhiteSpace(name)) state.Name = name!;

                var args = ReadString(fn, "arguments");
                if (!string.IsNullOrEmpty(args)) state.Arguments.Append(args);
            }

            EnsureToolUi(state, output);
            MaybeAppendPythonCode(state, output);
        }

        return handled ? output.ToString() : null;
    }

    public string FinalizeOpenBlocks()
    {
        var output = new StringBuilder();
        foreach (var state in _toolCalls.Values)
        {
            if (!state.UiStarted || state.UiClosed) continue;

            if (state.Name == "execute_python_code")
            {
                MaybeAppendPythonCode(state, output);
                output.Append("\n```\n\n</DSanalysis>\n");
            }
            else
            {
                output.Append(DecodeToolArgumentText(state.Arguments.ToString()));
                output.Append("\n```\n\n</DSanalysis>\n");
            }

            state.UiClosed = true;
        }

        return output.ToString();
    }

    public bool HasOpenBlocks => _toolCalls.Values.Any(s => s.UiStarted && !s.UiClosed);

    private State EnsureState(int idx)
    {
        if (_toolCalls.TryGetValue(idx, out var state)) return state;
        state = new State();
        _toolCalls[idx] = state;
        return state;
    }

    private static void EnsureToolUi(State state, StringBuilder output)
    {
        if (state.UiStarted || state.UiClosed || string.IsNullOrWhiteSpace(state.Name)) return;

        if (state.Name == "execute_python_code")
        {
            output.Append("\n\n<blockquote class=\"tool-status analyzing\"><p>正在执行 Python</p></blockquote>\n\n<DSanalysis data-tool-type=\"python\">\n\n**代码：**\n\n```python\n");
        }
        else
        {
            output.Append("\n\n<blockquote class=\"tool-status analyzing\"><p>");
            output.Append(GetToolStatusLabel(state.Name));
            output.Append("</p></blockquote>\n\n<DSanalysis data-tool-type=\"tool-call\">\n\n**工具：** `");
            output.Append(state.Name);
            output.Append("`\n\n**参数：**\n\n```json\n");
        }

        state.UiStarted = true;
    }

    private static void MaybeAppendPythonCode(State state, StringBuilder output)
    {
        if (!state.UiStarted || state.UiClosed || state.Name != "execute_python_code") return;
        var decodedCode = TryExtractPythonCode(state.Arguments.ToString());
        if (string.IsNullOrEmpty(decodedCode)) return;

        if (decodedCode.Length <= state.EmittedCodeLength) return;
        output.Append(decodedCode.AsSpan(state.EmittedCodeLength));
        state.EmittedCodeLength = decodedCode.Length;
    }

    private static string GetToolStatusLabel(string toolName) => toolName switch
    {
        "execute_python_code" => "正在执行 Python",
        "search_web" => "正在联网搜索",
        // BYOK 使用 web_fetch；代理服务端历史上发的是 steel_browser，两者都映射到同一个文案
        "web_fetch" or "steel_browser" => "正在访问互联网",
        "analyze_sandbox_image" => "正在分析图片",
        "draw_with_canvas" => "正在绘制",
        _ => !string.IsNullOrWhiteSpace(toolName) ? $"正在调用 {toolName}" : "正在调用工具"
    };

    private static string DecodeToolArgumentText(string rawArgs)
    {
        if (string.IsNullOrEmpty(rawArgs)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(rawArgs);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return rawArgs
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
    }

    private static string TryExtractPythonCode(string rawArgs)
    {
        if (string.IsNullOrEmpty(rawArgs)) return string.Empty;
        var match = Regex.Match(rawArgs, "\"code\"\\s*:\\s*\"([\\s\\S]*)");
        if (!match.Success) return string.Empty;

        var code = Regex.Replace(match.Groups[1].Value, "\"\\s*}?$", string.Empty);
        if (code.EndsWith("\\", StringComparison.Ordinal)) code = code[..^1];

        return code
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
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
}
