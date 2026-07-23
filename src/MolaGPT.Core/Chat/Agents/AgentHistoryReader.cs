using System.Text.Json;

namespace MolaGPT.Core.Chat.Agents;

/// <summary>
/// Reads the local on-disk session history of Claude Code and Codex so the
/// console can list and resume the user's recent real conversations.
///   - Claude Code: <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl</c>;
///     the first JSON line carries cwd/sessionId/gitBranch, the first user
///     message is used as the title.
///   - Codex: <c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c> whose first line
///     is a <c>session_meta</c> payload (id/cwd); titles come from
///     <c>~/.codex/session_index.jsonl</c> (id → thread_name) when present.
/// All parsing is best-effort: a malformed or partially-written file is skipped.
/// </summary>
public sealed partial class AgentHistoryReader
{
    private readonly string _home;

    public AgentHistoryReader(string? homeOverride = null)
        => _home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Enumerate recent sessions across both backends, newest first.</summary>
    /// <param name="max">Cap on total entries returned.</param>
    /// <param name="cwdFilter">When set, only sessions whose cwd matches (case-insensitive).</param>
    public async Task<IReadOnlyList<AgentHistoryEntry>> ListRecentAsync(
        int max = 40, string? cwdFilter = null, CancellationToken ct = default)
    {
        var entries = new List<AgentHistoryEntry>();
        try { entries.AddRange(await Task.Run(() => ReadClaude(ct), ct).ConfigureAwait(false)); } catch { }
        try { entries.AddRange(await Task.Run(() => ReadCodex(ct), ct).ConfigureAwait(false)); } catch { }

        IEnumerable<AgentHistoryEntry> q = entries;
        if (!string.IsNullOrWhiteSpace(cwdFilter))
            q = q.Where(e => PathEquals(e.WorkingDirectory, cwdFilter!));

        // One entry per (backend, sessionId) — Claude writes several sub-agent
        // transcripts under the same sessionId; keep the most recent.
        return q.OrderByDescending(e => e.LastModified)
            .GroupBy(e => $"{e.BackendId}|{e.SessionId}")
            .Select(g => g.First())
            .OrderByDescending(e => e.LastModified)
            .Take(max)
            .ToList();
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // CLAUDE_READER
    private IEnumerable<AgentHistoryEntry> ReadClaude(CancellationToken ct)
    {
        var root = Path.Combine(_home, ".claude", "projects");
        if (!Directory.Exists(root)) yield break;

        // Newest files first; cap how many we crack open for responsiveness.
        var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(120);

        foreach (var fi in files)
        {
            ct.ThrowIfCancellationRequested();
            string? cwd = null, sessionId = null, title = null, aiTitle = null;
            try
            {
                foreach (var line in ReadFirstLines(fi.FullName, 80))
                {
                    JsonElement root2;
                    try { using var doc = JsonDocument.Parse(line); root2 = doc.RootElement.Clone(); }
                    catch { continue; }

                    if (sessionId is null && root2.TryGetProperty("sessionId", out var sid))
                        sessionId = sid.GetString();
                    if (cwd is null && root2.TryGetProperty("cwd", out var c))
                        cwd = c.GetString();
                    if (root2.TryGetProperty("type", out var t))
                    {
                        var tt = t.GetString();
                        if (aiTitle is null && tt == "ai-title" && root2.TryGetProperty("aiTitle", out var at))
                            aiTitle = at.GetString();
                        if (title is null && tt == "user")
                            title = ExtractClaudeUserText(root2);
                    }

                    if (sessionId is not null && cwd is not null && aiTitle is not null) break;
                }
            }
            catch { continue; }

            if (sessionId is null) sessionId = Path.GetFileNameWithoutExtension(fi.Name);
            yield return new AgentHistoryEntry(
                ClaudeCodeBackend.BackendId,
                sessionId,
                cwd ?? "",
                CleanTitle(aiTitle ?? title) ?? "(无标题)",
                new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                fi.FullName);
        }
    }

    private static string? ExtractClaudeUserText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        string? text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => content.EnumerateArray()
                .Select(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("text", out var tx) ? tx.GetString() : null)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            _ => null
        };
        // Skip tool-result / command echoes that start with markup.
        if (text is null || text.StartsWith('<') || text.StartsWith("[")) return null;
        return text;
    }

    // CODEX_READER
    private IEnumerable<AgentHistoryEntry> ReadCodex(CancellationToken ct)
    {
        var codexHome = Path.Combine(_home, ".codex");
        var sessionsRoot = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessionsRoot)) yield break;

        // id -> thread_name, from the index when present.
        var titles = ReadCodexTitleIndex(Path.Combine(codexHome, "session_index.jsonl"));

        var files = Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(120);

        foreach (var fi in files)
        {
            ct.ThrowIfCancellationRequested();
            string? id = null, cwd = null, title = null;
            try
            {
                foreach (var line in ReadFirstLines(fi.FullName, 40))
                {
                    JsonElement r;
                    try { using var doc = JsonDocument.Parse(line); r = doc.RootElement.Clone(); }
                    catch { continue; }

                    if (r.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object)
                    {
                        if (id is null && pl.TryGetProperty("id", out var pid)) id = pid.GetString();
                        if (cwd is null && pl.TryGetProperty("cwd", out var pc)) cwd = pc.GetString();
                        // first user_message event → title
                        if (title is null && pl.TryGetProperty("type", out var pt) && pt.GetString() == "user_message"
                            && pl.TryGetProperty("message", out var pm))
                        {
                            var msg = pm.GetString();
                            if (!string.IsNullOrWhiteSpace(msg) && !msg.StartsWith('[') && !msg.StartsWith('<'))
                                title = msg;
                        }
                    }
                    if (id is not null && cwd is not null && title is not null) break;
                }
            }
            catch { continue; }
            if (id is null) continue;

            // Codex owns the semantic thread title.  The first user message is
            // only a fallback for old/missing index entries; preferring it here
            // made every bridge session ignore Codex's generated thread_name.
            if (titles.TryGetValue(id, out var indexTitle)) title = indexTitle;
            yield return new AgentHistoryEntry(
                CodexBackend.BackendId,
                id,
                cwd ?? "",
                CleanTitle(title) ?? "(无标题)",
                new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                fi.FullName);
        }
    }

    private static Dictionary<string, string> ReadCodexTitleIndex(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;
        try
        {
            foreach (var line in ReadAllLinesShared(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var id = r.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var name = r.TryGetProperty("thread_name", out var n) ? n.GetString() : null;
                    if (id is not null && !string.IsNullOrWhiteSpace(name)) map[id] = name!;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    /// <summary>Read up to <paramref name="max"/> lines without loading the whole file.</summary>
    private static IEnumerable<string> ReadFirstLines(string path, int max)
    {
        using var reader = OpenSharedReader(path);
        for (int i = 0; i < max; i++)
        {
            var line = reader.ReadLine();
            if (line is null) yield break;
            if (line.Length > 0) yield return line;
        }
    }

    /// <summary>Open a session file for reading even while another process holds it
    /// open for writing. Codex Desktop / Claude keep their <em>active</em> rollout
    /// files locked; the default <c>new StreamReader(path)</c> / <c>File.ReadLines</c>
    /// request a share mode the writer denies, throwing IOException — which made the
    /// history reader silently skip the very sessions the user is currently using.
    /// <c>FileShare.ReadWrite</c> lets us read alongside the live writer.</summary>
    internal static StreamReader OpenSharedReader(string path)
        => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

    /// <summary>Enumerate all lines of a file with a writer-tolerant share mode
    /// (see <see cref="OpenSharedReader"/>). Drop-in for <c>File.ReadLines</c>.</summary>
    internal static IEnumerable<string> ReadAllLinesShared(string path)
    {
        using var reader = OpenSharedReader(path);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    private static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim().ReplaceLineEndings(" ");
        return t.Length > 48 ? t[..48] + "…" : t;
    }
}
