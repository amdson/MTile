using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Is the bet ledger doing anything? Three isolating runs (Release, by hand):
//   A. Ranger variants vs the field over seeds 1-6: ledger+softmax (the default), ledger+argmax,
//      no ledger+argmax (NoLearn), no ledger+softmax. Plus the share of Act contests where the
//      corrections changed the argmax.
//   B. One ledger carried across 6 sequential bouts against the same opponent on the same
//      arena/side: points by bout position, so adaptation over a longer session is visible.
public class ZzzLearningEffect(ITestOutputHelper output)
{
    private const int VariantSeeds = 24;

    private static readonly (string, DuelBot)[] Opponents =
    {
        ("Rampart", RampartBot.Create), ("Citadel", CitadelBot.Create), ("Riposte", RiposteBot.Create),
        ("Bulldog", BulldogBot.Create), ("Brawler", BotDuel.Brawler),
    };

    private static float Points(string arena, DuelBot me, DuelBot them, int seed, bool p1)
    {
        var r = p1 ? BotDuel.Run(arena, me, them, seed) : BotDuel.Run(arena, them, me, seed);
        int w = p1 ? r.Winner : -r.Winner;
        return w > 0 ? 1f : w == 0 ? 0.5f : 0f;
    }

    [Fact]
    public void Variants()
    {
        var bots = new ConcurrentDictionary<string, ConcurrentBag<BulldogBot>>();
        DuelBot Collect(string name, DuelBot make) => (self, seed) =>
        {
            var bot = make(self, seed);
            if (bot is BulldogBot bb) bots.GetOrAdd(name, _ => new ConcurrentBag<BulldogBot>()).Add(bb);
            return bot;
        };
        DuelBot Calib(Action<BulldogBot> more = null) => RangerBot.Variant(b => { b.Calibration = BetCalibration.Field; more?.Invoke(b); });
        var challengers = new (string, DuelBot)[]
        {
            ("nolearn+argmax",  Collect("nolearn+argmax", RangerBot.NoLearn)),
            ("calib+nolearn",   Collect("calib+nolearn", Calib(b => b.Ledger = null))),
            ("default",         Collect("default", RangerBot.Create)),
        };
        var jobs = new List<(string C, string O, string A, int S, bool P1, DuelBot Cb, DuelBot Ob)>();
        foreach (var (cn, cb) in challengers) foreach (var (on, ob) in Opponents)
        foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, VariantSeeds)) foreach (bool p1 in new[] { true, false })
            jobs.Add((cn, on, a, s, p1, cb, ob));
        var pts = new ConcurrentDictionary<(string, string), float>();
        Parallel.ForEach(jobs, j => { float p = Points(j.A, j.Cb, j.Ob, j.S, j.P1); pts.AddOrUpdate((j.C, j.O), p, (_, o) => o + p); });
        output.WriteLine($"A. points out of {6 * VariantSeeds} per opponent (3 arenas x {VariantSeeds} seeds x 2 sides)");
        output.WriteLine($"{"",-18}" + string.Join("", Opponents.Select(o => $"{o.Item1,9}")) + "    total  divergence");
        foreach (var (cn, _) in challengers)
        {
            var bag = bots.GetValueOrDefault(cn);
            float div = bag == null ? 0 : bag.Where(b => b.Controller != null && b.Controller.ActContests > 0)
                                             .Select(b => (float)b.Controller.ActDivergences / b.Controller.ActContests).DefaultIfEmpty(0).Average();
            output.WriteLine($"{cn,-18}" + string.Join("", Opponents.Select(o => $"{pts.GetValueOrDefault((cn, o.Item1)),9:F1}"))
                             + $"  {Opponents.Sum(o => pts.GetValueOrDefault((cn, o.Item1))),7:F1}  {div,6:P1}");
        }
        foreach (var name in new[] { "default" })
            if (bots.TryGetValue(name, out var bag)) output.WriteLine(BotTraceReport.Format(name, bag, 12));
    }

    // What does each arm realise, per bucket, under the hand policy (shadow ledger) vs under
    // the learner's own argmax policy? Close-range buckets only; the fight happens there.
    [Fact]
    public void RealisedByArm()
    {
        var variants = new (string, Func<BulldogBot> , ConcurrentBag<BulldogBot>)[0];
        var shadowBag = new ConcurrentBag<BulldogBot>(); var learnBag = new ConcurrentBag<BulldogBot>();
        DuelBot shadow = (self, seed) => { var b = RangerBot.Build(seed, self, out _); b.Shadow = true; shadowBag.Add(b); return b; };
        DuelBot learn  = (self, seed) => { var b = RangerBot.Build(seed, self, out _); b.ExploreOverride = false; learnBag.Add(b); return b; };
        var jobs = new List<(string, DuelBot, string, int, bool, DuelBot)>();
        foreach (var (on, ob) in Opponents) foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, 6)) foreach (bool p1 in new[] { true, false })
        { jobs.Add(("shadow", shadow, a, s, p1, ob)); jobs.Add(("learn", learn, a, s, p1, ob)); }
        var pts = new ConcurrentDictionary<string, float>();
        Parallel.ForEach(jobs, j => { float p = Points(j.Item3, j.Item2, j.Item6, j.Item4, j.Item5); pts.AddOrUpdate(j.Item1, p, (_, o) => o + p); });
        output.WriteLine($"points /180: shadow {pts["shadow"]:F1}  learn-argmax {pts["learn"]:F1}");
        foreach (var (label, bag) in new[] { ("shadow (hand policy)", shadowBag), ("learn-argmax (own policy)", learnBag) })
        {
            output.WriteLine($"-- {label}: realised mean per arm, buckets <40 and 40-85 (gnd), n = settles summed over {bag.Count} bots");
            var agg = new Dictionary<(string, int), (double d, double t, int n)>();
            foreach (var b in bag)
                foreach (var (name, bucket, d, t, n) in b.Ledger.Realised)
                {
                    if (bucket >= 12 || bucket % 2 == 1) continue;   // bands 0-1, grounded
                    var cur = agg.GetValueOrDefault((name, bucket));
                    agg[(name, bucket)] = (cur.d + d * n, cur.t + t * n, cur.n + n);
                }
            foreach (var kv in agg.Where(k => k.Value.n >= 60).OrderBy(k => k.Key.Item2).ThenByDescending(k => k.Value.d / k.Value.n - k.Value.t / k.Value.n))
            {
                var (d, t, n) = kv.Value;
                output.WriteLine($"   {Situation.Describe(kv.Key.Item2),-14} {kv.Key.Item1,-26} dealt {d / n,6:F3} taken {t / n,6:F3} value {d / n - t / n,7:F3}  n={n}");
            }
        }
    }

    // Dump a calibration table: per arm, realised mean dealt/taken over ALL buckets under the
    // hand policy (shadow ledger), weighted by settles. Paste into Net/Bot/Control/BetCalibration.cs.
    [Fact]
    public void CalibrationDump()
    {
        var bag = new ConcurrentBag<BulldogBot>();
        DuelBot shadow = (self, seed) => { var b = RangerBot.Build(seed, self, out _); b.Shadow = true; bag.Add(b); return b; };
        var jobs = new List<(string, int, bool, DuelBot)>();
        foreach (var (_, ob) in Opponents) foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, 6)) foreach (bool p1 in new[] { true, false })
            jobs.Add((a, s, p1, ob));
        Parallel.ForEach(jobs, j => Points(j.Item1, shadow, j.Item4, j.Item2, j.Item3));
        var agg = new Dictionary<string, (double d, double t, int n)>();
        foreach (var b in bag)
            foreach (var (name, bucket, d, t, n) in b.Ledger.Realised)
            {
                if (bucket != -1) continue;   // the arm-level entry already sums every bucket
                var cur = agg.GetValueOrDefault(name);
                agg[name] = (cur.d + d * n, cur.t + t * n, cur.n + n);
            }
        output.WriteLine($"// Realised means under the hand policy, {bag.Count} bots x 60 s vs Rampart/Citadel/Riposte/Bulldog/Brawler (ZzzLearningEffect.CalibrationDump)");
        foreach (var kv in agg.Where(k => k.Value.n >= 30).OrderBy(k => k.Key))
        {
            var (d, t, n) = kv.Value;
            output.WriteLine($"        [\"{kv.Key}\"] = ({d / n:F3}f, {t / n:F3}f),   // n={n}");
        }
    }

    [Fact]
    public void CarriedLedger()
    {
        const int Bouts = 8;
        var seqs = new List<(string O, DuelBot Ob, string A, bool P1)>();
        foreach (var (on, ob) in Opponents) foreach (var a in BotDuel.Arenas) foreach (bool p1 in new[] { true, false })
            seqs.Add((on, ob, a, p1));
        var carried = new ConcurrentBag<(string O, float[] ByBout)>();
        var fresh   = new ConcurrentBag<(string O, float[] ByBout)>();
        Parallel.ForEach(seqs, sq =>
        {
            var ledger = new BetLedger();
            // The default learner (RangerBot.Create) with one ledger shared across the sequence,
            // vs the default learner with a fresh ledger per bout.
            DuelBot me = (self, seed) => { var b = RangerBot.Build(seed, self, out _); ledger.NewBout(); b.Ledger = ledger; return b; };
            DuelBot fr = RangerBot.Create;
            var c = new float[Bouts]; var f = new float[Bouts];
            for (int i = 0; i < Bouts; i++)
            {
                c[i] = Points(sq.A, me, sq.Ob, i + 1, sq.P1);
                f[i] = Points(sq.A, fr, sq.Ob, i + 1, sq.P1);
            }
            carried.Add((sq.O, c)); fresh.Add((sq.O, f));
        });
        output.WriteLine("B. default learner: one ledger carried across 8 bouts vs the same opponent, vs a fresh ledger per bout (points per bout position, summed over 3 arenas x 2 sides = max 6 per cell)");
        output.WriteLine($"{"",-10}{"variant",-8}" + string.Join("", Enumerable.Range(1, Bouts).Select(i => $"  b{i}")) + "  total");
        foreach (var (on, _) in Opponents)
        {
            foreach (var (label, bag) in new[] { ("carry", carried), ("fresh", fresh) })
            {
                var rows = bag.Where(x => x.O == on).Select(x => x.ByBout).ToList();
                var sum = Enumerable.Range(0, Bouts).Select(i => rows.Sum(r => r[i])).ToArray();
                output.WriteLine($"{on,-10}{label,-8}" + string.Join("", sum.Select(x => $"{x,4:F1}")) + $"  {sum.Sum(),5:F1}");
            }
        }
        float cEarly = carried.Sum(x => x.ByBout[0] + x.ByBout[1]), cLate = carried.Sum(x => x.ByBout[Bouts - 2] + x.ByBout[Bouts - 1]);
        float fEarly = fresh.Sum(x => x.ByBout[0] + x.ByBout[1]),   fLate = fresh.Sum(x => x.ByBout[Bouts - 2] + x.ByBout[Bouts - 1]);
        output.WriteLine($"carry: bouts 1-2 {cEarly:F1}  last two {cLate:F1}   fresh: bouts 1-2 {fEarly:F1}  last two {fLate:F1}  (max 60 each)");
    }
}

