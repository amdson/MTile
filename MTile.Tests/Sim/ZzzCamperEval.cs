using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Plans/BOT_ROUTING_PLAN.md: how well does each bot follow a player who stacks a column
// and camps on it? CamperBot is P1; the bot under test is P2. Scratch harness, Release.
public class ZzzCamperEval(ITestOutputHelper output)
{
    public static readonly (string Name, DuelBot Bot)[] Chasers =
    {
        ("Brawler", BotDuel.Brawler), ("Bulldog", BulldogBot.Create), ("Riposte", RiposteBot.Create),
        ("Citadel", CitadelBot.Create), ("Rampart", RampartBot.Create), ("Ranger", RangerBot.Create),
    };

    public record Stat(string Bot, string Arena, int Seed, float DmgToCamper, float DmgToBot, int FirstHit, float Height, int Placed);

    public static Stat Bout(string name, DuelBot bot, string arena, int seed, int frames = 3600, int tower = 0)
    {
        var sim = BotDuel.BuildArena(arena);
        var camper = new CamperBot(-1) { TowerRows = tower };
        var b = bot(0, seed);
        var p1 = sim.Player; var p2 = sim.SecondaryPlayers[0].Player;
        float prev1 = 0, prev2 = 0, d1 = 0, d2 = 0, startY = p1.Body.Position.Y, minY = startY;
        int first = -1;
        for (int f = 0; f < frames; f++)
        {
            sim.Step(camper.Poll(sim, f), b.Poll(sim, f));
            float a = p1.Combat.DamageTaken; d1 += a < prev1 ? a : a - prev1; prev1 = a;
            float c = p2.Combat.DamageTaken; d2 += c < prev2 ? c : c - prev2; prev2 = c;
            if (first < 0 && d1 > 0f) first = f;
            minY = MathF.Min(minY, p1.Body.Position.Y);
        }
        return new Stat(name, arena, seed, d1, d2, first, (startY - minY) / Chunk.TileSize, camper.Placed);
    }

    [Fact]
    public void Baseline()
    {
        var only = Environment.GetEnvironmentVariable("CAMPER_BOTS");
        var bots = Chasers.Where(c => string.IsNullOrEmpty(only) || only.Split(',').Contains(c.Name)).ToArray();
        var jobs = new List<(string, DuelBot, string, int)>();
        int tower = int.Parse(Environment.GetEnvironmentVariable("CAMPER_TOWER") ?? "0");
        var arenas = tower > 0 ? new[] { "plain_close" } : new[] { "plain_close", "hall_far" };
        foreach (var (n, b) in bots) foreach (var a in arenas) foreach (int s in new[] { 1, 2, 3 }) jobs.Add((n, b, a, s));
        var res = new ConcurrentBag<Stat>();
        Parallel.ForEach(jobs, j => res.Add(Bout(j.Item1, j.Item2, j.Item3, j.Item4, tower: tower)));
        foreach (var s in res.OrderBy(r => r.Bot).ThenBy(r => r.Arena).ThenBy(r => r.Seed))
            output.WriteLine($"{s.Bot,-8} {s.Arena,-12} s{s.Seed}: camper -{s.DmgToCamper:F1} (first hit {s.FirstHit}), bot -{s.DmgToBot:F1}, column {s.Height:F0} cells, {s.Placed} place presses");
        foreach (var g in res.GroupBy(r => r.Bot))
            output.WriteLine($"== {g.Key}: dealt {g.Sum(r => r.DmgToCamper):F1}, took {g.Sum(r => r.DmgToBot):F1}, bouts with a hit {g.Count(r => r.FirstHit >= 0)}/{g.Count()}");
    }
}

public class ZzzCamperTrace(ITestOutputHelper output)
{
    [Fact]
    public void RangerOpening()
    {
        var sim = BotDuel.BuildArena("plain_close");
        var camper = new CamperBot(-1) { TowerRows = 15 };
        var b = RangerBot.Build(3, 0, out var route);
        route.Log = output.WriteLine;
        var p1 = sim.Player; var p2 = sim.SecondaryPlayers[0].Player;
        string last = "";
        for (int f = 0; f < 1300; f++)
        {
            var i2 = b.Poll(sim, f);
            sim.Step(camper.Poll(sim, f), i2);
            string now = $"{b.Active?.Name} move {b.Controller?.Trace.MoveHolder} route {(b.Planner as RoutePlanner)?.Running} {route.Follower.Current}";
            if (now != last || f % 60 == 0)
                output.WriteLine($"f{f} {now} | p2 {p2.Body.Position} {p2.CurrentStateName} hp {p2.Health:F1} | camper y {p1.Body.Position.Y:F0} hp {p1.Health:F1}");
            last = now;
        }
        output.WriteLine($"plans {route.Plans} fails {route.Failures} reached {route.Reached} climbs {route.PlannedClimbs}");
    }
}

public class ZzzRangerCost(ITestOutputHelper output)
{
    [Fact]
    public void PollCost()
    {
        foreach (var (name, bot) in new (string, DuelBot)[] { ("Bulldog", BulldogBot.Create), ("Ranger", RangerBot.Create) })
        {
            var sim = BotDuel.BuildArena("plain_close");
            var camper = new CamperBot(-1) { TowerRows = 15 };
            var b = bot(0, 3);
            var sw = new System.Diagnostics.Stopwatch(); long worst = 0;
            for (int f = 0; f < 2400; f++)
            {
                long t0 = sw.ElapsedTicks; sw.Start();
                var i2 = b.Poll(sim, f);
                sw.Stop(); worst = Math.Max(worst, sw.ElapsedTicks - t0);
                sim.Step(camper.Poll(sim, f), i2);
            }
            output.WriteLine($"{name}: avg {sw.Elapsed.TotalMilliseconds * 1000 / 2400:F0} us/poll, worst {worst * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F2} ms");
        }
    }
}
