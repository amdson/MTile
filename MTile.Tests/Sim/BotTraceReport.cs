using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MTile.Net;

namespace MTile.Tests.Sim;

// Shared printout of the controller trace + bet ledger for the Zzz league/eval harnesses
// (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Trace and evaluation"). Everything is averaged
// over the bouts the bot name played, each bout weighted equally.
public static class BotTraceReport
{
    public static string Format(string name, IEnumerable<BulldogBot> bots, int top = 10)
    {
        var all = bots.ToList();
        var traces = all.Where(b => b.Controller != null && b.Controller.Trace.Frames > 0).Select(b => b.Controller.Trace).ToList();
        var sb = new StringBuilder();
        if (traces.Count == 0) return sb.AppendLine($"{name}: no controller traces").ToString();

        double Mean(Func<ControllerTrace, double> f) => traces.Average(f);
        var names = traces.SelectMany(t => t.Occupancy.Keys).Distinct().ToList();
        sb.AppendLine($"{name} trace, mean over {traces.Count} bouts");
        void Occ(string label, Func<(int Move, int Act), int> pick)
        {
            sb.AppendLine($"  {label} occupancy share (top {top}):");
            foreach (var x in names.Select(n => (n, v: Mean(t => pick(t.Occupancy.GetValueOrDefault(n)) / (double)t.Frames)))
                                   .Where(x => x.v > 0).OrderByDescending(x => x.v).Take(top))
                sb.AppendLine($"    {x.n,-30} {x.v,6:P1}");
        }
        Occ("Move", o => o.Move);
        Occ("Act", o => o.Act);

        sb.AppendLine($"  acquisitions {Mean(t => t.Acquisitions.Values.Sum()):F1}/bout, preemptions suffered {Mean(t => t.Preemptions.Values.Sum()):F1}/bout");
        sb.AppendLine($"  oscillation/bout: Move {Mean(t => t.MoveOsc.Changes):F1} changes (peak {Mean(t => t.MoveOsc.PeakInWindow):F1}/s, {Mean(t => t.MoveOsc.Flips):F1} flips); "
                      + $"Act {Mean(t => t.ActOsc.Changes):F1} changes (peak {Mean(t => t.ActOsc.PeakInWindow):F1}/s, {Mean(t => t.ActOsc.Flips):F1} flips)");

        var ledgers = all.Where(b => b.Ledger != null).Select(b => b.Ledger).ToList();
        if (ledgers.Count > 0)
        {
            var mean = ledgers.SelectMany(l => l.Offsets).GroupBy(o => (o.Name, o.Bucket))
                .Select(g => (g.Key, Dealt: g.Sum(o => o.DealtOffset) / ledgers.Count, Taken: g.Sum(o => o.TakenOffset) / ledgers.Count,
                              N: g.Sum(o => o.Settles)))
                .OrderByDescending(x => MathF.Max(MathF.Abs(x.Dealt), MathF.Abs(x.Taken))).Take(top);
            sb.AppendLine($"  learned offsets, mean over {ledgers.Count} ledgers (top {top} by |offset|):");
            foreach (var x in mean)
                sb.AppendLine($"    {x.Key.Name,-28} {Situation.Describe(x.Key.Bucket),-16} dealt {x.Dealt,6:+0.00;-0.00} taken {x.Taken,6:+0.00;-0.00}  n={x.N}");
        }
        return sb.ToString();
    }
}
