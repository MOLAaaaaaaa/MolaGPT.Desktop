using System.Text.Json;

using MemorySections = MolaGPT.Core.Personalization.MemorySections;

namespace MolaGPT.Core.Memory;

/// <summary>
/// The two tools the model gets. Deliberately two and not six: one way to look
/// things up, one way to change something, with the destructive variants folded
/// into an <c>op</c> enum rather than spread across separate names.
///
/// There is no whole-file write. Cherry Studio's memory tool takes the entire
/// markdown body on every update; with a file the user edits by hand, having the
/// model rewrite it wholesale is not a risk worth the convenience.
/// </summary>
public static class MemoryTools
{
    public const string RecallToolName = "recall";
    public const string WriteToolName = "memory_write";

    public static object BuildRecallDefinition() => new
    {
        type = "function",
        function = new
        {
            name = RecallToolName,
            description =
                "检索本机记忆和历史对话。需要确认过往信息时使用，不要凭印象回答。"
                + "结果仅用于本轮，不会自动写入长期记忆。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "检索内容，支持短中文词。" },
                    scope = new
                    {
                        type = "string",
                        @enum = new[] { "memory", "chats", "both" },
                        description = "memory=仅记忆；chats=仅历史对话；both=两者（默认）。"
                    },
                    limit = new { type = "integer", description = "返回条数，默认 3，最多 5。" }
                },
                required = new[] { "query" }
            }
        }
    };

    public static object BuildWriteDefinition() => new
    {
        type = "function",
        function = new
        {
            name = WriteToolName,
            description =
                "按需维护长期记忆，普通对话不必调用。仅保存有长期价值的用户自述或明确记忆请求；"
                + "不保存一次操作、问题、临时授权、报告数据和执行状态。先查重，同一主题优先更新已有内容。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    op = new
                    {
                        type = "string",
                        @enum = new[] { "add", "replace", "delete" },
                        description = "add=新增；replace=替换；delete=删除。"
                    },
                    section = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            MemorySections.IdentityWire,
                            MemorySections.PreferenceWire,
                            MemorySections.ProjectWire,
                            MemorySections.ContextWire,
                            MemorySections.ProhibitionWire
                        },
                        description = "记忆分类，add 时必填。"
                    },
                    text = new { type = "string", description = "记忆内容，add/replace 时必填。使用完整的第三人称陈述句。" },
                    target = new { type = "string", description = "目标记忆的原文或 id，replace/delete 时必填。" },
                    topic = new { type = "string", description = "所属主题名称，优先复用已有主题，如交流偏好、跑步、MolaGPT。" },
                    group = new { type = "string", @enum = MemoryTopics.Groups },
                    summary = new { type = "string", description = "新主题的一句话摘要，最多 240 字。" },
                    quote = new
                    {
                        type = "string",
                        description = "用户本轮原话，必须逐字引用；delete 时还须明确表达否认或删除意图。"
                    },
                    profile_key = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            MemoryProfile.PreferredName,
                            MemoryProfile.Occupation,
                            MemoryProfile.Location,
                            MemoryProfile.Language
                        },
                        description = "可选，对应的个人资料字段。"
                    }
                },
                required = new[] { "op", "quote" }
            }
        }
    };

    public static readonly JsonSerializerOptions ResultJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Ok(object value) => JsonSerializer.Serialize(value, ResultJson);

    public static string Error(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message }, ResultJson);

    public static MemoryToolArgs ParseArgs(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new MemoryToolArgs();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new MemoryToolArgs();
            return new MemoryToolArgs
            {
                Op = Str(root, "op"),
                Section = Str(root, "section"),
                Text = Str(root, "text"),
                Target = Str(root, "target"),
                Quote = Str(root, "quote"),
                ProfileKey = Str(root, "profile_key"),
                Topic = Str(root, "topic"),
                Group = Str(root, "group"),
                Summary = Str(root, "summary"),
                Query = Str(root, "query"),
                Scope = Str(root, "scope"),
                Limit = Int(root, "limit")
            };
        }
        catch (JsonException)
        {
            return new MemoryToolArgs();
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

public sealed record MemoryToolArgs
{
    public string? Op { get; init; }
    public string? Section { get; init; }
    public string? Text { get; init; }
    public string? Target { get; init; }
    public string? Quote { get; init; }
    public string? ProfileKey { get; init; }
    public string? Topic { get; init; }
    public string? Group { get; init; }
    public string? Summary { get; init; }
    public string? Query { get; init; }
    public string? Scope { get; init; }
    public int? Limit { get; init; }
}

/// <summary>
/// What <see cref="MolaGPT.Core.Chat.Tools.ChatToolHost"/> calls when the model
/// uses a memory tool. Implemented outside Core because the recall half needs
/// the SQLite index, and Core does not depend on the storage assembly — the same
/// split Mobile uses for its local tool handler.
/// </summary>
public interface IMemoryToolBackend
{
    /// <summary>
    /// Whether the call is allowed at all is decided by the caller from this
    /// turn's <c>LocalToolOptions</c>, not here: the switches include a
    /// per-conversation override, and the turn is the only place that knows
    /// which conversation it belongs to.
    /// </summary>
    Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        string? conversationId,
        string? currentUserMessage,
        CancellationToken ct);
}
