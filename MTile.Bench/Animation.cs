using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using MTile.Bench;

// The RENDER-side half of the harness: the per-frame cost of the animation solver.
//
// Why this belongs in a perf bench at all — the sim is bounded by design (fixed timestep,
// budgeted in Program.Budget), but the animator runs an iterative least-squares solve per
// character per frame with no equivalent budget, and its cost scales with the number of
// active constraints (contacts, terrain half-planes, overlays) rather than with anything
// the frame loop controls. On desktop that hides under a 16.7 ms budget; in the browser it
// competes with everything else inside ~25 ms.
//
// Headless: CharacterAnimator is pure math over SkeletonPose, so it needs no
// GraphicsDevice. The samples are driven off a REAL Simulation through the exact call
// path CosmeticUpdateSystem uses (TerrainSurfaces.Extract → CharacterAnimSample.From →
// Update), so what's measured is what ships, not a synthetic pose stream.
internal static class AnimationBench
{
    public static void Run(BenchReport report)
    {
        string rigDir = FindDir("SkeletonStates");
        string skelDir = FindDir("Skeletons");
        if (rigDir == null || skelDir == null)
        {
            Console.WriteLine("anim: SkeletonStates/ not found from the CWD — skipped.");
            return;
        }

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            Measure(report, rig, clipDir, "run",  new Vector2(24f, 72f), new PlayerInput { Right = true });
            Measure(report, rig, clipDir, "idle", new Vector2(100f, 72f), default);
            // Same idle pose with the terrain half-planes withheld. SolveStaticPose's
            // dormancy gate is supposed to skip the LM solve when no masked tip presses
            // past a plane; the gap between this row and the one above IS the cost of
            // the frames where that gate fires anyway. If they are far apart, standing
            // still is paying for a 12-iteration solve that changes nothing.
            Measure(report, rig, clipDir, "idle (no terrain)", new Vector2(100f, 72f), default,
                    feedTerrain: false);
        }
    }

    private static void Measure(BenchReport report, string rig, string clipDir, string label,
                                Vector2 spawn, PlayerInput input, bool feedTerrain = true)
    {
        List<AnimationDocument> clips;
        Skeleton skel;
        try
        {
            clips = AnimationStore.LoadAll(clipDir);
            skel  = SkeletonExamples.Load(rig);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"anim {rig}/{label}: load failed ({ex.Message}) — skipped.");
            return;
        }
        if (clips == null || clips.Count == 0) return;

        const float Scale = 0.6f;
        const int Frames = 1800, Bucket = 60;
        var surfaces = new SolverSurface[8];

        // Min over reps, for the reason spelled out in Program.Measure: noise only adds.
        double bestUs = double.MaxValue, worstBucketUs = 0;
        for (int rep = 0; rep < Bench.Reps; rep++)
        {
            var anim = new CharacterAnimator(skel, Scale, clips);
            var sim  = new Simulation(FlatFloor(), spawn);

            // Warm up JIT + let the solver settle into a steady cadence before timing; a
            // cold first solve is dominated by first-touch costs that never recur in play.
            for (int f = 0; f < 180; f++) Tick(sim, anim, input, surfaces, Scale, feedTerrain);

            var sw = new Stopwatch();
            var total = Stopwatch.StartNew();
            for (int b = 0; b < Frames / Bucket; b++)
            {
                sw.Restart();
                for (int f = 0; f < Bucket; f++) Tick(sim, anim, input, surfaces, Scale, feedTerrain);
                sw.Stop();
                worstBucketUs = Math.Max(worstBucketUs, sw.Elapsed.TotalMilliseconds * 1000.0 / Bucket);
            }
            total.Stop();
            bestUs = Math.Min(bestUs, total.Elapsed.TotalMilliseconds * 1000.0 / Frames);
        }
        // The sim step is inside the loop (the animator needs a live body to track), so
        // subtract nothing and say so: this is "sim + one animator solve" per frame, which
        // is also exactly what one rendered frame costs on the CPU.
        report.Add($"anim {rig}/{label} (+sim)", bestUs, worstBucketUs);
    }

    private static void Tick(Simulation sim, CharacterAnimator anim, PlayerInput input,
                             SolverSurface[] surfaces, float scale, bool feedTerrain)
    {
        sim.Step(input);
        var p = sim.Player;
        int tc = 0; bool near = false;
        if (feedTerrain)
            tc = TerrainSurfaces.Extract(sim.Chunks, anim, p.Body.Position, p.Facing, scale,
                                         surfaces, out near);
        anim.Update(CharacterAnimSample.From(p, Simulation.FixedDt, surfaces, tc, near, sim.Chunks));
    }

    private static ChunkMap FlatFloor() => SimTerrain.FromAscii(
        new string('X', 80), originTileX: 0, originTileY: 6);

    // Walk up from the binary toward the repo root looking for an asset dir — the bench
    // runs from bin/Release/net8.0 but reads clips out of the source tree.
    private static string FindDir(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, name);
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return null;
    }
}
