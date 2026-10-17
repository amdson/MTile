using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile;

namespace MTile.Bench;

// Plans/WEB_COOP.md step 6: is the co-op "depths" stage cheap enough for browser rollback?
//
//   dotnet run -c Release --project MTile.Bench -- --depths [room[:wave] ...]
//
// For each (room, wave) it builds a fresh two-player Simulation from Stages.Get("depths"),
// drops both players in the room (as DepthsStageTests does), advances to the wave, lets the
// enemies engage for 5 s, and times per-frame Step(p0,p1) / Snapshot() / Restore() /
// Checksum() plus a full rollback frame (restore the snapshot 6 frames back, replay 6 x
// [Snapshot+Step+Checksum], then the new frame) -- RollbackSession's worst case, since
// InputFrameDelay + StallSlack caps the replay depth at 6.
internal static class DepthsBench
{
    private static readonly int EngageFrames = int.Parse(Environment.GetEnvironmentVariable("DEPTHS_FRAMES") ?? "300");
    private const int RollbackDepth = 6;

    private static readonly (int room, int wave)[] DefaultCases =
    {
        (13, 0), (0, 0), (7, 0), (12, 0), (13, 0), (13, 1), (13, 2), (18, 0), (18, 1), (19, 0), (19, 1),
    };

    private static double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;

    private static void Kill(Simulation g)
    {
        foreach (var e in g.Entities.ToList()) if ((e is EnemyEntity or StalkerEnemy or TurretEnemy or EnemySpawner)) e.Health = 0f;
    }

    private static void Place(Simulation g, DepthsRoom r)
    {
        var p = r.FloorSpots[0] - new Vector2(0f, 6f);
        g.Player.Body.Position = p; g.Player.Body.Velocity = Vector2.Zero;
        foreach (var (sp, _) in g.SecondaryPlayers)
        { sp.Body.Position = p + new Vector2(24f, 0f); sp.Body.Velocity = Vector2.Zero; }
    }

    private static void KeepAlive(Simulation g)
    {
        g.Player.Health = g.Player.MaxHealth;
        foreach (var (sp, _) in g.SecondaryPlayers) sp.Health = sp.MaxHealth;
    }

    // Scripted-ish input so players move/jump/attack a bit instead of standing still.
    private static PlayerInput Inp(int f, int who)
    {
        int ph = (f / 45 + who) % 4;
        return new PlayerInput { Right = ph == 0 || ph == 1, Left = ph == 3, Space = f % 50 < 6, LeftClick = f % 40 < 5, };
    }

    private static string Stat(List<double> v)
    {
        if (v.Count == 0) return "-";
        v.Sort();
        return $"{v[v.Count / 2],7:F0} {v[(int)(v.Count * 0.95)],7:F0} {v[^1],7:F0}";
    }

