using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace MolaGPT.App.Rendering;

/// <summary>
/// Debug-only timing summaries for the two transcript animations. Samples stay
/// in memory while an animation is running; disk writes happen on one background
/// reader so the trace cannot become the UI-thread stall it is meant to measure.
/// </summary>
internal static class AnimationPerformanceTrace
{
#if DEBUG
    private const double SlowLayoutMilliseconds = 8;

    private static readonly Channel<string> Lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private static readonly Dictionary<RevealPresenter, Session> Reveals =
        new(ReferenceEqualityComparer.Instance);
    private static Session? _wheel;
    private static int _nextSession;

    static AnimationPerformanceTrace()
    {
        Directory.CreateDirectory(LogDirectory);
        _ = Task.Run(WriteLinesAsync);
        Write($"trace-start build=Debug pid={Environment.ProcessId}");
    }
#endif

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MolaGPT");

    public static long Timestamp()
    {
#if DEBUG
        return Stopwatch.GetTimestamp();
#else
        return 0;
#endif
    }

    public static void BeginWheel(string? conversationId, double start, double target, int rows)
    {
#if DEBUG
        if (_wheel is not null) WriteSummary(_wheel, "restarted", start);
        _wheel = new Session(++_nextSession, "wheel", conversationId ?? "none", start, target, rows);
#endif
    }

    public static void UpdateWheelTarget(double target)
    {
#if DEBUG
        if (_wheel is not null) _wheel.Target = target;
#endif
    }

    public static void WheelFrame(double rawIntervalMilliseconds)
    {
#if DEBUG
        _wheel?.FrameIntervals.Add(rawIntervalMilliseconds);
#endif
    }

    public static void WheelOffsetWritten()
    {
#if DEBUG
        if (_wheel is not null) _wheel.OffsetWrites++;
#endif
    }

    public static void EndWheel(double end, string reason)
    {
#if DEBUG
        if (_wheel is null) return;
        WriteSummary(_wheel, reason, end);
        _wheel = null;
#endif
    }

    public static void BeginReveal(
        RevealPresenter owner,
        string label,
        bool opening,
        double start,
        TimeSpan duration)
    {
#if DEBUG
        if (Reveals.Remove(owner, out var previous))
            WriteSummary(previous, "interrupted", start);

        Reveals.Add(owner, new Session(
            ++_nextSession,
            opening ? "reveal-open" : "reveal-close",
            label,
            start,
            opening ? 1 : 0,
            0)
        {
            RequestedDurationMilliseconds = duration.TotalMilliseconds
        });
#endif
    }

    public static void RevealFrame(RevealPresenter owner)
    {
#if DEBUG
        if (!Reveals.TryGetValue(owner, out var session)) return;
        var now = Stopwatch.GetTimestamp();
        if (session.LastFrameTimestamp != 0)
            session.FrameIntervals.Add(Stopwatch.GetElapsedTime(session.LastFrameTimestamp, now).TotalMilliseconds);
        session.LastFrameTimestamp = now;
#endif
    }

    public static void SetRevealMode(RevealPresenter owner, bool fadeOnly, double naturalHeight)
    {
#if DEBUG
        if (!Reveals.TryGetValue(owner, out var session)) return;
        session.FadeOnly = fadeOnly;
        session.NaturalHeight = naturalHeight;
#endif
    }

    public static void EndReveal(RevealPresenter owner, double end, string reason)
    {
#if DEBUG
        if (!Reveals.Remove(owner, out var session)) return;
        WriteSummary(session, reason, end);
#endif
    }

    public static void PanelMeasureFinished(
        long started,
        int itemCount,
        int realizedCount,
        int first,
        int last)
    {
#if DEBUG
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AddMeasure(_wheel, elapsed);
        foreach (var session in Reveals.Values) AddMeasure(session, elapsed);

        if (elapsed >= SlowLayoutMilliseconds)
        {
            Write(
                $"slow-panel-measure elapsed_ms={elapsed:0.###} items={itemCount} " +
                $"realized={realizedCount} range={first}-{last} active={ActiveSessions()}");
        }
#endif
    }

    public static void PanelArrangeFinished(long started, int itemCount, int realizedCount)
    {
#if DEBUG
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AddArrange(_wheel, elapsed);
        foreach (var session in Reveals.Values) AddArrange(session, elapsed);

        if (elapsed >= SlowLayoutMilliseconds)
        {
            Write(
                $"slow-panel-arrange elapsed_ms={elapsed:0.###} items={itemCount} " +
                $"realized={realizedCount} active={ActiveSessions()}");
        }
#endif
    }

    public static void RevealMeasureFinished(RevealPresenter owner, long started)
    {
#if DEBUG
        if (!Reveals.TryGetValue(owner, out var session)) return;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        session.RevealMeasureCount++;
        session.RevealMeasureTotal += elapsed;
        session.RevealMeasureMax = Math.Max(session.RevealMeasureMax, elapsed);
#endif
    }

