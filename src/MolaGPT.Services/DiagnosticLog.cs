using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MolaGPT.Desktop.Services;

public static class DiagnosticLog
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly Regex Secrets = new(
        @"\b(Authorization\s*[:=]\s*)(?:Bearer\s+)?[A-Za-z0-9._~+/\-]+=*|\b(Bearer)\s+[A-Za-z0-9._~+/\-]+=*|([?&](?:code|token|jwt|access_token|refresh_token|api[_-]?key|apikey|password|secret)=)([^&\s\]""')]+)|\b((?:api[_-]?key|apikey|token|jwt|password|secret)\s*[:=]\s*)([^\s,;]+)|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b|\b(?:sk|sess)-[A-Za-z0-9_-]{12,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Channel<Entry> Queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private static long _dropped;
    private static readonly Lazy<Task> Worker = new(() => Task.Run(ConsumeAsync));
    public static string SessionDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT", "Logs",
        DateTime.Now.ToString("yyyy-MM-dd"), $"{DateTime.Now:HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");

    public static void Write(string tag, string message)
    {
        _ = Worker.Value;
        if (!Queue.Writer.TryWrite(new Entry(DateTime.Now, tag, message, null)))
            Interlocked.Increment(ref _dropped);
    }

    public static void Flush()
    {
        _ = Worker.Value;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            Queue.Writer.WriteAsync(new Entry(default, "", "", completion), timeout.Token).AsTask().GetAwaiter().GetResult();
            completion.Task.Wait(timeout.Token);
        }
        catch (OperationCanceledException) { }
    }

    public static string LogsRoot { get; } = Directory.GetParent(SessionDirectory)!.Parent!.FullName;

    /// <summary>Logs of earlier runs. The running session's directory is left out:
    /// it is being written, and a crash report would need it.</summary>
    public static IReadOnlyList<string> ListPastSessionPaths()
    {
        var paths = new List<string>();
        try
        {
            foreach (var day in new DirectoryInfo(LogsRoot).EnumerateDirectories())
            {
                if (day.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !DateTime.TryParseExact(day.Name, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out _)) continue;
                paths.AddRange(day.EnumerateDirectories()
                    .Where(session => !session.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                      && Regex.IsMatch(session.Name, @"^\d{6}-\d+-[0-9a-f]{32}$")
                                      && !string.Equals(session.FullName, SessionDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(session => session.FullName));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths;
    }

    /// <summary>
    /// Log files earlier builds wrote straight into the data directory and never
    /// rotated away: one <c>animation-performance-&lt;pid&gt;.log</c> per launch, the
    /// single-file <c>app.log</c>, and the WPF-era <c>perf.log</c> with its
    /// <c>perf.on</c> switch. Nothing writes or reads any of them now. Matched by
    /// exact name so nothing else in that directory can be caught.
    /// </summary>
    public static IReadOnlyList<string> ListLegacyLogPaths()
    {
        try
        {
            var dataRoot = Directory.GetParent(LogsRoot)!.FullName;
            return Directory.EnumerateFiles(dataRoot)
                .Where(path => LegacyLogName.IsMatch(System.IO.Path.GetFileName(path)))
                .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static readonly Regex LegacyLogName = new(
        @"^(?:animation-performance-\d+\.log|app\.log|perf\.log|perf\.on)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static async Task ConsumeAsync()
    {
        try
        {
            Directory.CreateDirectory(SessionDirectory);
            var root = Directory.GetParent(SessionDirectory)!.Parent!;
            foreach (var day in root.EnumerateDirectories())
            {
                try
                {
                    if (DateTime.TryParseExact(day.Name, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)
                        && date < DateTime.Today.AddDays(-14)) day.Delete(true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            while (await Queue.Reader.WaitToReadAsync())
            {
                var batches = new Dictionary<string, StringBuilder>();
                var completions = new List<TaskCompletionSource>();
                var count = 0;
                while (count++ < 512 && Queue.Reader.TryRead(out var entry))
                {
                    if (entry.Completion is not null) { completions.Add(entry.Completion); continue; }
                    var category = Category(entry.Tag);
                    if (!batches.TryGetValue(category, out var text)) batches[category] = text = new StringBuilder();
                    text.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                        .Append(" [").Append(Environment.ProcessId).Append("] ")
                        .Append(Sanitize(entry.Tag)).Append(' ').Append(Sanitize(entry.Message)).Append('\n');
                }
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    if (!batches.TryGetValue("app", out var app)) batches["app"] = app = new StringBuilder();
                    app.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                        .Append(" [log] dropped ").Append(dropped).Append(" entries\n");
                }
                foreach (var (category, text) in batches)
                {
                    try
                    {
                        var path = System.IO.Path.Combine(SessionDirectory, category + ".log");
                        if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                            File.Move(path, path + ".1", true);
                        await File.AppendAllTextAsync(path, text.ToString(), Encoding.UTF8);
                    }
                    catch (IOException ex) { System.Diagnostics.Trace.WriteLine($"Diagnostic log failed: {ex.Message}"); }
                    catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.WriteLine($"Diagnostic log failed: {ex.Message}"); }
                }
                foreach (var completion in completions) completion.TrySetResult();
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Diagnostic log failed: {ex.Message}"); }
    }

    private static string Sanitize(string value) => Secrets.Replace(value, m =>
    {
        if (m.Groups[1].Success) return m.Groups[1].Value + "[redacted]";
        if (m.Groups[2].Success) return m.Groups[2].Value + " [redacted]";
        if (m.Groups[3].Success) return m.Groups[3].Value + "[redacted]";
        if (m.Groups[5].Success) return m.Groups[5].Value + "[redacted]";
        return "[redacted]";
    }).Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Category(string tag) => tag.ToLowerInvariant() switch
    {
        "chatgpt" or "account" or "proxy" or "updatecheck" or "provider" => "network",
        "pi-work" or "pi-runtime" or "pi-byok" or "pi-sidecar" or "mcp" or "agentrelay" or "remotework" => "agent",
        "attachments" or "memory" or "sync" => "storage",
        "avalonia" or "rendering" or "canvas" or "images" or "selection" => "ui",
        "performance" => "performance",
        _ => "app"
    };

    private sealed record Entry(DateTime Time, string Tag, string Message, TaskCompletionSource? Completion);
}
