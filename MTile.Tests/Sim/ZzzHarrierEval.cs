using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch A/B for the hit-and-run counter: Bulldog vs Harrier (= Bulldog + WhiffPunish)
// against the field, same seeds, both sides. Not a regression test; run by hand in Release.
public class ZzzHarrierEval(ITestOutputHelper output)
{
    private static readonly int[] Seeds = Enumerable.Range(1, 10).ToArray();

    [Fact]
    public void AB()
    {
        var opponents = new (string, DuelBot)[]
        {
            ("Rampart", RampartBot.Create), ("Citadel", CitadelBot.Create),
            // Rampart reduced to its hit-and-run: melee, guard and backoff only — no
            // terrain, no spikes, no waves. The pure form of the question.
            ("Runner", (self, seed) =>
            {
                var r = new RampartBot(seed, self);
                foreach (var n in new[] { "RampartPulse", "RampartEnclose", "RampartCrystalWave",
                                          "RampartAvalancheCrush", "RampartWallUp", "RampartSpikeMoat", "BreachManeuver" })
                    r.Disabled.Add(n);
                return r;
            }),
            ("Riposte", RiposteBot.Create), ("Bulldog", BulldogBot.Create), ("Brawler", BotDuel.Brawler),
        };
        var only = Environment.GetEnvironmentVariable("HARRIER_OPP");   // e.g. "Rampart,Citadel"
        if (!string.IsNullOrEmpty(only)) opponents = opponents.Where(o => only.Split(',').Contains(o.Item1)).ToArray();
        // HARRIER_BAIT="80:95,88:102" — one Harrier variant per BaitIn:BaitOut pair.
        var baits = (Environment.GetEnvironmentVariable("HARRIER_BAIT") ?? "80:95").Split(',')
            .Select(x => x.Split(':').Select(float.Parse).ToArray()).ToArray();
        var bags = new Dictionary<string, ConcurrentBag<WhiffPunishManeuver>>();
        var challengers = new List<(string, DuelBot)> { ("Bulldog", BulldogBot.Create) };
        foreach (var bt in baits)
        {
            string name = $"H{bt[0]}:{bt[1]}";
            var bag = bags[name] = new ConcurrentBag<WhiffPunishManeuver>();
            challengers.Add((name, (self, seed) =>
            {
                var b = HarrierBot.Build(seed, self, out var p);
                p.BaitIn = bt[0]; p.BaitOut = bt[1];
                bag.Add(p); return b;
            }));
        }

        var jobs = new List<(string C, string O, string Arena, int Seed, bool CIsP1, DuelBot Cb, DuelBot Ob)>();
        foreach (var (cn, cb) in challengers)
        foreach (var (on, ob) in opponents)
        foreach (var arena in BotDuel.Arenas)
        foreach (int s in Seeds)
        foreach (bool p1 in new[] { true, false })
            jobs.Add((cn, on, arena, s, p1, cb, ob));

        var pts = new ConcurrentDictionary<(string, string, string), float>();
        Parallel.ForEach(jobs, j =>
        {
            var r = j.CIsP1 ? BotDuel.Run(j.Arena, j.Cb, j.Ob, j.Seed) : BotDuel.Run(j.Arena, j.Ob, j.Cb, j.Seed);
            int w = j.CIsP1 ? r.Winner : -r.Winner;
            float p = w > 0 ? 1f : w == 0 ? 0.5f : 0f;
            pts.AddOrUpdate((j.C, j.O, j.Arena), p, (_, old) => old + p);
        });

        int per = Seeds.Length * 2;
        output.WriteLine($"points out of {per} per arena ({per * 3} total)");
        foreach (var (on, _) in opponents)
        foreach (var (cn, _) in challengers)
        {
            var by = BotDuel.Arenas.Select(a => pts.GetValueOrDefault((cn, on, a))).ToArray();
            output.WriteLine($"{cn,-8} vs {on,-8} total {by.Sum(),5:F1}   " +
                             string.Join("  ", BotDuel.Arenas.Zip(by, (a, x) => $"{a} {x:F1}")));
        }
        foreach (var (name, bag) in bags) {
        var P = bag.ToArray();
        output.WriteLine(name);
        output.WriteLine($"WhiffPunish: {P.Sum(p => p.Baits)} baits ({P.Sum(p => p.BaitHits)} hit while baiting), " +
                         $"{P.Sum(p => p.Punishes)} punishes: {P.Sum(p => p.PunishHit)} landed, {P.Sum(p => p.PunishGotHit)} got hit, " +
                         $"{P.Sum(p => p.PunishNoClick)} never in click range, {P.Sum(p => p.PunishNoDash)} no dash; {P.Sum(p => p.PunishDamage):F1} HP");
        foreach (var g in P.SelectMany(p => p.HitBy).GroupBy(kv => kv.Key).OrderByDescending(g => g.Sum(kv => kv.Value)))
            output.WriteLine($"   hit by {g.Key}: {g.Sum(kv => kv.Value)}");
        output.WriteLine("trigger gap buckets (10px): " + string.Join(" ", Enumerable.Range(0, 16).Select(i => $"{i * 10}:{P.Sum(p => p.TriggerGap[i])}")));
        }
    }
}