    public static void ThinkingBodyBuilt(long started, int sourceCharacters, int blockCount)
    {
#if DEBUG
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed < 4) return;
        Write($"thinking-body-build elapsed_ms={elapsed:0.###} chars={sourceCharacters} blocks={blockCount}");
#endif
    }

#if DEBUG
    private static string LogPath => Path.Combine(
        LogDirectory,
        $"animation-performance-{Environment.ProcessId}.log");

    private static void AddMeasure(Session? session, double elapsed)
    {
        if (session is null) return;
        session.PanelMeasureCount++;
        session.PanelMeasureTotal += elapsed;
        session.PanelMeasureMax = Math.Max(session.PanelMeasureMax, elapsed);
    }

    private static void AddArrange(Session? session, double elapsed)
    {
        if (session is null) return;
        session.PanelArrangeCount++;
        session.PanelArrangeTotal += elapsed;
        session.PanelArrangeMax = Math.Max(session.PanelArrangeMax, elapsed);
    }

    private static string ActiveSessions()
    {
        var wheel = _wheel is null ? 0 : 1;
        return $"wheel:{wheel},reveal:{Reveals.Count}";
    }

    private static void WriteSummary(Session session, string reason, double end)
    {
        var gaps = session.FrameIntervals;
        var over8 = gaps.Count(x => x > 8.33);
        var over16 = gaps.Count(x => x > 16.67);
        var over33 = gaps.Count(x => x > 33.33);
        var elapsed = Stopwatch.GetElapsedTime(session.StartedTimestamp).TotalMilliseconds;

        Write(
            $"animation session={session.Id} kind={session.Kind} label={Sanitize(session.Label)} " +
            $"reason={reason} duration_ms={elapsed:0.###} requested_ms={session.RequestedDurationMilliseconds:0.###} " +
            $"start={session.Start:0.###} target={session.Target:0.###} end={end:0.###} rows={session.Rows} " +
            $"frames={gaps.Count} writes={session.OffsetWrites} gap_p50_ms={Percentile(gaps, 0.50):0.###} " +
            $"gap_p95_ms={Percentile(gaps, 0.95):0.###} gap_p99_ms={Percentile(gaps, 0.99):0.###} " +
            $"gap_max_ms={(gaps.Count == 0 ? 0 : gaps.Max()):0.###} over_8ms={over8} over_16ms={over16} over_33ms={over33} " +
            $"panel_measure_count={session.PanelMeasureCount} panel_measure_total_ms={session.PanelMeasureTotal:0.###} " +
            $"panel_measure_max_ms={session.PanelMeasureMax:0.###} panel_arrange_count={session.PanelArrangeCount} " +
            $"panel_arrange_total_ms={session.PanelArrangeTotal:0.###} panel_arrange_max_ms={session.PanelArrangeMax:0.###} " +
            $"reveal_measure_count={session.RevealMeasureCount} reveal_measure_total_ms={session.RevealMeasureTotal:0.###} " +
            $"reveal_measure_max_ms={session.RevealMeasureMax:0.###} mode={(session.FadeOnly ? "fade" : "height")} " +
            $"natural_height={session.NaturalHeight:0.###}");
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static string Sanitize(string value) =>
        value.Replace(' ', '_').Replace('\r', '_').Replace('\n', '_');

    private static void Write(string message) =>
        Lines.Writer.TryWrite($"{DateTimeOffset.Now:O} {message}");

    private static async Task WriteLinesAsync()
    {
        await using var stream = new FileStream(
            LogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await foreach (var line in Lines.Reader.ReadAllAsync())
            await writer.WriteLineAsync(line);
    }

    private sealed class Session(
        int id,
        string kind,
        string label,
        double start,
        double target,
        int rows)
    {
        public int Id { get; } = id;
        public string Kind { get; } = kind;
        public string Label { get; } = label;
        public double Start { get; } = start;
        public double Target { get; set; } = target;
        public int Rows { get; } = rows;
        public long StartedTimestamp { get; } = Stopwatch.GetTimestamp();
        public long LastFrameTimestamp { get; set; }
        public List<double> FrameIntervals { get; } = [];
        public int OffsetWrites { get; set; }
        public double RequestedDurationMilliseconds { get; set; }
        public int PanelMeasureCount { get; set; }
        public double PanelMeasureTotal { get; set; }
        public double PanelMeasureMax { get; set; }
        public int PanelArrangeCount { get; set; }
        public double PanelArrangeTotal { get; set; }
        public double PanelArrangeMax { get; set; }
        public int RevealMeasureCount { get; set; }
        public double RevealMeasureTotal { get; set; }
        public double RevealMeasureMax { get; set; }
        public bool FadeOnly { get; set; }
        public double NaturalHeight { get; set; }
    }
#endif
}
