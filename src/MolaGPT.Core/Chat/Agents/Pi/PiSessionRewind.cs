using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents.Pi;

/// <summary>
/// Where a Pi transcript has to be cut to undo the newest turn. Split out from
/// <see cref="PiWorkProvider"/> because getting this wrong silently corrupts a
/// conversation's memory, and checking it should not require a Node process.
///
/// Pi's session file is JSONL — one entry per line, appended, each carrying its
/// own <c>id</c> and <c>parentId</c>. A turn is the run of lines starting at a
/// <c>message</c> whose role is <c>user</c> and continuing through everything the
/// agent produced for it (thinking, tool calls, tool results, the reply) plus any
/// bookkeeping entries alongside. So undoing the newest turn is a truncation, and
/// the surviving parent chain stays intact because only leaves are removed.
/// </summary>
public static class PiSessionRewind
{
    /// <summary>
    /// How many leading lines to keep in order to drop the newest user turn and
    /// everything after it. Returns -1 when there is no user turn to drop, which
    /// is the caller's signal to leave the file alone.
    /// </summary>
    public static int KeepCountBeforeLastUserTurn(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
            if (IsUserMessage(lines[i]))
                return i;

        return -1;
    }

    private static bool IsUserMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "message")
            {
                return false;
            }

            return root.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.Object
                   && message.TryGetProperty("role", out var role)
                   && role.ValueKind == JsonValueKind.String
                   && role.GetString() == "user";
        }
        catch (JsonException)
        {
            // A half-written trailing line is data, not a crash: treat it as
            // "not a user turn" and keep scanning backwards.
            return false;
        }
    }
}
