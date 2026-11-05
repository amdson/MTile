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
        var challengers = new (string, DuelBot)[]
        {
            ("ledger+softmax",   Collect("ledger+softmax", RangerBot.Create)),
            ("ledger+argmax",    Collect("ledger+argmax", RangerBot.LearnArgmax)),
            ("nolearn+argmax",   Collect("nolearn+argmax", RangerBot.NoLearn)),
            ("nolearn+softmax",  Collect("nolearn+softmax", RangerBot.NoLearnExplore)),
        };
        var jobs = new List<(string C, string O, string A, int S, bool P1, DuelBot Cb, DuelBot Ob)>();
        foreach (var (cn, cb) in challengers) foreach (var (on, ob) in Opponents)
        foreach (var a in BotDuel.Arenas) foreach (int s in Enumerable.Range(1, 6)) foreach (bool p1 in new[] { true, false })
            jobs.Add((cn, on, a, s, p1, cb, ob));
        var pts = new ConcurrentDictionary<(string, string), float>();
        Parallel.ForEach(jobs, j => { float p = Points(j.A, j.Cb, j.Ob, j.S, j.P1); pts.AddOrUpdate((j.C, j.O), p, (_, o) => o + p); });
        output.WriteLine("A. points out of 36 per opponent (3 arenas x 6 seeds x 2 sides)");
        output.WriteLine($"{"",-18}" + string.Join("", Opponents.Select(o => $"{o.Item1,9}")) + "    total  divergence");
        foreach (var (cn, _) in challengers)
        {
            var bag = bots.GetValueOrDefault(cn);
            float div = bag == null ? 0 : bag.Where(b => b.Controller != null && b.Controller.ActContests > 0)
                                             .Select(b => (float)b.Controller.ActDivergences / b.Controller.ActContests).DefaultIfEmpty(0).Average();
            output.WriteLine($"{cn,-18}" + string.Join("", Opponents.Select(o => $"{pts.GetValueOrDefault((cn, o.Item1)),9:F1}"))
                             + $"  {Opponents.Sum(o => pts.GetValueOrDefault((cn, o.Item1))),7:F1}  {div,6:P1}");
        }
    }

    [Fact]
    public void CarriedLedger()
    {
        const int Bouts = 6;
        var seqs = new List<(string O, DuelBot Ob, string A, bool P1)>();
        foreach (var (on, ob) in Opponents) foreach (var a in BotDuel.Arenas) foreach (bool p1 in new[] { true, false })
            seqs.Add((on, ob, a, p1));
        var carried = new ConcurrentBag<(string O, float[] ByBout)>();
        var fresh   = new ConcurrentBag<(string O, float[] ByBout)>();
        Parallel.ForEach(seqs, sq =>
        {
            var ledger = new BetLedger();
            var me = RangerBot.Carry(ledger);
            var c = new float[Bouts]; var f = new float[Bouts];
            for (int i = 0; i < Bouts; i++)
            {
                c[i] = Points(sq.A, me, sq.Ob, i + 1, sq.P1);
                f[i] = Points(sq.A, RangerBot.NoLearn, sq.Ob, i + 1, sq.P1);
            }
            carried.Add((sq.O, c)); fresh.Add((sq.O, f));
        });
        output.WriteLine("B. one ledger carried across 6 bouts vs the same opponent (points per bout position, summed over 3 arenas x 2 sides = max 6 per cell)");
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
        float cEarly = carried.Sum(x => x.ByBout[0] + x.ByBout[1]), cLate = carried.Sum(x => x.ByBout[4] + x.ByBout[5]);
        float fEarly = fresh.Sum(x => x.ByBout[0] + x.ByBout[1]),   fLate = fresh.Sum(x => x.ByBout[4] + x.ByBout[5]);
        output.WriteLine($"carry: bouts 1-2 {cEarly:F1}  bouts 5-6 {cLate:F1}   fresh: bouts 1-2 {fEarly:F1}  bouts 5-6 {fLate:F1}  (max 60 each)");
    }
}
