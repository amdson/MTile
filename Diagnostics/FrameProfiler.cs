using System;
using System.Diagnostics;

namespace MTile;

// Per-pass frame timing, cheap enough to leave on and portable enough to read in the
// browser — where no external profiler exists and the only honest instrument is an
// on-screen number. The desktop `worst/60f sim/cosmetics/draw` line this replaces was
// three lumps; a WASM frame budget (25 ms at the measured ~40 fps, vs 16.7 ms native)
// has to be attributed to individual passes before any of it can be cut.
//
// Zero allocation in steady state: slots are registered once (literal names, matched by
// reference then by value), timings accumulate into long[] arrays, and the report string
// is rebuilt only on the frames it is published.
//
// Timing source is Stopwatch, which on WASM is backed by performance.now() — coarse
// (~0.1 ms, and deliberately jittered by browsers). A single frame's sub-ms pass is
// therefore noise; the WINDOW MEAN over 60 frames is the number to trust, which is why
// mean is reported alongside worst rather than worst alone.
//
// Scopes are INCLUSIVE: a scope opened inside another counts in both. Keep the nesting
// shallow and read siblings against each other, not children against parents.
public sealed class FrameProfiler
{
    public const int MaxSlots = 24;

    private readonly string[] _names = new string[MaxSlots];
    private readonly long[]   _frame = new long[MaxSlots];   // this frame, ticks
    private readonly long[]   _sum   = new long[MaxSlots];   // window total, ticks
    private readonly long[]   _worst = new long[MaxSlots];   // worst single frame in window
    // Published per-slot snapshot, in ms. Kept separate from the accumulators because
    // those are zeroed the instant a window closes — Dump() is called from a hotkey on
    // an arbitrary later frame and must still see the last complete window.
    private readonly double[] _repMean  = new double[MaxSlots];
    private readonly double[] _repWorst = new double[MaxSlots];
    private int  _count;

    private int  _windowFrames;
    private long _frameStart;
    private long _wallSum, _wallWorst;

    // Published report line — read by the HUD, rewritten once per window.
    private string _report = "(profiling...)";
    public string Report => _report;
    public double FrameMeanMs  { get; private set; }
    public double FrameWorstMs { get; private set; }
    public double Fps => FrameMeanMs > 0 ? 1000.0 / FrameMeanMs : 0;

    // Frames per published window. 60 is ~1 s at 60 fps, ~1.5 s at the web build's 40.
    public int WindowFrames = 60;
    // Passes under this share of the frame are folded into "other" in the report line, so
    // it stays readable on a 1000px window instead of listing twenty 0.00s. Dump() is
    // unfiltered.
    public double ReportFloorMs = 0.05;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    // Register a pass and get its slot. Call once at startup and cache the int — the
    // per-frame path must never do a name lookup.
    public int Slot(string name)
    {
        for (int i = 0; i < _count; i++)
            if (ReferenceEquals(_names[i], name) || _names[i] == name) return i;
        if (_count == MaxSlots) throw new InvalidOperationException($"FrameProfiler: >{MaxSlots} slots");
        _names[_count] = name;
        return _count++;
    }

    public Scope Measure(int slot) => new(this, slot);

    // Manual form, for passes whose begin/end straddle control flow a `using` can't wrap.
    public long Begin() => Stopwatch.GetTimestamp();
    public void End(int slot, long startTicks) => _frame[slot] += Stopwatch.GetTimestamp() - startTicks;

    public readonly struct Scope : IDisposable
    {
        private readonly FrameProfiler _p;
        private readonly int _slot;
        private readonly long _start;
        internal Scope(FrameProfiler p, int slot) { _p = p; _slot = slot; _start = Stopwatch.GetTimestamp(); }
        public void Dispose() => _p._frame[_slot] += Stopwatch.GetTimestamp() - _start;
    }

    // Call once at the very top of the frame (start of Update).
    public void BeginFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_frameStart != 0)
        {
            // Wall time of the PREVIOUS frame, top-of-frame to top-of-frame, so it includes
            // the vsync/present stall — i.e. the number that matches observed fps, rather
            // than the sum of the passes below it.
            long wall = now - _frameStart;
            _wallSum += wall;
            if (wall > _wallWorst) _wallWorst = wall;
        }
        _frameStart = now;
    }

    // Call once at the very bottom of the frame (end of Draw).
    public void EndFrame()
    {
        for (int i = 0; i < _count; i++)
        {
            _sum[i] += _frame[i];
            if (_frame[i] > _worst[i]) _worst[i] = _frame[i];
            _frame[i] = 0;
        }
        if (++_windowFrames < WindowFrames) return;

        FrameMeanMs  = _wallSum   * TicksToMs / _windowFrames;
        FrameWorstMs = _wallWorst * TicksToMs;
        for (int i = 0; i < _count; i++)
        {
            _repMean[i]  = _sum[i] * TicksToMs / _windowFrames;
            _repWorst[i] = _worst[i] * TicksToMs;
        }
        _report = BuildReport();

        for (int i = 0; i < _count; i++) { _sum[i] = 0; _worst[i] = 0; }
        _wallSum = _wallWorst = 0;
        _windowFrames = 0;
    }

    private string BuildReport()
    {
        var sb = new System.Text.StringBuilder(192);
        sb.Append($"{Fps,5:F1} fps  frame {FrameMeanMs:F2}/{FrameWorstMs:F2}ms  |");
        double other = 0;
        for (int i = 0; i < _count; i++)
        {
            double mean = _repMean[i];
            if (mean < ReportFloorMs) { other += mean; continue; }
            sb.Append($"  {_names[i]} {mean:F2}");
            if (_repWorst[i] > mean * 3) sb.Append($"/{_repWorst[i]:F1}");   // flag spiky passes only
        }
        if (other >= ReportFloorMs) sb.Append($"  other {other:F2}");
        return sb.ToString();
    }

    // Machine-readable dump of the last complete window, one pass per line. This is how
    // the browser build reports numbers: the on-screen line is hard to read off a
    // screenshot, but Console.WriteLine lands in devtools where it can be copied.
    public string Dump()
    {
        var sb = new System.Text.StringBuilder(320);
        sb.AppendLine($"frame_mean_ms={FrameMeanMs:F3}");
        sb.AppendLine($"frame_worst_ms={FrameWorstMs:F3}");
        sb.AppendLine($"fps={Fps:F2}");
        for (int i = 0; i < _count; i++)
            sb.AppendLine($"{_names[i]}_ms={_repMean[i]:F3}  worst_ms={_repWorst[i]:F3}");
        return sb.ToString();
    }
}