public class ZzzLearningTiming(ITestOutputHelper output)
{
    // Every bout of the sweep for two variants, in parallel; report the slow ones.
    [Fact]
    public void SlowBouts()
    {
        var variants = new (string, DuelBot)[]
        {
            ("default",      RangerBot.Create),
            ("nonewstances", RangerBot.Variant(b => b.ExtraStances.RemoveAll(s => s is PressStance or KiteStance))),
        };
        var opps = new (string, DuelBot)[] { ("Rampart", RampartBot.Create), ("Citadel", CitadelBot.Create), ("Riposte", RiposteBot.Create), ("Bulldog", BulldogBot.Create), ("Brawler", BotDuel.Brawler) };
        var jobs = new List<(string V, DuelBot Vb, string O, DuelBot Ob, string A, int S, bool P1)>();
        foreach (var (vn, vb) in variants) foreach (var (on, ob) in opps) foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, 12)) foreach (bool p1 in new[] { true, false })
            jobs.Add((vn, vb, on, ob, a, s, p1));
        var slow = new ConcurrentBag<string>();
        var total = new ConcurrentDictionary<string, long>();
        Parallel.ForEach(jobs, j =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (j.P1) BotDuel.Run(j.A, j.Vb, j.Ob, j.S); else BotDuel.Run(j.A, j.Ob, j.Vb, j.S);
            total.AddOrUpdate(j.V, sw.ElapsedMilliseconds, (_, o) => o + sw.ElapsedMilliseconds);
            if (sw.ElapsedMilliseconds > 4000) slow.Add($"{j.V,-14} {j.A,-11} vs {j.O,-8} s{j.S} {(j.P1 ? "P1" : "P2")} {sw.ElapsedMilliseconds,7} ms");
        });
        foreach (var kv in total) output.WriteLine($"{kv.Key,-14} total {kv.Value / 1000.0:F0} s over {jobs.Count / variants.Length} bouts");
        foreach (var l in slow.OrderByDescending(x => x)) output.WriteLine(l);
    }

    [Fact]
    public void OneBoutEach()
    {
        var variants = new (string, DuelBot)[]
        {
            ("nolearn",      RangerBot.NoLearn),
            ("default",      RangerBot.Create),
            ("nonewstances", RangerBot.Variant(b => b.ExtraStances.RemoveAll(s => s is PressStance or KiteStance))),
            ("nopress",      RangerBot.Variant(b => b.ExtraStances.RemoveAll(s => s is PressStance))),
            ("nokite",       RangerBot.Variant(b => b.ExtraStances.RemoveAll(s => s is KiteStance))),
            ("oldgap",       RangerBot.Variant(b => b.StanceGap = 0.35f)),
        };
        var opps = new (string, DuelBot)[] { ("Rampart", RampartBot.Create), ("Citadel", CitadelBot.Create), ("Riposte", RiposteBot.Create), ("Bulldog", BulldogBot.Create), ("Brawler", BotDuel.Brawler) };
        foreach (var (name, bot) in variants.Take(3))
        foreach (var arena in BotDuel.Arenas)
        foreach (var opp in opps)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = BotDuel.Run(arena, bot, opp.Item2, 2);
            output.WriteLine($"{name,-14} {arena,-11} vs {opp.Item1,-8} {sw.ElapsedMilliseconds,6} ms  {r}");
        }
    }
}
