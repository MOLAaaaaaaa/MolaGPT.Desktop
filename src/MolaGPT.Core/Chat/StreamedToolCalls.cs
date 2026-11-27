using System.Text;
using System.Text.Json;

namespace MolaGPT.Core.Chat;

/// <summary>Accumulates the argument stream for the official relay's chat-completions tool calls.</summary>
internal sealed class StreamedToolCalls
{
    private sealed class Call(string id)
    {
        public string Id { get; } = id;
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
        public bool Announced { get; set; }
    }

    private readonly Dictionary<int, Call> _calls = new();
    private int _round;

    public void Append(JsonElement delta, ref List<ToolCallDelta>? updates)
    {
        if (!delta.TryGetProperty("tool_calls", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return;

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("function", out var function)
                || function.ValueKind != JsonValueKind.Object)
                continue;

            var index = tool.TryGetProperty("index", out var n) && n.ValueKind == JsonValueKind.Number
                && n.TryGetInt32(out var i) && i >= 0 ? i : 0;
            if (!_calls.TryGetValue(index, out var call))
            {
                var id = Text(tool, "id");
                call = new Call(string.IsNullOrWhiteSpace(id) ? $"stream-tool-{_round}-{index}" : id);
                _calls.Add(index, call);
            }
            if (Text(function, "name") is { Length: > 0 } name) call.Name = name;
            call.Arguments.Append(Text(function, "arguments"));
            if (call.Name is null || call.Announced) continue;

            call.Announced = true;
            (updates ??= []).Add(new ToolCallDelta(call.Id, call.Name, "running", Label: "工具调用参数",
                Origin: ToolCallOrigin.Arguments));
        }
    }

    public void Finish(ref List<ToolCallDelta>? updates)
    {
        if (_calls.Count == 0) return;
        foreach (var call in _calls.Values)
        {
            if (call.Name is null) continue;
            (updates ??= []).Add(new ToolCallDelta(call.Id, call.Name, "completed",
                Label: "工具调用参数", ArgumentsJson: call.Arguments.Length > 0 ? call.Arguments.ToString() : null,
                Origin: ToolCallOrigin.Arguments));
        }
        _calls.Clear();
        _round++;
    }

    private static string? Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
