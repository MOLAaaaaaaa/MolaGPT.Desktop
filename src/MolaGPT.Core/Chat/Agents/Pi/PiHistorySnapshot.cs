using System.Text.Json.Nodes;
using MolaGPT.Core.Models;

namespace MolaGPT.Core.Chat.Agents.Pi;

public static class PiHistorySnapshot
{
    public static string Create(string workingDirectory, string api, string model, IReadOnlyList<ChatMessage> messages)
    {
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string>
        {
            new JsonObject
            {
                ["type"] = "session", ["version"] = 3,
                ["id"] = Guid.NewGuid().ToString(), ["timestamp"] = now.ToString("O"),
                ["cwd"] = workingDirectory
            }.ToJsonString()
        };
        string? parent = null;
        foreach (var source in messages.Where(message => message.Role is ChatMessage.RoleUser or ChatMessage.RoleAssistant))
        {
            if (source.Role == ChatMessage.RoleAssistant && string.IsNullOrWhiteSpace(source.AsText())) continue;
            var content = new JsonArray();
            if (source.Content is JsonArray parts)
            {
                foreach (var part in parts)
                    if (part?["type"]?.GetValue<string>() == "text")
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = part["text"]!.GetValue<string>() });
            }
            else content.Add(new JsonObject { ["type"] = "text", ["text"] = source.AsText() });
            if (source.Attachments is { } attachments)
                foreach (var attachment in attachments.Where(attachment => attachment.Kind == AttachmentKind.Image))
                    content.Add(new JsonObject
                    {
                        ["type"] = "image", ["mimeType"] = attachment.MimeType,
                        ["data"] = Convert.ToBase64String(attachment.Bytes)
                    });
            var message = new JsonObject
            {
                ["role"] = source.Role, ["content"] = content, ["timestamp"] = now.ToUnixTimeMilliseconds()
            };
            if (source.Role == ChatMessage.RoleAssistant)
            {
                message["api"] = api;
                message["provider"] = PiWorkProvider.SidecarProviderId;
                message["model"] = model;
                message["stopReason"] = "stop";
                message["usage"] = new JsonObject
                {
                    ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["totalTokens"] = 0,
                    ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 }
                };
            }
            var id = Guid.NewGuid().ToString("N");
            lines.Add(new JsonObject
            {
                ["type"] = "message", ["id"] = id, ["parentId"] = parent,
                ["timestamp"] = now.ToString("O"), ["message"] = message
            }.ToJsonString());
            parent = id;
        }
        return string.Join('\n', lines) + "\n";
    }
}
