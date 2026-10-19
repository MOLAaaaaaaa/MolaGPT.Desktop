using System.Text.Json;
using MolaGPT.Core.Chat.LocalTools;
using MolaGPT.Core.Chat.Tools;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>Projects an unfinished Pi tail into the existing ChatChunk stream.</summary>
public static class PiSessionRecovery
{
    public static PiSessionRecoveryResult Recover(
        string sessionRoot,
        string conversationId,
        long persistedLastUserCreatedAtMs,
        string expectedUserText,
        LocalToolOptions? toolOptions = null)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot) || string.IsNullOrWhiteSpace(conversationId))
            return PiSessionRecoveryResult.Empty;

        return Recover(
            PiRuntime.ResolveSessionPath(sessionRoot, conversationId),
            persistedLastUserCreatedAtMs,
            expectedUserText,
            toolOptions);
    }

    public static PiSessionRecoveryResult Recover(
        string sessionPath,
        long persistedLastUserCreatedAtMs,
        string expectedUserText,
        LocalToolOptions? toolOptions = null)
    {
        if (!File.Exists(sessionPath))
            return PiSessionRecoveryResult.Empty;

        var branch = ActiveBranch(ReadEntries(sessionPath));
        var boundary = FindBoundary(branch, persistedLastUserCreatedAtMs, expectedUserText);
        if (boundary < 0) return PiSessionRecoveryResult.Empty;

        var chunks = new List<ChatChunk>();
        var pending = new Dictionary<string, PendingTool>(StringComparer.Ordinal);
        string? lastStopReason = null;

        for (var i = boundary + 1; i < branch.Count; i++)
        {
            if (branch[i].Message is not { } message) continue;
            var role = ReadString(message, "role");
            if (role == "user") break;
            if (role == "assistant")
            {
                lastStopReason = ReadString(message, "stopReason");
                ReadAssistant(message, pending, chunks, toolOptions);
            }
            else if (role == "toolResult")
            {
                ReadToolResult(message, pending, chunks, toolOptions);
            }
        }

        if (!chunks.Any(HasVisibleContent)) return PiSessionRecoveryResult.Empty;

        var hadPendingTools = pending.Count > 0;
        foreach (var (id, call) in pending)
        {
            chunks.Add(new ChatChunk(Tool: ToolDeltaBuilder.BuildToolDelta(
                id, call.Name, call.Arguments, toolOptions, "cancelled")));
        }

        return new PiSessionRecoveryResult(
            chunks,
            lastStopReason is not ("stop" or "length") || hadPendingTools);
    }

    private static bool HasVisibleContent(ChatChunk chunk) =>
        chunk.DeltaText is { Length: > 0 }
        || chunk.DeltaThinking is { Length: > 0 }
        || chunk.Tool is not null;

    private static void ReadAssistant(
        JsonElement message,
        IDictionary<string, PendingTool> pending,
        ICollection<ChatChunk> chunks,
        LocalToolOptions? toolOptions)
    {
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return;

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || ReadString(part, "type") is not { } type) continue;

            switch (type)
            {
                case "thinking" when ReadString(part, "thinking") is { Length: > 0 } thinking:
                    chunks.Add(new ChatChunk(DeltaThinking: thinking));
                    break;
                case "text" when ReadString(part, "text") is { Length: > 0 } text:
                    chunks.Add(new ChatChunk(DeltaText: text));
                    break;
                case "toolCall":
                {
                    var id = ReadString(part, "id");
                    if (string.IsNullOrWhiteSpace(id)) break;
                    var name = ReadString(part, "name") ?? "tool";
                    var arguments = part.TryGetProperty("arguments", out var args)
                        ? args.GetRawText() : "{}";
                    pending[id] = new PendingTool(name, arguments);
                    chunks.Add(new ChatChunk(Tool: ToolDeltaBuilder.BuildToolDelta(
                        id, name, arguments, toolOptions, "running")));
                    break;
                }
            }
        }
    }

    private static void ReadToolResult(
        JsonElement message,
        IDictionary<string, PendingTool> pending,
        ICollection<ChatChunk> chunks,
        LocalToolOptions? toolOptions)
    {
        var id = ReadString(message, "toolCallId");
        if (string.IsNullOrWhiteSpace(id)) return;

        pending.TryGetValue(id, out var call);
        var name = ReadString(message, "toolName") ?? call?.Name ?? "tool";
        var arguments = call?.Arguments ?? "{}";
        var failed = message.TryGetProperty("isError", out var error)
                     && error.ValueKind == JsonValueKind.True;
        var status = failed ? "failed" : "completed";
        var delta = ToolDeltaBuilder.BuildToolDelta(
            id, name, arguments, toolOptions, status, ResultText(message));
        chunks.Add(new ChatChunk(Tool: delta with { Status = status }));
        pending.Remove(id);
    }

    private static string? ResultText(JsonElement message)
    {
        if (message.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("output", out var output))
        {
            var value = output.ValueKind == JsonValueKind.String
                ? output.GetString() : output.GetRawText();
            if (value is not null) return FormatResult(value);
        }

        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return null;

        var parts = content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object)
            .Select(part => ReadString(part, "text"))
            .Where(text => text is not null);
        return FormatResult(string.Join("\n", parts));
    }

    private static string FormatResult(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            using var doc = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(doc.RootElement, DisplayJson);
        }
        catch (JsonException) { return value; }
    }

    private static List<PersistedEntry> ReadEntries(string path)
    {
        var entries = new List<PersistedEntry>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String) continue;

                JsonElement? message = null;
                if (root.TryGetProperty("message", out var value)
                    && value.ValueKind == JsonValueKind.Object)
                    message = value.Clone();

                entries.Add(new PersistedEntry(
                    id.GetString() ?? string.Empty,
                    ReadString(root, "parentId"),
                    message is { } m ? ReadLong(m, "timestamp") : ReadLong(root, "timestamp"),
                    message));
            }
            catch (JsonException) { }
        }

        return entries;
    }

    private static IReadOnlyList<PersistedEntry> ActiveBranch(IReadOnlyList<PersistedEntry> entries)
    {
        if (entries.Count == 0) return Array.Empty<PersistedEntry>();
        var byId = entries.Where(entry => entry.Id.Length > 0)
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var branch = new List<PersistedEntry>();
        var current = entries[^1];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current.Id))
        {
            branch.Add(current);
            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !byId.TryGetValue(current.ParentId, out current!)) break;
        }

        branch.Reverse();
        return branch;
    }

    private static int FindBoundary(
        IReadOnlyList<PersistedEntry> branch,
        long expectedTimestamp,
        string expectedText)
    {
        if (string.IsNullOrWhiteSpace(expectedText)) return -1;
        expectedText = expectedText.Trim();
        for (var i = 0; i < branch.Count; i++)
        {
            if (branch[i].Message is not { } message
                || ReadString(message, "role") != "user"
                || branch[i].Timestamp < expectedTimestamp) continue;
            var actual = MessageText(message).Trim();
            if (string.Equals(actual, expectedText, StringComparison.Ordinal)
                || (actual.StartsWith(expectedText, StringComparison.Ordinal)
                    && actual.Length > expectedText.Length
                    && actual.AsSpan(expectedText.Length).TrimStart().StartsWith("✝", StringComparison.Ordinal))) return i;
        }

        return -1;
    }

    private static string MessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Join("\n", content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object)
            .Select(part => ReadString(part, "text"))
            .Where(text => text is not null));
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static long ReadLong(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var result) ? result : 0;

    private sealed record PendingTool(string Name, string Arguments);
    private sealed record PersistedEntry(string Id, string? ParentId, long Timestamp, JsonElement? Message);

    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed record PiSessionRecoveryResult(IReadOnlyList<ChatChunk> Chunks, bool HasUnfinishedTurn)
{
    public static PiSessionRecoveryResult Empty { get; } = new(Array.Empty<ChatChunk>(), false);
}
