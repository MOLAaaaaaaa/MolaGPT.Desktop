using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat.LocalTools;

/// <summary>
/// Read-only local file tools exposed to BYOK chats: <c>read_file</c>,
/// <c>glob_files</c>, <c>grep_files</c>. Pure C#, no shell/script backend — a
/// future PowerShell/Python backend can layer on without changing this surface.
///
/// All three are read-only and default-allowed (no approval prompt). The only
/// gate is a path deny-list (shared with the Python tool's
/// <c>DeniedPathPrefixes</c>): a target under a denied prefix is refused with a
/// friendly error rather than read.
///
/// Results are returned as plain objects; <see cref="LocalToolRegistry"/>
/// serializes them with the shared JSON options.
/// </summary>
internal static class FileToolset
{
    private const int MaxReadLines = 2000;
    private const int MaxReadBytes = 5 * 1024 * 1024;
    private const int DefaultGlobLimit = 200;
    private const int MaxGlobLimit = 1000;
    private const int DefaultGrepMatches = 100;
    private const int MaxGrepMatches = 500;
    private const int GrepLineClip = 400;
    private const int MaxGrepFileBytes = 4 * 1024 * 1024;

    // Traversal budget (Glob/Grep). A large scope (e.g. C:\) would otherwise
    // walk the whole disk; we stop early and return a partial result flagged
    // timed_out/truncated so the model narrows its scope. The CancellationToken
    // (user "stop" / stream cancel) aborts immediately via OperationCanceledException.
    private static readonly TimeSpan TraversalBudget = TimeSpan.FromSeconds(8);
    private const int MaxScannedEntries = 50_000;

    private static readonly Regex BinaryProbe = new("\0", RegexOptions.Compiled);

    /// <summary>Bounds a directory walk by wall-clock time, scanned-entry count,
    /// and cancellation. <see cref="ShouldStop"/> returns true once any limit is
    /// hit; <see cref="Exhausted"/> records that a budget (not cancellation) ended
    /// the walk so the caller can flag the result as partial.</summary>
    private sealed class TraversalLimit
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly CancellationToken _ct;
        private int _scanned;

        public TraversalLimit(CancellationToken ct) => _ct = ct;
        public bool Exhausted { get; private set; }

