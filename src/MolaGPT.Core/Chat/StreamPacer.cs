using System.Diagnostics;
using System.Globalization;

namespace MolaGPT.Core.Chat;

/// <summary>Which of a turn's two text streams a paced run belongs to.</summary>
public enum PacedStream
{
    /// <summary>The visible answer.</summary>
    Answer,
    /// <summary>Reasoning / chain of thought.</summary>
    Thinking
}

/// <summary>
/// Releases streamed text to the UI at a readable, steady rate.
///
/// This is an adaptive <b>jitter buffer</b>, not a drain scheduler, and the
/// difference is the whole point. The obvious design — "aim to empty the queue
/// within N milliseconds" — saturates its own ceiling the moment a provider is
/// faster than that ceiling, at which point it has stopped being a smoother and
/// become a fixed speed limit unrelated to the model. Desktop faces a very wide
/// span of providers (a local sidecar emitting instantly, a cloud model
/// trickling a token at a time), so the target rate here is instead the
/// <i>observed sustained inbound rate</i>: fast model fast, slow model slow,
/// never faster than the model — outrunning it would drain the cushion and put
/// the stalls back on screen.
///
/// Rate matching alone has no restoring force: output ≈ average input leaves the
/// queue wherever it happened to settle, so a bursty-but-fast stream can empty
/// during an inter-chunk gap and stutter. So the pacer also holds a small
/// deliberate backlog — a cushion sized to the jitter it has actually seen — and
/// pulls toward it proportionally. That cushion is bought with display latency,
/// which is the fundamental jitter-buffer trade, so it is kept adaptive: a
/// provider that never stalls stays at the floor and pays almost nothing. Since
/// the cushion is at most <see cref="TargetDelayCapSec"/> of the current rate,
/// that cap is also the bound on how far the display can sit behind the model.
///
/// Text is never dropped and never reordered. Structural events (a tool card
/// opening) are ordered against the text around them by the caller, which
/// reveals everything queued before recording the event's offset.
/// </summary>
public sealed class StreamPacer
{
    /// <summary>Frame interval the caller should tick at. Actual elapsed time is
    /// measured and passed in, so a missed tick is caught up rather than lost.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>Denominator floor for the sustained-rate estimate, bounding the
    /// cold-start spike where a first burst would otherwise divide by ~0.</summary>
    private const double SustainedMinMs = 1000;

    /// <summary>Minimum cushion for any provider: a frame-quantisation floor
    /// below the threshold of perception. Smooth providers never leave it.</summary>
    private const double TargetDelayFloorSec = 0.08;

    /// <summary>Maximum steady display latency traded for smoothness. A policy
    /// budget, not a constant of nature.</summary>
    private const double TargetDelayCapSec = 0.80;

    /// <summary>A gap counts as a stall only when it dwarfs the stream's own
    /// cadence, so a slow-but-steady provider is not misread as stalling.</summary>
    private const double StallArmAbsMinSec = 0.25;
    private const double StallArmFactor = 6;

    /// <summary>Fast attack, slow release: one stall arms the cushion, and it
    /// decays over this constant so the cushion is still up for the next one.</summary>
    private const double StallReleaseSec = 6;

    /// <summary>Inter-arrival gaps kept for the median that sets the stall
    /// threshold.</summary>
    private const int GapSamples = 32;

    /// <summary>Time constant of the pull toward the cushion. Far larger than a
    /// frame, so the controller is well damped and does not oscillate.</summary>
    private const double RelaxSec = 0.30;

    /// <summary>
    /// Ceiling on catch-up: output may run at the inbound rate <i>plus</i>
    /// whatever clears the current backlog inside this window, and no faster.
    ///
    /// Something has to bound catch-up, because the restoring term below is a
    /// nudge toward the cushion and behaves very badly as a catch-up engine — a
    /// backlog of a few thousand characters drives it to five figures per
    /// second, which puts the whole answer on screen in two frames. The
    /// reference implementation instead keeps a hard backlog ceiling and
    /// releases everything above it in one frame; that is fine when the ceiling
    /// only ever guards latency against a live model, but Desktop also replays
    /// completed streams and talks to a local sidecar that can hand over an
    /// entire answer at once, and there a one-frame release is precisely the
    /// paragraph-appears-instantly effect this class exists to remove.
    ///
    /// Expressing the bound as a time rather than a rate is what keeps it from
    /// becoming the fixed speed limit it replaces: it never binds on a stream
    /// the display is already keeping up with.
    /// </summary>
    private const double CatchUpSec = 1.0;

