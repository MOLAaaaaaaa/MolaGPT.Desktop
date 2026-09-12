using System.Text.Json.Nodes;

namespace MolaGPT.Core.Models;

public sealed record RolePromptTrace(string Api, IReadOnlyList<RolePromptMessage> Messages, DateTimeOffset CreatedAt)
{
    public static RolePromptTrace Read(string api, string body)
    {
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new InvalidDataException("请求内容为空。");
        var messages = new List<RolePromptMessage>();
        var system = root["system"] ?? root["systemInstruction"] ?? root["system_instruction"];
        if (system is not null) messages.Add(new("system", Text(system)));
        var items = root["messages"] ?? root["input"] ?? root["contents"];
        if (items is JsonArray array)
        {
            foreach (var node in array.OfType<JsonObject>())
            {
                var role = node["role"]?.GetValue<string>();
                var type = node["type"]?.GetValue<string>();
                if (role is null && type is "function_call" or "function_call_output")
                    role = type == "function_call" ? "assistant" : "tool";
                if (role is null) continue;
                var text = Text(node["content"] ?? node["parts"] ?? node["output"]);
                if (node["tool_calls"] is JsonArray calls)
                    text += "\n" + string.Join("\n", calls.OfType<JsonObject>().Select(call => "[工具调用：" + call["function"]?["name"]?.GetValue<string>() + "]"));
                if (type == "function_call") text = "[工具调用：" + node["name"]?.GetValue<string>() + "]";
                messages.Add(new(role == "model" ? "assistant" : role, text));
            }
        }
        else if (items is not null) messages.Add(new("user", Text(items)));
        return new(api, messages, DateTimeOffset.UtcNow);
    }

    private static string Text(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return text;
        if (value is JsonArray array) return string.Join("\n", array.Select(Text).Where(part => part.Length > 0));
        if (value is not JsonObject obj) return "";
        if (obj["text"] is { } literal) return Text(literal);
        if (obj["parts"] is { } parts) return Text(parts);
        if (obj["type"]?.GetValue<string>() is "image" or "input_image" or "image_url"
            || obj.ContainsKey("inlineData") || obj.ContainsKey("inline_data") || obj.ContainsKey("fileData")) return "[图片]";
        if (obj["type"]?.GetValue<string>() == "tool_use") return "[工具调用：" + obj["name"]?.GetValue<string>() + "]";
        if (obj["functionCall"] is JsonObject call) return "[工具调用：" + call["name"]?.GetValue<string>() + "]";
        if (obj["functionResponse"] is JsonObject response) return "[工具结果：" + response["name"]?.GetValue<string>() + "]";
        if (obj["content"] is { } content) return Text(content);
        return "";
    }
}