    public static int Run(string[] args)
    {
        SimTrace.Enabled = false;
        var cases = new List<(int, int)>();
        foreach (var a in args)
        {
            if (a.StartsWith("--") || !char.IsDigit(a[0])) continue;
            var s = a.Split(':');
            cases.Add((int.Parse(s[0]), s.Length > 1 ? int.Parse(s[1]) : 0));
        }
        if (cases.Count == 0) cases.AddRange(DefaultCases);

        // Boot cost (Stages.Get + new Simulation); first call includes JIT/cold file I/O.
        Console.WriteLine("boot (Stages.Get + new Simulation, 2 players):");
        var cfg = new GameConfig { SpawnSecondPlayer = true };
        for (int i = 0; i < 4; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var st = Stages.Get("depths");
            long t1 = Stopwatch.GetTimestamp();
            var gg = new Simulation(cfg, st);
            long t2 = Stopwatch.GetTimestamp();
            Console.WriteLine($"  run {i}: Stages.Get {Us(t1 - t0) / 1000:F1} ms, new Simulation {Us(t2 - t1) / 1000:F1} ms");
        }
        Console.WriteLine();
        Console.WriteLine("all times in us/frame: median p95 max");
        Console.WriteLine($"{"case",-26}{"ents",5}{"nest",5} | {"Step",24} | {"Snapshot",24} | {"Restore",24} | {"Checksum",24} | {"rollback frame (depth 6)",24} | activation-frame / wave-advance max");

        foreach (var (room, wave) in cases)
        {
            var stage = Stages.Get("depths");
            var g = new Simulation(cfg, stage);
            var rooms = DepthsLayout.Build(7, Stages.DepthsShapes()).Rooms;
            var plan = Stages.DepthsPlan[room];
            g.StageVars[0] = room;
            Place(g, rooms[room]);

            // Frames around the room activation (spawn of wave 0) and each later wave advance.
            double activationMax = 0, advanceMax = 0;
            int fr = 0;
            for (int i = 0; i < 10; i++)
            {
                long a = Stopwatch.GetTimestamp(); g.Step(Inp(fr, 0), Inp(fr, 1)); fr++;
                activationMax = Math.Max(activationMax, Us(Stopwatch.GetTimestamp() - a));
                KeepAlive(g);
            }
            for (int w = 0; w < wave; w++)
            {
                Kill(g);
                for (int i = 0; i < 120; i++)
                {
                    long a = Stopwatch.GetTimestamp(); g.Step(Inp(fr, 0), Inp(fr, 1)); fr++;
                    advanceMax = Math.Max(advanceMax, Us(Stopwatch.GetTimestamp() - a));
                    KeepAlive(g);
                }
            }
            // Warm the JIT / settle.
            for (int i = 0; i < 60; i++) { g.Step(Inp(fr, 0), Inp(fr, 1)); fr++; KeepAlive(g); }

            var step = new List<double>(); var snapT = new List<double>(); var restT = new List<double>();
            var csT = new List<double>(); var rbT = new List<double>();
            long allocStep = 0, allocSnap = 0; var pause0 = GC.GetTotalPauseDuration(); int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            var ring = new SimSnapshot[RollbackDepth + 1];
            int maxEnt = 0, maxNest = 0; string mix = "";
            for (int i = 0; i < EngageFrames; i++)
            {
                var p0 = Inp(fr, 0); var p1 = Inp(fr, 1);
                long al0 = GC.GetAllocatedBytesForCurrentThread();
                long a = Stopwatch.GetTimestamp();
                var snap = g.Snapshot();
                long b = Stopwatch.GetTimestamp();
                long al1 = GC.GetAllocatedBytesForCurrentThread();
                g.Restore(snap);
                long c = Stopwatch.GetTimestamp();
                long al2 = GC.GetAllocatedBytesForCurrentThread();
                g.Step(p0, p1);
                long d = Stopwatch.GetTimestamp();
                allocStep += GC.GetAllocatedBytesForCurrentThread() - al2; allocSnap += al1 - al0;
                g.Checksum();
                long e = Stopwatch.GetTimestamp();
                snapT.Add(Us(b - a)); restT.Add(Us(c - b)); step.Add(Us(d - c)); csT.Add(Us(e - d));
                ring[i % ring.Length] = snap;
                fr++; KeepAlive(g);

                if (i % 5 == 0 && i >= RollbackDepth)
                {
                    // Worst-case rollback tick. Replays the same inputs, so the timeline is unchanged.
                    var old = ring[(i - RollbackDepth + 1) % ring.Length];
                    int start = fr - RollbackDepth;
                    long r0 = Stopwatch.GetTimestamp();
                    g.Restore(old);
                    for (int k = 0; k < RollbackDepth; k++)
                    { g.Snapshot(); g.Step(Inp(start + k, 0), Inp(start + k, 1)); g.Checksum(); }
                    g.Snapshot(); g.Step(Inp(fr, 0), Inp(fr, 1)); g.Checksum(); fr++;
                    rbT.Add(Us(Stopwatch.GetTimestamp() - r0));
                    KeepAlive(g);
                }
                if (i % 10 != 0) continue;
                int ents = g.Entities.Count(x => !x.IsDead);
                int nests = g.Entities.Count(x => x is EnemySpawner);
                if (ents > maxEnt)
                {
                    maxEnt = ents; maxNest = nests;
                    mix = string.Join(",", g.Entities.Where(x => !x.IsDead).GroupBy(x => x.GetType().Name).Select(q => $"{q.Key}={q.Count()}"));
                }
            }
            Console.WriteLine($"{room + ":" + wave + " " + plan.Name,-26}{maxEnt,5}{maxNest,5} | {Stat(step)} | {Stat(snapT)} | {Stat(restT)} | {Stat(csT)} | {Stat(rbT)} | {activationMax:F0} / {advanceMax:F0}");
            Console.WriteLine($"    alloc/frame: Step {allocStep / EngageFrames} B, Snapshot {allocSnap / EngageFrames} B; mean us: step {step.Average():F0} snap {snapT.Average():F0} restore {restT.Average():F0} cksum {csT.Average():F0}; GC pause total {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:F1} ms over run; GCs gen0/1/2: {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}");
            Console.WriteLine($"    mix: {mix}");
        }
        return 0;
    }
}
