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
        var bandits = new ConcurrentBag<TacticBandit>();
        var learned = new ConcurrentDictionary<string, ConcurrentBag<BetLedger>>();
        // Each challenger's bet ledger is collected for the offsets printout below.
        DuelBot Collect(string name, DuelBot make) => (self, seed) =>
        {
            var bot = make(self, seed);
            if (bot is BulldogBot { Ledger: { } l }) learned.GetOrAdd(name, _ => new ConcurrentBag<BetLedger>()).Add(l);
            return bot;
        };
        var challengers = new (string, DuelBot)[]
        {
            ("Ranger", Collect("Ranger", RangerBot.Create)),
            ("Ranger-noAA", Collect("Ranger-noAA", RangerBot.NoAntiAir)),
        };
        var jobs = new List<(string C, string O, string A, int S, bool P1, DuelBot Cb, DuelBot Ob)>();
        foreach (var (cn, cb) in challengers) foreach (var (on, ob) in opponents)
        foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, 10)) foreach (bool p1 in new[] { true, false })
            jobs.Add((cn, on, a, s, p1, cb, ob));
        var pts = new ConcurrentDictionary<(string, string), float>();
        Parallel.ForEach(jobs, j =>
        {
            var r = j.P1 ? BotDuel.Run(j.A, j.Cb, j.Ob, j.S) : BotDuel.Run(j.A, j.Ob, j.Cb, j.S);
            int w = j.P1 ? r.Winner : -r.Winner;
            float p = w > 0 ? 1f : w == 0 ? 0.5f : 0f;
            pts.AddOrUpdate((j.C, j.O), p, (_, o) => o + p);
        });
        output.WriteLine("points out of 60 per opponent (3 arenas x 10 seeds x 2 sides)");
        output.WriteLine($"{"",-16}" + string.Join("", opponents.Select(o => $"{o.Item1,9}")) + "    total");
        foreach (var (cn, _) in challengers)
            output.WriteLine($"{cn,-16}" + string.Join("", opponents.Select(o => $"{pts.GetValueOrDefault((cn, o.Item1)),9:F1}"))
                             + $"  {opponents.Sum(o => pts.GetValueOrDefault((cn, o.Item1))),7:F1}");
        // Tactic weights, mean over bouts, per (decision, situation).
        foreach (var g in bandits.SelectMany(b => b.All).GroupBy(kv => kv.Key).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2))
        {
            int n = g.First().Value.Length;
            var mean = Enumerable.Range(0, n).Select(i => g.Average(kv => kv.Value[i]));
            output.WriteLine($"tactic {g.Key.Item1} ctx {g.Key.Item2} ({g.Count()} bouts): " + string.Join(" ", mean.Select(m => $"{m:F2}")));
        }
        // What the learners ended up believing: mean final bet offset per (behaviour,
        // situation), over bouts, largest first; then one bout's full ledger report.
        foreach (var (name, bag) in learned.OrderBy(k => k.Key))
        {
            var mean = bag.SelectMany(l => l.Offsets).GroupBy(o => (o.Name, o.Bucket))
                          .Select(g => (g.Key, Dealt: g.Sum(o => o.DealtOffset) / bag.Count, Taken: g.Sum(o => o.TakenOffset) / bag.Count,
                                        N: g.Sum(o => o.Settles)))
                          .OrderByDescending(x => System.MathF.Max(System.MathF.Abs(x.Dealt), System.MathF.Abs(x.Taken))).Take(16);
            output.WriteLine($"{name} mean final offsets over {bag.Count} bouts:");
            foreach (var x in mean)
                output.WriteLine($"  {x.Key.Name,-24} {Situation.Describe(x.Key.Bucket),-16} dealt {x.Dealt,6:+0.00;-0.00} taken {x.Taken,6:+0.00;-0.00}  n={x.N}");
            output.WriteLine(bag.First().Report());
        }
    }
}
