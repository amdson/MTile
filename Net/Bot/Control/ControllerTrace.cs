using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MTile.Net;

// Occupancy / acquisition / preemption / oscillation bookkeeping for one BotController
// (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Trace and evaluation"). Per frame it keeps only
// the current record (Move holder, Act holder, constraints that masked) and folds it into
// the aggregates; the oscillation window is a 60-frame ring per resource.
public sealed class ControllerTrace
{
    public const int Window = 60;
    public const string NoneName = "(none)";

    // This frame's record.
    public string MoveHolder { get; private set; }
    public string ActHolder  { get; private set; }
    public readonly List<string> MaskedThisFrame = new();

    public int Frames { get; private set; }
    public readonly Dictionary<string, (int Move, int Act)> Occupancy = new();
    public readonly Dictionary<string, int> Acquisitions = new();
    public readonly Dictionary<string, int> Preemptions  = new();
    public readonly Dictionary<string, int> Masks        = new();   // constraint -> frames it changed the input

    public readonly Oscillation MoveOsc = new(), ActOsc = new();

    public void Acquired(string name)  => Acquisitions[name] = Acquisitions.GetValueOrDefault(name) + 1;
    public void Preempted(string name) => Preemptions[name]  = Preemptions.GetValueOrDefault(name) + 1;

    internal void BeginFrame() => MaskedThisFrame.Clear();

    internal void Masked(string name)
    {
        MaskedThisFrame.Add(name);
        Masks[name] = Masks.GetValueOrDefault(name) + 1;
    }

    internal void EndFrame(string move, string act)
    {
        MoveHolder = move; ActHolder = act;
        Frames++;
        var m = Occupancy.GetValueOrDefault(move ?? NoneName);
        Occupancy[move ?? NoneName] = (m.Move + 1, m.Act);
        var a = Occupancy.GetValueOrDefault(act ?? NoneName);
        Occupancy[act ?? NoneName] = (a.Move, a.Act + 1);
        MoveOsc.Observe(move, Frames);
        ActOsc.Observe(act, Frames);
    }

    // Holder changes on one resource. `InWindow` is changes over the last Window frames,
    // `PeakInWindow` its maximum over the bout; `Flips` counts A->B->A returns to the
    // previous (non-empty) holder within Window frames of leaving it.
    public sealed class Oscillation
    {
        private readonly bool[] _ring = new bool[Window];
        private string _cur, _prevNamed, _curNamed;
        private int _leftPrevAt = int.MinValue / 2;
        private bool _started;

        public int Changes, InWindow, PeakInWindow, Flips;

        internal void Observe(string holder, int frame)
        {
            int slot = frame % Window;
            if (_ring[slot]) InWindow--;
            bool changed = _started && holder != _cur;
            _ring[slot] = changed;
            _started = true;
            if (changed)
            {
                Changes++; InWindow++;
                if (InWindow > PeakInWindow) PeakInWindow = InWindow;
            }
            _cur = holder;
            if (holder == null || holder == _curNamed) return;
            if (holder == _prevNamed && frame - _leftPrevAt <= Window) Flips++;
            _prevNamed  = _curNamed;
            _leftPrevAt = frame;
            _curNamed   = holder;
        }
    }

    public string Report()
    {
        var sb = new StringBuilder();
        int n = Math.Max(1, Frames);
        void Share(string label, Func<(int Move, int Act), int> pick)
        {
            sb.Append(label).Append(':');
            foreach (var kv in Occupancy.Where(kv => pick(kv.Value) > 0).OrderByDescending(kv => pick(kv.Value)))
                sb.Append(' ').Append(kv.Key).Append(' ').Append((100f * pick(kv.Value) / n).ToString("F1")).Append('%');
            sb.AppendLine();
        }
        sb.AppendLine($"frames {Frames}");
        Share("Move", o => o.Move);
        Share("Act ", o => o.Act);
        static string Dict(Dictionary<string, int> d)
            => d.Count == 0 ? "-" : string.Join(", ", d.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}"));
        sb.AppendLine("acquisitions: " + Dict(Acquisitions));
        sb.AppendLine("preempted:    " + Dict(Preemptions));
        if (Masks.Count > 0) sb.AppendLine("masked:       " + Dict(Masks));
        sb.AppendLine($"oscillation:  Move {MoveOsc.Changes} changes (peak {MoveOsc.PeakInWindow}/s, {MoveOsc.Flips} flips); " +
                      $"Act {ActOsc.Changes} changes (peak {ActOsc.PeakInWindow}/s, {ActOsc.Flips} flips)");
        return sb.ToString();
    }
}