        public bool ShouldStop()
        {
            _ct.ThrowIfCancellationRequested(); // user stop → abort the whole call
            if (Exhausted) return true;
            if (++_scanned > MaxScannedEntries || _sw.Elapsed > TraversalBudget)
                Exhausted = true;
            return Exhausted;
        }
    }

    public static object ReadFile(string? path, int? offset, int? limit, IReadOnlyList<string> deniedPrefixes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Error("read_file 需要 path 参数。");

        var full = ResolveAndGuard(path!, deniedPrefixes, out var denyError);
        if (full is null)
            return denyError!;

        if (!File.Exists(full))
            return Error($"文件不存在：{full}");

        try
        {
            var info = new FileInfo(full);
            if (info.Length > MaxReadBytes)
                return Error($"文件过大（{info.Length / 1024}KB），超过 {MaxReadBytes / 1024 / 1024}MB 上限，无法读取。");

            var raw = File.ReadAllText(full, Encoding.UTF8);
            if (raw.Length > 0 && BinaryProbe.IsMatch(raw))
                return Error("该文件看起来是二进制文件，无法作为文本读取。");

            var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var totalLines = lines.Length;

            var start = Math.Max(0, (offset ?? 1) - 1); // offset is 1-based
            if (start >= totalLines && totalLines > 0)
                start = totalLines - 1;
            var take = limit is { } l && l > 0 ? Math.Min(l, MaxReadLines) : MaxReadLines;
            var slice = lines.Skip(start).Take(take).ToArray();
            var truncated = start + slice.Length < totalLines;

            return new
            {
                success = true,
                source = "local_read_file",
                path = full,
                offset = start + 1,
                line_count = slice.Length,
                total_lines = totalLines,
                truncated,
                content = string.Join("\n", slice)
            };
        }
        catch (Exception ex)
        {
            return Error($"读取失败：{ex.Message}");
        }
    }

    public static object Glob(string? pattern, string? path, int? limit, IReadOnlyList<string> deniedPrefixes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Error("glob_files 需要 pattern 参数（如 **/*.cs）。");

        var root = ResolveRoot(path, deniedPrefixes, out var denyError);
        if (root is null)
            return denyError!;

        var cap = limit is { } l && l > 0 ? Math.Min(l, MaxGlobLimit) : DefaultGlobLimit;
        try
        {
            var regex = GlobToRegex(pattern!);
            var limiter = new TraversalLimit(ct);
            var matches = new List<(string Path, DateTime Mtime)>();
            foreach (var file in EnumerateFilesSafe(root, limiter))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!regex.IsMatch(rel) && !regex.IsMatch(Path.GetFileName(file)))
                    continue;
                if (IsDenied(file, deniedPrefixes))
                    continue;
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(file); }
                catch { mtime = DateTime.MinValue; }
                matches.Add((file, mtime));
            }

            var ordered = matches.OrderByDescending(m => m.Mtime).Select(m => m.Path).ToList();
            var truncated = ordered.Count > cap || limiter.Exhausted;

            return new
            {
                success = true,
                source = "local_glob",
                pattern,
                root,
                count = Math.Min(ordered.Count, cap),
                truncated,
                timed_out = limiter.Exhausted,
                note = limiter.Exhausted ? "搜索范围过大，已在限定时间内返回部分结果；请缩小 path 范围或使用更精确的 pattern。" : null,
                matches = ordered.Take(cap).ToArray()
            };
        }
        catch (Exception ex)
        {
            return Error($"查找失败：{ex.Message}");
        }
    }

    public static object Grep(
        string? pattern,
        string? path,
        string? glob,
        bool ignoreCase,
        int? maxMatches,
        IReadOnlyList<string> deniedPrefixes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Error("grep_files 需要 pattern 参数（正则）。");

        var root = ResolveRoot(path, deniedPrefixes, out var denyError);
        if (root is null)
            return denyError!;

        var cap = maxMatches is { } m && m > 0 ? Math.Min(m, MaxGrepMatches) : DefaultGrepMatches;
        Regex regex;
        try
        {
            var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (ignoreCase) opts |= RegexOptions.IgnoreCase;
            regex = new Regex(pattern!, opts);
        }
        catch (Exception ex)
        {
            return Error($"无效的正则表达式：{ex.Message}");
        }

        Regex? fileFilter = null;
        if (!string.IsNullOrWhiteSpace(glob))
        {
            try { fileFilter = GlobToRegex(glob!); }
            catch { fileFilter = null; }
        }

        try
        {
            var hits = new List<object>();
            var truncated = false;
            var limiter = new TraversalLimit(ct);
            foreach (var file in EnumerateFilesSafe(root, limiter))
            {
                if (hits.Count >= cap) { truncated = true; break; }
                if (IsDenied(file, deniedPrefixes)) continue;
                if (fileFilter is not null)
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!fileFilter.IsMatch(rel) && !fileFilter.IsMatch(Path.GetFileName(file)))
                        continue;
                }

                FileInfo info;
                try { info = new FileInfo(file); } catch { continue; }
                if (info.Length > MaxGrepFileBytes) continue;

                string[] lines;
                try
                {
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    if (text.Length > 0 && BinaryProbe.IsMatch(text)) continue;
                    lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                }
                catch { continue; }

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!regex.IsMatch(lines[i])) continue;
                    var text = lines[i].Length > GrepLineClip ? lines[i][..GrepLineClip] + "…" : lines[i];
                    hits.Add(new { file, line = i + 1, text });
                    if (hits.Count >= cap) { truncated = true; break; }
                }
            }

            truncated |= limiter.Exhausted;
            return new
            {
                success = true,
                source = "local_grep",
                pattern,
                root,
                count = hits.Count,
                truncated,
                timed_out = limiter.Exhausted,
                note = limiter.Exhausted ? "搜索范围过大，已在限定时间内返回部分结果；请缩小 path 范围或加 glob 过滤。" : null,
                matches = hits.ToArray()
            };
        }
        catch (Exception ex)
        {
            return Error($"搜索失败：{ex.Message}");
        }
    }

    // ---- helpers ----

    private static object Error(string message) => new { success = false, source = "local_file_tool", error = message };

    private static string? ResolveAndGuard(string path, IReadOnlyList<string> denied, out object? error)
    {
        error = null;
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"', '\'')); }
        catch (Exception ex) { error = Error($"无效路径：{ex.Message}"); return null; }

        if (IsDenied(full, denied))
        {
            error = Error($"路径在拒绝列表内，已被策略拦截：{full}");
            return null;
        }
        return full;
    }

    private static string? ResolveRoot(string? path, IReadOnlyList<string> denied, out object? error)
    {
        error = null;
        var raw = string.IsNullOrWhiteSpace(path)
            ? (Directory.Exists(Directory.GetCurrentDirectory())
                ? Directory.GetCurrentDirectory()
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : path!.Trim().Trim('"', '\'');

        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception ex) { error = Error($"无效路径：{ex.Message}"); return null; }

        if (!Directory.Exists(full))
        {
            error = Error($"目录不存在：{full}");
            return null;
        }
        if (IsDenied(full, denied))
        {
            error = Error($"目录在拒绝列表内，已被策略拦截：{full}");
            return null;
        }
        return full;
    }

    private static bool IsDenied(string fullPath, IReadOnlyList<string> denied)
    {
        if (denied.Count == 0) return false;
        var normalized = fullPath.Replace('\\', '/');
        foreach (var prefix in denied)
        {
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            string full;
            try { full = Path.GetFullPath(prefix.Trim()).Replace('\\', '/').TrimEnd('/'); }
            catch { continue; }
            if (full.Length == 0) continue;
            if (normalized.Equals(full, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(full + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, TraversalLimit limit)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (limit.ShouldStop()) yield break; // time / scan-count budget or cancellation

            var dir = stack.Pop();
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            // Skip common heavy / noise directories.
            if (name is "node_modules" or ".git" or "bin" or "obj" or ".vs" or "__pycache__")
                continue;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { subDirs = Array.Empty<string>(); }
            foreach (var sub in subDirs) stack.Push(sub);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var file in files)
            {
                if (limit.ShouldStop()) yield break;
                yield return file;
            }
        }
    }

    /// <summary>Translates a glob (supporting <c>**</c>, <c>*</c>, <c>?</c>) to a
    /// full-match regex against a forward-slash relative path.</summary>
    private static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/').Trim();
        var sb = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                        if (i + 1 < normalized.Length && normalized[i + 1] == '/') i++;
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '.': sb.Append("\\."); break;
                case '/': sb.Append('/'); break;
                default:
                    if (!char.IsLetterOrDigit(c)) sb.Append('\\');
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
