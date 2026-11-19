using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch sanity check for the bots' plant play (Plans/PLANT_SEEDS_PLAN.md §8b): Ranger
// and Bulldog with the two plant maneuvers off ("before") vs on ("after"), against Bulldog
// and the Brawler, on the garden stage (dirt, so plants happen) and two stone league
// arenas (no dirt: plants must change nothing). Not a tuning target. Run by hand, Release.
public class ZzzPlantBotEval(ITestOutputHelper output)
{
    private static readonly string[] PlantNames = { nameof(PlantSpitterManeuver), nameof(PlantStrikeManeuver) };

    private static DuelBot NoPlants(Func<int, int, BulldogBot> make)
        => (self, seed) => { var b = make(self, seed); foreach (var n in PlantNames) b.Disabled.Add(n); return b; };

    private static string LevelsFile(string file)
    {
        for (var d = System.IO.Directory.GetCurrentDirectory(); d != null; d = System.IO.Path.GetDirectoryName(d))
        {
            string c = System.IO.Path.Combine(d, "Levels", file);
            if (System.IO.File.Exists(c)) return c;
        }
        throw new System.IO.FileNotFoundException(file);
    }

    private static Simulation Garden()
    {
        var reg = Stages.Get("garden");
        var stage = new Stage
        {
            Name = reg.Name, TerrainConfig = LevelsFile("garden.json"), Versus = true,
            PlayerSpawn = reg.PlayerSpawn, SecondSpawn = reg.SecondSpawn, Populate = reg.Populate,
        };
        return new Simulation(new GameConfig { SpawnSecondPlayer = true }, stage);
    }

    [Fact]
    public void BeforeAfter()
    {
        var planters = new ConcurrentBag<BulldogBot>();
        DuelBot Track(Func<int, int, BulldogBot> make) => (self, seed) => { var b = make(self, seed); planters.Add(b); return b; };

        Func<int, int, BulldogBot> ranger  = (self, seed) => RangerBot.Build(seed, self, out _);
        Func<int, int, BulldogBot> bulldog = (self, seed) => new BulldogBot(seed, self);

        var variants = new (string Name, DuelBot Ranger, DuelBot Bulldog)[]
        {
            ("before", NoPlants(ranger), NoPlants(bulldog)),
            ("after",  Track(ranger),    Track(bulldog)),
        };
        string[] arenas = { "garden", "hall_close", "plain_close" };
        int[] seeds = { 1, 2, 3 };

        var jobs = new List<(string V, string O, string A, int S, bool P1, DuelBot R, DuelBot Ob)>();
        foreach (var (vn, rb, bb) in variants)
        foreach (var (on, ob) in new (string, DuelBot)[] { ("Bulldog", bb), ("Brawler", BotDuel.Brawler) })
        foreach (var a in arenas) foreach (int s in seeds) foreach (bool p1 in new[] { true, false })
            jobs.Add((vn, on, a, s, p1, rb, ob));

        var pts = new ConcurrentDictionary<(string, string, string), float>();
        var kills = new ConcurrentDictionary<(string, string), (int K, int D)>();
        var draws0 = new ConcurrentDictionary<string, int>();
        Parallel.ForEach(jobs, j =>
        {
            var r = j.A == "garden"
                ? (j.P1 ? BotDuel.Run(Garden(), "garden", j.R, j.Ob, j.S) : BotDuel.Run(Garden(), "garden", j.Ob, j.R, j.S))
                : (j.P1 ? BotDuel.Run(j.A, j.R, j.Ob, j.S) : BotDuel.Run(j.A, j.Ob, j.R, j.S));
            int w = j.P1 ? r.Winner : -r.Winner;
            float p = w > 0 ? 1f : w == 0 ? 0.5f : 0f;
            pts.AddOrUpdate((j.V, j.O, j.A), p, (_, o) => o + p);
            int k = j.P1 ? r.KillsByP1 : r.KillsByP2, dth = j.P1 ? r.KillsByP2 : r.KillsByP1;
            kills.AddOrUpdate((j.V, j.A), (k, dth), (_, o) => (o.K + k, o.D + dth));
            if (r.KillsByP1 + r.KillsByP2 == 0 && r.DamageToP1 + r.DamageToP2 < 0.5f) draws0.AddOrUpdate(j.V, 1, (_, o) => o + 1);
            lock (output) output.WriteLine($"{j.V,-6} Ranger({(j.P1 ? "P1" : "P2")}) vs {j.O}: {r}");
        });

        output.WriteLine("\nRanger points out of 6 per cell (3 seeds x 2 sides)");
        foreach (var (vn, _, _) in variants)
        {
            output.WriteLine($"{vn}:");
            foreach (var on in new[] { "Bulldog", "Brawler" })
                output.WriteLine($"  vs {on,-8}" + string.Join("", arenas.Select(a => $"  {a} {pts.GetValueOrDefault((vn, on, a)):F1}")));
            output.WriteLine("  Ranger K/D" + string.Join("", arenas.Select(a => $"  {a} {kills.GetValueOrDefault((vn, a)).K}/{kills.GetValueOrDefault((vn, a)).D}"))
                             + $"   stalled (no kills, <0.5 dmg): {draws0.GetValueOrDefault(vn)}");
        }
        int planted = 0, strikes = 0;
        foreach (var b in planters)
        {
            b.Starts.TryGetValue(nameof(PlantSpitterManeuver), out int ps);
            b.Starts.TryGetValue(nameof(PlantStrikeManeuver), out int ss);
            planted += ps; strikes += ss;
        }
        output.WriteLine($"after: PlantSpitter acquisitions {planted}, PlantStrike acquisitions {strikes} (all bots, all bouts)");
    }
}
