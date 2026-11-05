using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch A/B for the routing duelist: Bulldog vs Ranger (= Bulldog + RouteManeuver,
// Bulldog's wall climb off) against the field on the league arenas, tuning seeds, both
// sides. Routing must not cost it anything on flat ground. Run by hand, Release.
public class ZzzRangerEval(ITestOutputHelper output)
{
    [Fact]
    public void AB()
    {
        var opponents = new (string, DuelBot)[]
        {
            ("Rampart", RampartBot.Create), ("Citadel", CitadelBot.Create), ("Riposte", RiposteBot.Create),
            ("Bulldog", BulldogBot.Create), ("Brawler", BotDuel.Brawler),
        };
        var bots = new ConcurrentDictionary<string, ConcurrentBag<BulldogBot>>();
        // Each challenger's brains are collected for the ledger / occupancy printouts below.
        DuelBot Collect(string name, DuelBot make) => (self, seed) =>
        {
            var bot = make(self, seed);
            if (bot is BulldogBot bb) bots.GetOrAdd(name, _ => new ConcurrentBag<BulldogBot>()).Add(bb);
            return bot;
        };
        var challengers = new (string, DuelBot)[]
        {
            ("Ranger", Collect("Ranger", RangerBot.Create)),
            ("Ranger-NoLearn", Collect("Ranger-NoLearn", RangerBot.NoLearn)),
        };
        var jobs = new List<(string C, string O, string A, int S, bool P1, DuelBot Cb, DuelBot Ob)>();
        foreach (var (cn, cb) in challengers) foreach (var (on, ob) in opponents)
        foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, 6)) foreach (bool p1 in new[] { true, false })
            jobs.Add((cn, on, a, s, p1, cb, ob));
        var pts = new ConcurrentDictionary<(string, string), float>();
        Parallel.ForEach(jobs, j =>
        {
            var r = j.P1 ? BotDuel.Run(j.A, j.Cb, j.Ob, j.S) : BotDuel.Run(j.A, j.Ob, j.Cb, j.S);
            int w = j.P1 ? r.Winner : -r.Winner;
            float p = w > 0 ? 1f : w == 0 ? 0.5f : 0f;
            pts.AddOrUpdate((j.C, j.O), p, (_, o) => o + p);
        });
        output.WriteLine("points out of 36 per opponent (3 arenas x 6 seeds x 2 sides)");
        output.WriteLine($"{"",-16}" + string.Join("", opponents.Select(o => $"{o.Item1,9}")) + "    total");
        foreach (var (cn, _) in challengers)
            output.WriteLine($"{cn,-16}" + string.Join("", opponents.Select(o => $"{pts.GetValueOrDefault((cn, o.Item1)),9:F1}"))
                             + $"  {opponents.Sum(o => pts.GetValueOrDefault((cn, o.Item1))),7:F1}");
        // The learning challenger only: mean final ledger offsets and Move-stance occupancy.
        if (bots.TryGetValue("Ranger", out var bag))
        {
            var ledgers = bag.Where(b => b.Ledger != null).Select(b => b.Ledger).ToList();
            var mean = ledgers.SelectMany(l => l.Offsets).GroupBy(o => (o.Name, o.Bucket))
                .Select(g => (g.Key, Dealt: g.Sum(o => o.DealtOffset) / ledgers.Count, Taken: g.Sum(o => o.TakenOffset) / ledgers.Count,
                              N: g.Sum(o => o.Settles)))
                .OrderByDescending(x => System.MathF.Max(System.MathF.Abs(x.Dealt), System.MathF.Abs(x.Taken))).Take(15);
            output.WriteLine($"Ranger mean final offsets over {ledgers.Count} bots (top 15 by |offset|):");
            foreach (var x in mean)
                output.WriteLine($"  {x.Key.Name,-28} {Situation.Describe(x.Key.Bucket),-16} dealt {x.Dealt,6:+0.00;-0.00} taken {x.Taken,6:+0.00;-0.00}  n={x.N}");
            // Occupancy share per behaviour, averaged over bouts (each bout weighted equally).
            var traces = bag.Where(b => b.Controller != null && b.Controller.Trace.Frames > 0).Select(b => b.Controller.Trace).ToList();
            var names = traces.SelectMany(t => t.Occupancy.Keys).Distinct().ToList();
            output.WriteLine($"Ranger occupancy share, mean over {traces.Count} bouts (Move / Act):");
            foreach (var n in names.OrderByDescending(n => traces.Average(t => t.Occupancy.GetValueOrDefault(n).Move / (double)t.Frames)
                                                          + traces.Average(t => t.Occupancy.GetValueOrDefault(n).Act / (double)t.Frames)))
                output.WriteLine($"  {n,-30} move {traces.Average(t => t.Occupancy.GetValueOrDefault(n).Move / (double)t.Frames),6:P1}"
                                 + $"  act {traces.Average(t => t.Occupancy.GetValueOrDefault(n).Act / (double)t.Frames),6:P1}");
        }
    }
}
