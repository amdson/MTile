using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;

namespace MTile.Bench;

// `--diag`: structural breakdown of the animation solve, as opposed to the wall-clock
// totals the main harness reports.
//
// The totals alone are ambiguous. "idle costs 2.2 ms, idle-without-terrain costs 90 µs"
// is consistent with three very different stories: extraction is slow, extraction feeds
// the solver too many planes, or the solver's row layout is wasteful regardless of how
// many planes arrive. Those want different fixes, so this splits them:
//
//   extract_us  — TerrainSurfaces.Extract alone (the tile scan + coplanar merge)
//   update_us   — CharacterAnimator.Update alone (sampling, compose, and the LM solve)
//   surfaces    — how many half-planes actually reach the solver per frame
//   nopen_rows  — surfaces × bones, the no-penetration block's contribution to m
//   live_rows   — of those, how many can EVER be nonzero (bones in some BoneMask)
//   solved      — share of frames on which the LM solve ran at all
//
// `live_rows` vs `nopen_rows` is the number to look at: terrain planes are only ever
// extracted for the 7 tips in TerrainSurfaces.TipNames, but the residual layout emits a
// row per (surface, bone) over the WHOLE rig.
internal static class AnimDiag
{
    public static void Run()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) { Console.WriteLine("diag: SkeletonStates/ not found."); return; }

        Console.WriteLine();
        LeastSquaresSolver.ProfileCallbacks = true;
        Console.WriteLine($"{"scenario",-20} {"extract",8} {"update",8} {"srf",4} {"live/nopen",11} {"solved",7} {"iters",6} {"evals",6} {"m",5} {"resid",7} {"jacob",7} {"algebra",8} {"condA~",10} {"condA max",10} {"diag~",10} {"diag max",10}");

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            Diag(rig, clipDir, "idle", new Vector2(100f, 72f), default);
            Diag(rig, clipDir, "run",  new Vector2(24f, 72f), new PlayerInput { Right = true });
        }
    }

    private static void Diag(string rig, string clipDir, string label, Vector2 spawn, PlayerInput input)
    {
        List<AnimationDocument> clips;
        Skeleton skel;
        try { clips = AnimationStore.LoadAll(clipDir); skel = SkeletonExamples.Load(rig); }
        catch (Exception ex) { Console.WriteLine($"diag {rig}/{label}: {ex.Message}"); return; }
        if (clips == null || clips.Count == 0) return;

        const float Scale = 0.6f;
        var anim = new CharacterAnimator(skel, Scale, clips);
        var sim  = new Simulation(FlatFloor(), spawn);
        var surfaces = new SolverSurface[8];
        int bones = skel.Count;

        for (int f = 0; f < 180; f++)   // warmup
        {
            sim.Step(input);
            var pw = sim.Player;
            int tcw = TerrainSurfaces.Extract(sim.Chunks, anim, pw.Body.Position, pw.Facing, Scale,
                                              surfaces, out bool nw);
            anim.Update(CharacterAnimSample.From(pw, Simulation.FixedDt, surfaces, tcw, nw, sim.Chunks));
        }

        const int N = 600;
        var swE = new Stopwatch();
        var swU = new Stopwatch();
        double srfSum = 0, liveSum = 0, iterSum = 0, evalSum = 0; int solved = 0;
        long rT = 0, jT = 0, aT = 0; double rowSum = 0;
        double prSum = 0, prMax = 0; int prN = 0;
        double drSum = 0, drMax = 0; int drN = 0;
        for (int f = 0; f < N; f++)
        {
            sim.Step(input);
            var p = sim.Player;

            swE.Start();
            int tc = TerrainSurfaces.Extract(sim.Chunks, anim, p.Body.Position, p.Facing, Scale,
                                             surfaces, out bool near);
            swE.Stop();

            swU.Start();
            anim.Update(CharacterAnimSample.From(p, Simulation.FixedDt, surfaces, tc, near, sim.Chunks));
            swU.Stop();

            float pr = CharacterAnimator.LastPivotRatio;
            if (!float.IsInfinity(pr) && pr > 0f) { prSum += Math.Log10(pr); prN++; prMax = Math.Max(prMax, pr); }
            float dr = CharacterAnimator.LastDiagRatio;
            if (!float.IsInfinity(dr) && dr > 0f) { drSum += Math.Log10(dr); drN++; drMax = Math.Max(drMax, dr); }
            var dbg = anim.CaptureDebug();
            if (dbg.Solved)
            {
                solved++;
                var w = anim.LastSolveWork;
                iterSum += w.Iterations;
                evalSum += w.ResidualEvals + w.JacobianEvals;
                var t = anim.LastSolveTicks;
                rT += t.Residual; jT += t.Jacobian; aT += t.Algebra;
                rowSum += anim.LastSolveRows;
            }
            srfSum += dbg.Surfaces.Length;
            // Rows that could ever carry a nonzero residual: for each surface, the count
            // of bones actually named in its mask. Everything else in the block is a
            // structural zero the solver still allocates, zeroes, and multiplies through.
            foreach (var s in dbg.Surfaces)
                for (int b = 0; b < bones; b++)
                    if (((s.BoneMask >> b) & 1) != 0) liveSum++;
        }

        double srf = srfSum / N;
        double updUs = swU.Elapsed.TotalMilliseconds * 1000.0 / N;
        // Per-frame averages: solve costs are divided by N (all frames), so the three tick
        // columns sum to the solve's share of `update`. The gap between that sum and
        // `update` is the non-solve part of Update — sampling, blending, the fast path.
        double toUs = 1e6 / Stopwatch.Frequency / N;
        Console.WriteLine($"{rig + "/" + label,-20} {swE.Elapsed.TotalMilliseconds * 1000.0 / N,8:F1} " +
                          $"{updUs,8:F1} {srf,4:F1} " +
                          $"{(liveSum / N).ToString("F2") + "/" + (srf * bones).ToString("F0"),11} " +
                          $"{(double)solved / N,7:P0} {(solved > 0 ? iterSum / solved : 0),6:F1} " +
                          $"{(solved > 0 ? evalSum / solved : 0),6:F1} {(solved > 0 ? rowSum / solved : 0),5:F0} " +
                          $"{rT * toUs,7:F1} {jT * toUs,7:F1} {aT * toUs,8:F1} " +
                          $"{(prN > 0 ? Math.Pow(10, prSum / prN) : 0),10:E1} {prMax,10:E1} " +
                          $"{(drN > 0 ? Math.Pow(10, drSum / drN) : 0),10:E1} {drMax,10:E1}");
    }

    // Cost of the two halves of BuildSolvePose, which runs once per residual/Jacobian
    // evaluation (~40× per solve). On the STATIC path Δφ is hard-locked to zero
    // (SolveStaticPose pins _solveLo/_solveHi[IdxPhi] = 0), so the sample+compose half
    // returns a bit-identical pose on every one of those evaluations while only the
    // Δθ-add + FK half depends on x. This measures what hoisting the invariant half out
    // of the residual function would be worth.
    public static void PoseSplit()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;
        Console.WriteLine();
        Console.WriteLine($"{"rig",-16} {"sample+compose",16} {"FK (ComputeWorld)",18}  (µs per BuildSolvePose half)");

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            List<AnimationDocument> clips;
            Skeleton skel;
            try { clips = AnimationStore.LoadAll(clipDir); skel = SkeletonExamples.Load(rig); }
            catch { continue; }
            var idle = clips.Find(c => c.Name == "idle") ?? clips[0];

            var pose = new SkeletonPose(skel);
            var kfA = new SkeletonPose(skel); var kfB = new SkeletonPose(skel);
            var kfC = new SkeletonPose(skel); var kfD = new SkeletonPose(skel);
            var root = Affine2.FromTRS(new Vector2(100f, 72f), 0f, new Vector2(0.6f, 0.6f));

            const int M = 20000;
            for (int i = 0; i < 2000; i++)   // warmup
            { AnimationSampler.SampleSmooth(idle, 0.3f, kfA, kfB, kfC, kfD, pose); pose.ComputeWorld(root); }

            var sw1 = Stopwatch.StartNew();
            for (int i = 0; i < M; i++) AnimationSampler.SampleSmooth(idle, 0.3f, kfA, kfB, kfC, kfD, pose);
            sw1.Stop();
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < M; i++) pose.ComputeWorld(root);
            sw2.Stop();

            Console.WriteLine($"{rig,-16} {sw1.Elapsed.TotalMilliseconds * 1000.0 / M,16:F2} " +
                              $"{sw2.Elapsed.TotalMilliseconds * 1000.0 / M,18:F2}");
        }
    }

    // Does the iteration budget BUY anything? LeastSquaresSolver stops only on the
    // iteration cap, an absolute cost floor, or total failure to find a downhill step — it
    // has none of the three standard LM convergence tests (relative cost reduction ftol,
    // step size xtol, gradient norm gtol). So it will happily spend eight more iterations
    // shaving a fraction of a percent off the cost, which no one can see on screen.
    //
    // Prints the cost after each iteration as a fraction of the starting cost, so the
    // iteration at which the solve is visually done can be read straight off.
    public static void Convergence()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;
        Console.WriteLine();
        Console.WriteLine("per-iteration relative cost reduction (mean over solved frames):");

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            Trace(rig, clipDir, "idle", new Vector2(100f, 72f), default);
            Trace(rig, clipDir, "run",  new Vector2(24f, 72f), new PlayerInput { Right = true });
        }
    }

    private static void Trace(string rig, string clipDir, string label, Vector2 spawn, PlayerInput input)
    {
        List<AnimationDocument> clips;
        Skeleton skel;
        try { clips = AnimationStore.LoadAll(clipDir); skel = SkeletonExamples.Load(rig); }
        catch { return; }
        if (clips == null || clips.Count == 0) return;

        const float Scale = 0.6f;
        var anim = new CharacterAnimator(skel, Scale, clips);
        var sim  = new Simulation(FlatFloor(), spawn);
        var surfaces = new SolverSurface[8];
        LeastSquaresSolver.ProfileCallbacks = true;

        var sum = new double[16];
        var hits = new int[16];
        for (int f = 0; f < 780; f++)
        {
            sim.Step(input);
            var p = sim.Player;
            int tc = TerrainSurfaces.Extract(sim.Chunks, anim, p.Body.Position, p.Facing, Scale,
                                             surfaces, out bool near);
            anim.Update(CharacterAnimSample.From(p, Simulation.FixedDt, surfaces, tc, near, sim.Chunks));
            if (f < 180) continue;                       // warmup
            var tr = anim.LastSolveCostTrace;
            if (tr.Length < 2 || tr[0] <= 0f) continue;
            for (int i = 0; i < tr.Length && i < 16; i++) { sum[i] += tr[i] / tr[0]; hits[i]++; }
        }

        if (hits[0] == 0) { Console.WriteLine($"  {rig}/{label,-6} (no solves)"); return; }
        var sb = new System.Text.StringBuilder();
        sb.Append($"  {rig + "/" + label,-22}");
        // Per-iteration RELATIVE REDUCTION, not the cost fraction: the question this answers
        // is whether a late iteration is still buying anything measurable, and at what scale
        // — a reduction near float epsilon means the accept/reject test is a coin flip.
        for (int i = 1; i < 16 && hits[i] > 0; i++)
        {
            double prev = sum[i - 1] / hits[i - 1], cur = sum[i] / hits[i];
            sb.Append($" {(prev > 0 ? (prev - cur) / prev : 0),9:E1}");
        }
        Console.WriteLine(sb.ToString());
    }

    private static ChunkMap FlatFloor() => SimTerrain.FromAscii(
        new string('X', 80), originTileX: 0, originTileY: 6);

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