    /// <summary>Frame delta is clamped before it drives the playout maths. A
    /// minimised window coalesces timer ticks, and an unclamped catch-up delta
    /// would dump the whole queue in the frame after it is restored.</summary>
    private const double MaxFrameDtMs = 100;

    /// <summary>Progress guarantee against rounding, not a speed floor — a real
    /// floor would over-drain a genuinely slow stream.</summary>
    private const int MinStep = 1;

    /// <summary>Once upstream has finished there are no more arrivals, so the
    /// rate estimate is meaningless. The tail plays out at whichever is faster:
    /// a gentle typewriter, or fast enough to finish inside the drain budget.</summary>
    private const int PostStreamStep = 5;
    private const double PostStreamDrainSec = 2.0;

    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private readonly Lock _gate = new();
    private readonly CharQueue _answer = new();
    private readonly CharQueue _thinking = new();
    private readonly double[] _gaps = new double[GapSamples];
    private readonly List<(PacedStream Kind, string Text)> _scratch = new(2);

    private int _gapCount;
    private int _gapNext;
    private long _totalChars;
    private long _firstArrivalTicks = -1;
    private long _lastArrivalTicks;
    private bool _sawFirstChunk;
    private double _stallEstSec;
    private double _credit;
    private bool _firstFrame = true;
    private bool _completed;

    /// <summary>Whether any text is still waiting to be revealed.</summary>
    public bool HasPending
    {
        get { lock (_gate) return _answer.Length > 0 || _thinking.Length > 0; }
    }

    /// <summary>Answer text accepted but not yet revealed. Callers that capture
    /// the final text — persistence, request history, title generation — must
    /// append this to what they have shown, because the display deliberately
    /// lags during the post-stream drain.</summary>
    public string PendingAnswer
    {
        get { lock (_gate) return _answer.Peek(); }
    }

