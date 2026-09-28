using System.Text.Json;
using System.Text.Json.Nodes;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Where to cut a Pi transcript that is being copied into a new one — a sub-agent
/// forked from its parent, or a branch taken into a conversation of its own.
///
/// The copy keeps a prefix of the file byte for byte. That is the whole point: a
/// history identical to the original's is a prompt prefix the provider has already
/// cached. See <see cref="PiSessionRewind"/> for the file's shape.
/// </summary>
public static class PiSessionFork
{
    /// <summary>
    /// How many leading lines to keep so that no assistant message is left calling
    /// a tool whose result is missing — the state of a transcript copied while its
    /// agent is still inside a turn. Everything from that message on is dropped.
    /// </summary>
    public static int KeepCountWithoutOpenToolCalls(IReadOnlyList<string> lines)
    {
        var entries = lines.Select(Parse).ToList();
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Role != "assistant") continue;
            if (entry.ToolCallIds.Count == 0) return lines.Count;

            var answered = entries
                .Skip(i + 1)
                .Where(e => e.Role == "toolResult" && e.ToolCallId is not null)
                .Select(e => e.ToolCallId!)
                .ToHashSet(StringComparer.Ordinal);
            return entry.ToolCallIds.All(answered.Contains) ? lines.Count : i;
        }
        return lines.Count;
    }

    public static int CountUserMessages(IReadOnlyList<string> lines) =>
        lines.Count(line => Parse(line).Role == "user");

    /// <summary>
    /// How many leading lines hold the first <paramref name="userMessages"/> user
    /// turns and everything answered to them: the cut falls just before the next
    /// user message. -1 when the file has fewer turns than asked for.
    /// </summary>
    public static int KeepCountThroughUserTurns(IReadOnlyList<string> lines, int userMessages)
    {
        var seen = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (Parse(lines[i]).Role != "user") continue;
            if (seen == userMessages) return i;
            seen++;
        }
        return seen == userMessages ? lines.Count : -1;
    }

    /// <summary>The session header with a fresh id, so two files never claim to be
    /// the same session.</summary>
    public static string WithNewSessionId(string headerLine)
    {
        try
        {
            if (JsonNode.Parse(headerLine) is JsonObject header
                && header["type"]?.GetValue<string>() == "session")
            {
                header["id"] = Guid.NewGuid().ToString();
                return header.ToJsonString();
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return headerLine;
    }

    private sealed record Entry(string? Role, string? ToolCallId, IReadOnlyList<string> ToolCallIds);

    private static readonly Entry Other = new(null, null, Array.Empty<string>());

    private static Entry Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Other;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "message"
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object)
                return Other;

            var role = message.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            var toolCallId = message.TryGetProperty("toolCallId", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            var calls = new List<string>();
            if (role == "assistant"
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("type", out var partType)
                        && partType.GetString() == "toolCall"
                        && part.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String)
                        calls.Add(id.GetString()!);
                }
            }
            return new Entry(role, toolCallId, calls);
        }
        catch (JsonException)
        {
            return Other;
        }
    }
}
