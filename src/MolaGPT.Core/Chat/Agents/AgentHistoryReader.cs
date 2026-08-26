using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
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

    /// <summary>
    /// Memo of what each transcript file said, keyed by path and invalidated by
    /// (last write time, length).
    ///
    /// Periodic relay and status refreshes inspect the same mostly unchanged
    /// history. Without this memo they repeatedly parse the same transcripts.
    /// Only the transcript the user is actively writing to misses the memo, and
    /// re-reading exactly that one is the point.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedFile<ClaudeFacts>> _claudeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedFile<CodexFacts>> _codexCache = new(StringComparer.OrdinalIgnoreCase);
    private CachedFile<Dictionary<string, string>>? _codexTitleIndex;

    /// <summary>Bound on cache growth. Each scan looks at 120 files per backend,
    /// so this only trips after a lot of session churn in one process lifetime.</summary>
    private const int MaxCachedFiles = 2000;

    /// <summary>A file's parsed metadata plus the stamp that validates it.</summary>
    private readonly record struct CachedFile<T>(long WriteTicks, long Length, T Value)
    {
        public bool Matches(FileInfo file) => WriteTicks == file.LastWriteTimeUtc.Ticks && Length == file.Length;
    }

    private readonly record struct ClaudeFacts(string SessionId, string? Cwd, string? Title);

    /// <summary>Codex facts deliberately exclude the index-supplied thread name:
    /// that lives in a different file with its own lifetime, and is re-applied
    /// on every scan so a rename shows up without invalidating the transcript.</summary>
    private readonly record struct CodexFacts(string? Id, string? Cwd, string? FallbackTitle);

    public AgentHistoryReader(string? homeOverride = null)
        => _home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Enumerate recent sessions across both backends, newest first.</summary>
    /// <param name="max">Cap on total entries returned.</param>
    /// <param name="cwdFilter">When set, only sessions whose cwd matches (case-insensitive).</param>
    /// <param name="maxStaleness">
    /// How old a previous scan may be and still be reused. Zero — the default,
    /// and what every on-demand caller uses — always walks the disk. Callers on
    /// a timer pass a budget instead: they are polling for change, and a
    /// <see cref="FileSystemWatcher"/> on both history roots already tells us
    /// when a change happened, so re-walking a tree that provably has not moved
    /// is pure cost. A reused scan is only ever served when the watchers are
    /// live and have reported nothing since.
    /// </param>
    public async Task<IReadOnlyList<AgentHistoryEntry>> ListRecentAsync(
        int max = 40, string? cwdFilter = null, CancellationToken ct = default,
        TimeSpan maxStaleness = default)
    {
        IEnumerable<AgentHistoryEntry> q = await ScanAsync(maxStaleness, ct).ConfigureAwait(false);
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

    // --- scan reuse ---------------------------------------------------------
    // The per-file memo above stops us re-parsing files that have not changed;
    // this stops us re-walking the directory trees that hold them. On a real
    // history (a few hundred transcripts across ~/.claude/projects and the
    // ~/.codex/sessions YYYY/MM/DD tree) the walk is still avoidable work.

    private readonly object _scanGate = new();
    private readonly System.Diagnostics.Stopwatch _scanClock = System.Diagnostics.Stopwatch.StartNew();
    private IReadOnlyList<AgentHistoryEntry>? _lastScan;
    private TimeSpan _lastScanAt;
    private int _scanDirty = 1;
    private List<FileSystemWatcher>? _watchers;
    private bool _watchersUnavailable;

    private async Task<IReadOnlyList<AgentHistoryEntry>> ScanAsync(TimeSpan maxStaleness, CancellationToken ct)
    {
        if (maxStaleness > TimeSpan.Zero)
        {
            EnsureWatchers();
            lock (_scanGate)
            {
                if (!_watchersUnavailable
                    && Volatile.Read(ref _scanDirty) == 0
                    && _lastScan is { } cached
                    && _scanClock.Elapsed - _lastScanAt <= maxStaleness)
                {
                    return cached;
                }
            }
        }

        // Cleared before the walk, not after: a write that lands mid-scan must
        // leave the flag dirty so the next caller rescans, rather than being
        // swallowed by the result we are about to publish.
        Volatile.Write(ref _scanDirty, 0);

        var entries = new List<AgentHistoryEntry>();
        try { entries.AddRange(await Task.Run(() => ReadClaude(ct), ct).ConfigureAwait(false)); } catch { }
        try { entries.AddRange(await Task.Run(() => ReadCodex(ct), ct).ConfigureAwait(false)); } catch { }

        lock (_scanGate)
        {
            _lastScan = entries;
            _lastScanAt = _scanClock.Elapsed;
        }
        return entries;
    }

    /// <summary>Watch every history root that currently exists. A backend that
    /// is not installed must not disable scan reuse for the other backend.</summary>
    private void EnsureWatchers()
    {
        lock (_scanGate)
        {
            if (_watchers is not null || _watchersUnavailable) return;

            var codexHome = Path.Combine(_home, ".codex");
            var roots = new (string Path, string Filter, bool Recursive)[]
            {
                (Path.Combine(_home, ".claude", "projects"), "*.jsonl", true),
                (Path.Combine(codexHome, "sessions"), "rollout-*.jsonl", true),
                // Codex renames threads in the index, not in the transcript.
                (codexHome, "session_index.jsonl", false),
            };

            var created = new List<FileSystemWatcher>();
            try
            {
                foreach (var (path, filter, recursive) in roots)
                {
                    if (!Directory.Exists(path)) continue;

                    var watcher = new FileSystemWatcher(path, filter)
                    {
                        IncludeSubdirectories = recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    void MarkDirty(object? _, FileSystemEventArgs __) => Volatile.Write(ref _scanDirty, 1);
                    watcher.Changed += MarkDirty;
                    watcher.Created += MarkDirty;
                    watcher.Deleted += MarkDirty;
                    watcher.Renamed += (_, _) => Volatile.Write(ref _scanDirty, 1);
                    // A dropped event (buffer overflow, root removed) means we no
                    // longer know what changed — fall back to always scanning.
                    watcher.Error += (_, _) =>
                    {
                        Volatile.Write(ref _scanDirty, 1);
                        lock (_scanGate) _watchersUnavailable = true;
                    };
                    watcher.EnableRaisingEvents = true;
                    created.Add(watcher);
                }

                if (created.Count == 0)
                {
                    _watchersUnavailable = true;
                    return;
                }

                _watchers = created;
            }
            catch
            {
                foreach (var w in created) { try { w.Dispose(); } catch { } }
                _watchersUnavailable = true;
            }
        }
    }

    // CLAUDE_READER
    private IEnumerable<AgentHistoryEntry> ReadClaude(CancellationToken ct)
    {
        var root = Path.Combine(_home, ".claude", "projects");
        if (!Directory.Exists(root)) yield break;

        // Newest files first; cap how many we crack open for responsiveness.
        // DirectoryInfo.EnumerateFiles hands back FileInfo objects already
        // populated from the directory enumeration; Directory.EnumerateFiles +
        // new FileInfo(path) costs one extra stat syscall per file, which on a
        // few hundred transcripts is a measurable slice of the scan on its own.
        var files = new DirectoryInfo(root)
            .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(120);

        foreach (var fi in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryGetCached(_claudeCache, fi, out var facts))
            {
                string? cwd = null, sessionId = null, title = null, aiTitle = null;
                try
                {
                    foreach (var line in ReadFirstLines(fi.FullName, 80))
                    {
                        JsonElement root2;
                        try { using var doc = JsonDocument.Parse(line); root2 = doc.RootElement.Clone(); }
                        catch { continue; }
                        if (root2.ValueKind != JsonValueKind.Object) continue;

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

                facts = new ClaudeFacts(
                    sessionId ?? Path.GetFileNameWithoutExtension(fi.Name),
                    cwd,
                    aiTitle ?? title);
                Store(_claudeCache, fi, facts);
            }

            yield return new AgentHistoryEntry(
                ClaudeCodeBackend.BackendId,
                facts.SessionId,
                facts.Cwd ?? "",
                CleanTitle(facts.Title) ?? "(无标题)",
                new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                fi.FullName);
        }
    }

    private static bool TryGetCached<T>(
        ConcurrentDictionary<string, CachedFile<T>> cache, FileInfo file, out T value)
    {
        if (cache.TryGetValue(file.FullName, out var hit) && hit.Matches(file))
        {
            value = hit.Value;
            return true;
        }
        value = default!;
        return false;
    }

    private static void Store<T>(
        ConcurrentDictionary<string, CachedFile<T>> cache, FileInfo file, T value)
    {
        if (cache.Count > MaxCachedFiles) cache.Clear();
        cache[file.FullName] = new CachedFile<T>(file.LastWriteTimeUtc.Ticks, file.Length, value);
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

        var files = new DirectoryInfo(sessionsRoot)
            .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(120);

        foreach (var fi in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryGetCached(_codexCache, fi, out var facts))
            {
                string? id = null, cwd = null, title = null;
                try
                {
                    foreach (var line in ReadFirstLines(fi.FullName, 40))
                    {
                        JsonElement r;
                        try { using var doc = JsonDocument.Parse(line); r = doc.RootElement.Clone(); }
                        catch { continue; }
                        if (r.ValueKind != JsonValueKind.Object) continue;

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

                facts = new CodexFacts(id, cwd, title);
                Store(_codexCache, fi, facts);
            }

            if (facts.Id is not { } sessionId) continue;

            // Codex owns the semantic thread title.  The first user message is
            // only a fallback for old/missing index entries; preferring it here
            // made every bridge session ignore Codex's generated thread_name.
            var displayTitle = titles.TryGetValue(sessionId, out var indexTitle)
                ? indexTitle
                : facts.FallbackTitle;

            yield return new AgentHistoryEntry(
                CodexBackend.BackendId,
                sessionId,
                facts.Cwd ?? "",
                CleanTitle(displayTitle) ?? "(无标题)",
                new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                fi.FullName);
        }
    }

    private Dictionary<string, string> ReadCodexTitleIndex(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var info = new FileInfo(path);
        if (!info.Exists) return map;

        // Same memo rule as the transcripts: the index only changes when Codex
        // renames a thread, but every scan used to re-read and re-parse it.
        lock (_scanGate)
        {
            if (_codexTitleIndex is { } cached && cached.Matches(info)) return cached.Value;
        }

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

            lock (_scanGate)
            {
                _codexTitleIndex = new CachedFile<Dictionary<string, string>>(
                    info.LastWriteTimeUtc.Ticks, info.Length, map);
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// A metadata line — sessionId, cwd, a thread name, the opening user turn —
    /// is never this long. A tool result inside the same transcript routinely
    /// is: single lines of several MB are normal in Claude Code and Codex
    /// transcripts, and <c>StreamReader.ReadLine</c> materialises every one of
    /// them as a string on the large object heap.
    /// </summary>
    private const int MaxScannedLineBytes = 64 * 1024;

    /// <summary>
    /// Read up to <paramref name="max"/> lines without loading the whole file,
    /// and without ever allocating a string for a line longer than
    /// <see cref="MaxScannedLineBytes"/> — such a line is scanned past and
    /// counts against the budget, exactly as if it had been read and ignored.
    /// UTF-8 is assumed (both CLIs write it); a leading BOM is stripped.
    /// </summary>
    private static IEnumerable<string> ReadFirstLines(string path, int max)
    {
        using var stream = OpenSharedStream(path);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxScannedLineBytes);
        var atFileStart = true;

        // A line is [start, end) in buffer; strip CR and a leading UTF-8 BOM.
        string? Decode(int start, int end)
        {
            if (end > start && buffer[end - 1] == (byte)'\r') end--;
            if (atFileStart)
            {
                atFileStart = false;
                if (end - start >= 3 && buffer[start] == 0xEF
                    && buffer[start + 1] == 0xBB && buffer[start + 2] == 0xBF)
                {
                    start += 3;
                }
            }
            return end > start ? Encoding.UTF8.GetString(buffer, start, end - start) : null;
        }

        try
        {
            var start = 0;          // first unconsumed byte
            var used = 0;           // one past the last valid byte
            var lines = 0;
            var skipping = false;   // the line in progress already blew the cap

            while (lines < max)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, used - start);
                if (newline >= 0)
                {
                    lines++;
                    var line = skipping ? null : Decode(start, newline);
                    skipping = false;
                    start = newline + 1;
                    if (line is not null) yield return line;
                    continue;
                }

                if (start > 0)
                {
                    // Compact what is left, then top the buffer up.
                    Buffer.BlockCopy(buffer, start, buffer, 0, used - start);
                    used -= start;
                    start = 0;
                }
                else if (used == MaxScannedLineBytes)
                {
                    // A full buffer with no newline: this line is over the cap.
                    // Drop what we hold and keep scanning for its terminator.
                    skipping = true;
                    atFileStart = false;
                    used = 0;
                }

                var read = stream.Read(buffer, used, MaxScannedLineBytes - used);
                if (read <= 0) break;
                used += read;
            }

            // Trailing line with no newline terminator.
            if (!skipping && lines < max && used > start)
            {
                var line = Decode(start, used);
                if (line is not null) yield return line;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Open a session file for reading even while another process holds it
    /// open for writing. Codex Desktop / Claude keep their <em>active</em> rollout
    /// files locked; the default <c>new StreamReader(path)</c> / <c>File.ReadLines</c>
    /// request a share mode the writer denies, throwing IOException — which made the
    /// history reader silently skip the very sessions the user is currently using.
    /// <c>FileShare.ReadWrite</c> lets us read alongside the live writer.</summary>
    internal static StreamReader OpenSharedReader(string path)
        => new(OpenSharedStream(path));

    /// <summary>The raw stream behind <see cref="OpenSharedReader"/>, for the
    /// scan path that does its own line splitting.</summary>
    private static FileStream OpenSharedStream(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

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