    /// <summary>Accepts text for later release. Safe to call from any thread.</summary>
    public void Enqueue(PacedStream kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_gate)
        {
            (kind == PacedStream.Answer ? _answer : _thinking).Append(text);
            RecordArrival(text.Length);
        }
    }

    /// <summary>
    /// Upstream has finished. Reasoning is released whole — it is collapsed on
    /// completion anyway, so pacing its tail buys nothing — while the answer
    /// switches to the bounded post-stream drain so the last words are not
    /// slammed on screen in a single frame.
    /// </summary>
    public void Complete(Action<PacedStream, string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        lock (_gate) _completed = true;
        DumpThinking(emit);
    }

    /// <summary>Releases queued reasoning immediately, leaving the answer queue
    /// alone. Used when a segment closes: its text must be complete before the
    /// card freezes, but the answer below it should keep playing out.</summary>
    public void DumpThinking(Action<PacedStream, string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        string thinking;
        lock (_gate) thinking = _thinking.TakeAll();

        if (thinking.Length > 0) emit(PacedStream.Thinking, thinking);
    }

    /// <summary>Reveals everything immediately. Used where the display must be
    /// in step with the model right now: a structural event whose position in
    /// the text has to be recorded, a cancelled turn, teardown.</summary>
    public void DumpAll(Action<PacedStream, string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        string thinking, answer;
        lock (_gate)
        {
            thinking = _thinking.TakeAll();
            answer = _answer.TakeAll();
            _credit = 0;
        }

        if (thinking.Length > 0) emit(PacedStream.Thinking, thinking);
        if (answer.Length > 0) emit(PacedStream.Answer, answer);
    }

    /// <summary>
    /// Releases one frame's worth of text. <paramref name="elapsedMs"/> is the
    /// real time since the previous frame; pass 0 for the first frame after a
    /// start or reset.
    ///
    /// <paramref name="emit"/> is invoked outside the lock, because it runs
    /// arbitrary view-model work — property change notification, block rebuilds
    /// — and holding the queue lock across that would invite a re-entrant
    /// enqueue to deadlock.
    /// </summary>
    public void Drain(double elapsedMs, Action<PacedStream, string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        _scratch.Clear();

        lock (_gate)
        {
            var backlog = _answer.Length + _thinking.Length;
            if (backlog == 0)
            {
                _credit = 0;
                return;
            }

            var budget = NextBudget(backlog, elapsedMs);

            // Reasoning drains before the answer: within a turn it always comes
            // first, and spending the budget in order is what keeps the two from
            // appearing to grow side by side.
            budget -= TakeInto(_thinking, PacedStream.Thinking, budget);
            if (budget > 0) TakeInto(_answer, PacedStream.Answer, budget);
        }

        foreach (var (kind, text) in _scratch) emit(kind, text);
        _scratch.Clear();
    }

    /// <summary>Clears the queue and every estimator. For reuse across turns.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _answer.Clear();
            _thinking.Clear();
            _gapCount = 0;
            _gapNext = 0;
            _totalChars = 0;
            _firstArrivalTicks = -1;
            _lastArrivalTicks = 0;
            _sawFirstChunk = false;
            _stallEstSec = 0;
            _credit = 0;
            _firstFrame = true;
            _completed = false;
        }
    }

    // ---- internals ---------------------------------------------------------

    /// <summary>Updates the rate and stall estimators for one arrival. Called
    /// under <see cref="_gate"/>.</summary>
    private void RecordArrival(int length)
    {
        var now = Stopwatch.GetTimestamp();
        if (_firstArrivalTicks < 0) _firstArrivalTicks = now;
        _totalChars += length;

        var previous = _lastArrivalTicks;
        _lastArrivalTicks = now;
        if (previous <= 0) return;

        var gapSec = (now - previous) / TicksPerMs / 1000.0;

        // The startup gap arms the cushion — it is the largest stall the stream
        // will ever show — but must not enter the cadence median, where a single
        // huge sample in a near-empty ring would poison the threshold for every
        // later stall.
        if (_sawFirstChunk)
        {
            _gaps[_gapNext] = gapSec;
            _gapNext = (_gapNext + 1) % GapSamples;
            if (_gapCount < GapSamples) _gapCount++;
        }
        _sawFirstChunk = true;

        var armSec = Math.Max(StallArmAbsMinSec, StallArmFactor * MedianGapSec());
        if (gapSec > armSec)
            _stallEstSec = Math.Max(_stallEstSec, Math.Min(gapSec, TargetDelayCapSec));
    }

    private double MedianGapSec()
    {
        if (_gapCount == 0) return 0;

        Span<double> sorted = stackalloc double[GapSamples];
        _gaps.AsSpan(0, _gapCount).CopyTo(sorted);
        sorted = sorted[.._gapCount];
        sorted.Sort();
        return sorted[_gapCount >> 1];
    }

    /// <summary>How many UTF-16 units this frame may release. Called under
    /// <see cref="_gate"/>.</summary>
    private int NextBudget(int backlog, double elapsedMs)
    {
        // First frame after a start has no reference delta and no rate sample,
        // so it reveals a single unit: a burst that piled up before the loop
        // started must not land in one frame.
        if (_firstFrame)
        {
            _firstFrame = false;
            return MinStep;
        }

        var dt = Math.Clamp(elapsedMs, 0, MaxFrameDtMs);

        int count;
        if (_completed)
        {
            var toFinish = (int)Math.Ceiling(backlog * dt / (PostStreamDrainSec * 1000));
            count = Math.Max(PostStreamStep, toFinish);
        }
        else
        {
            var elapsedSinceFirstMs = (Stopwatch.GetTimestamp() - _firstArrivalTicks) / TicksPerMs;
            var ratePerSec = _totalChars / Math.Max(elapsedSinceFirstMs, SustainedMinMs) * 1000.0;

            // Slow release, so a stream that stops stalling returns to low latency.
            _stallEstSec *= Math.Exp(-(dt / 1000.0) / StallReleaseSec);
            var targetSec = Math.Clamp(_stallEstSec, TargetDelayFloorSec, TargetDelayCapSec);
            var adjustedRate = ratePerSec + (backlog - ratePerSec * targetSec) / RelaxSec;
            adjustedRate = Math.Min(adjustedRate, ratePerSec + backlog / CatchUpSec);

            if (adjustedRate > 0)
            {
                // Sub-unit-per-frame budget is carried, so a genuinely slow
                // stream plays at its real rate instead of being forced up to
                // one unit every frame (~60/s) and then stalling.
                _credit += adjustedRate * dt / 1000.0;
                count = (int)_credit;
                _credit -= count;
            }
            else
            {
                // Only reachable when the queue is far below the cushion — a
                // stall draining past equilibrium. Dribble rather than freeze
                // with text in hand.
                count = MinStep;
                _credit = 0;
            }
        }

        return Math.Min(count, backlog);
    }

    /// <summary>Moves up to <paramref name="budget"/> units out of one queue and
    /// stages them for emission. Returns what it actually took.</summary>
    private int TakeInto(CharQueue queue, PacedStream kind, int budget)
    {
        if (budget <= 0 || queue.Length == 0) return 0;

        var length = SafeLength(queue.Span, budget);
        if (length <= 0) return 0;

        _scratch.Add((kind, queue.Take(length)));
        return length;
    }

    /// <summary>
    /// Rounds a UTF-16 budget up to a grapheme-cluster boundary.
    ///
    /// Cutting on a raw index splits surrogate pairs and combining sequences, so
    /// an emoji or an astral-plane character renders as a replacement glyph for
    /// the frame in which its halves are separated. Walking clusters from the
    /// head costs time proportional to what is being emitted, not to the queue.
    /// </summary>
    private static int SafeLength(ReadOnlySpan<char> span, int budget)
    {
        if (budget >= span.Length) return span.Length;
        if (budget <= 0) return 0;

        var taken = 0;
        while (taken < budget)
        {
            var next = StringInfo.GetNextTextElementLength(span[taken..]);
            if (next <= 0) return Math.Min(budget, span.Length);

            taken += next;
            if (taken >= span.Length) return span.Length;
        }

        return taken;
    }

    /// <summary>
    /// Growable character buffer with a read cursor.
    ///
    /// A <see cref="System.Text.StringBuilder"/> cannot hand out the span the
    /// grapheme walk needs, and re-materialising one per frame would allocate
    /// the whole backlog sixty times a second.
    /// </summary>
    private sealed class CharQueue
    {
        private char[] _buffer = new char[1024];
        private int _start;
        private int _end;

        public int Length => _end - _start;

        public ReadOnlySpan<char> Span => _buffer.AsSpan(_start, _end - _start);

        public void Append(string text)
        {
            EnsureRoom(text.Length);
            text.CopyTo(0, _buffer, _end, text.Length);
            _end += text.Length;
        }

        public string Take(int length)
        {
            var result = new string(_buffer, _start, length);
            _start += length;
            if (_start == _end) _start = _end = 0;
            return result;
        }

        public string Peek() => Length == 0 ? string.Empty : new string(_buffer, _start, Length);

        public string TakeAll() => Take(Length);

        public void Clear() => _start = _end = 0;

        private void EnsureRoom(int needed)
        {
            if (_end + needed <= _buffer.Length) return;

            var live = Length;
            if (live + needed <= _buffer.Length)
            {
                Array.Copy(_buffer, _start, _buffer, 0, live);
            }
            else
            {
                var grown = new char[Math.Max(_buffer.Length * 2, live + needed)];
                Array.Copy(_buffer, _start, grown, 0, live);
                _buffer = grown;
            }

            _start = 0;
            _end = live;
        }
    }
}
