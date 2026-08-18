using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;

namespace MTile.Bench;

// Where does the JᵀJ diagonal spread come from?
//
// The diagnosis under test: most Δθ columns have no geometric constraint touching them on a
// given frame, so their only entry in J is the pose prior's √λ_limb — a near-empty column
// next to a contact-constrained one carrying √TierContact times a lever arm. That is a
// DIAGONAL defect, fixable by weights or by not solving for those bones at all. The
// alternative — a fairly uniform diagonal, with the ill-conditioning off-diagonal — would
// mean genuinely correlated columns, which neither fix addresses.
//
// So this prints, for the worst-conditioned frame of a scenario, each column's JᵀJ diagonal
// split into the geometric blocks vs the prior blocks. `geom` ≈ 0 means a free variable.
internal static class ColumnDiag
{
    private const float Scale = 0.6f;

    private static readonly HashSet<string> PriorBlocks = new()
    {
        "PlaybackContinuityConstraint", "PhaseRateFloorConstraint", "ComOffsetConstraint",
        "PosePriorConstraint", "ThetaSmoothnessConstraint",
    };

    public static void Run()
    {
        foreach (int mode in new[] { 0, 1, 2 })
        {
            AnimSolverConfig.Current.PhaseFloorMode = mode;
            Console.WriteLine();
            Console.WriteLine($"=== PhaseFloorMode = {mode} ===");
            RunOne();
        }
        AnimSolverConfig.Current.PhaseFloorMode = 0;
    }

    private static void RunOne()
    {
        string rigDir = FtolStudy.FindDir("SkeletonStates");
        if (rigDir == null) { Console.WriteLine("no SkeletonStates dir"); return; }

        foreach (var (rig, label, spawn, input) in new (string, string, Vector2, PlayerInput)[]
                 {
                     ("biped", "run", new Vector2(24f, 72f), new PlayerInput { Right = true }),
                 })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            Dump(rig, label, clipDir, spawn, input);
        }
    }

    private static void Dump(string rig, string label, string clipDir, Vector2 spawn, PlayerInput input)
    {
        List<AnimationDocument> clips;
        Skeleton skel;
        try { clips = AnimationStore.LoadAll(clipDir); skel = SkeletonExamples.Load(rig); }
        catch { return; }
        if (clips == null || clips.Count == 0) return;

        CharacterAnimator.CaptureResidualBreakdown = true;
        var anim = new CharacterAnimator(skel, Scale, clips);
        var sim  = new Simulation(FtolStudy.FlatFloor(), spawn);
        var surfaces = new SolverSurface[8];

        // Warm past the spawn drop and landing transient before sampling — the character
        // starts above the floor, and without this the "worst" frame was routinely a landing
        // spike rather than steady-state running. Matches FtolStudy.Measure's 180/600.
        const int Warm = 180, N = 600;
        for (int f = 0; f < Warm; f++) FtolStudy.Tick(sim, anim, input, surfaces, Scale);

        // The cadence solve only carries signal on frames with a planted contact; the rest
        // are flight and never solve. Collect the distribution over SOLVED frames, not the
        // max alone — one outlier frame is a bad basis for a conditioning claim.
        float worst = 0f;
        int worstFrame = -1, solved = 0;
        var ratios = new List<float>();
        List<(string Block, float[] ColSq)> snap = null, median = null;
        var snaps = new List<(float Ratio, List<(string, float[])> Cols)>();
        for (int f = 0; f < N; f++)
        {
            FtolStudy.Tick(sim, anim, input, surfaces, Scale);
            if (anim.LastSolveWork.Iterations <= 0) continue;
            solved++;
            float dr = CharacterAnimator.LastDiagRatio;
            if (float.IsInfinity(dr)) continue;
            ratios.Add(dr);
            var cols = new List<(string, float[])>();
            foreach (var (b, c) in anim.LastColumnBreakdown) cols.Add((b, (float[])c.Clone()));
            snaps.Add((dr, cols));
            if (dr > worst) { worst = dr; worstFrame = f; snap = cols; }
        }
        if (ratios.Count > 0)
        {
            snaps.Sort((a, b) => a.Ratio.CompareTo(b.Ratio));
            var mid = snaps[snaps.Count / 2];
            median = mid.Cols;
            ratios.Sort();
            Console.WriteLine();
            Console.WriteLine($"{rig}/{label}: solved {solved}/{N} frames; diag ratio " +
                              $"median {ratios[ratios.Count / 2]:G4}, p90 {ratios[(int)(ratios.Count * 0.9)]:G4}, " +
                              $"max {worst:G4} (frame {worstFrame})");
        }
        CharacterAnimator.CaptureResidualBreakdown = false;

        if (snap == null) { Console.WriteLine("no solved frames"); return; }

        Print(skel, snap, "WORST frame");
        if (median != null) Print(skel, median, "MEDIAN frame");
    }

    private static void Print(Skeleton skel, List<(string Block, float[] ColSq)> snap, string title)
    {

        int n = snap[0].ColSq.Length;
        Console.WriteLine();
        Console.WriteLine($"{title} — JᵀJ diagonal by column");
        Console.WriteLine($"{"col",-16} {"diag",12} {"geom",12} {"prior",12} {"geom share",11}");

        double gMin = double.MaxValue, gMax = 0;
        for (int a = 0; a < n; a++)
        {
            double geom = 0, prior = 0;
            foreach (var (block, colSq) in snap)
                if (PriorBlocks.Contains(block)) prior += colSq[a]; else geom += colSq[a];
            double tot = geom + prior;
            if (tot > 0) { gMin = Math.Min(gMin, tot); gMax = Math.Max(gMax, tot); }
            Console.WriteLine($"{Label(skel, a),-16} {tot,12:G4} {geom,12:G4} {prior,12:G4} " +
                              $"{(tot > 0 ? geom / tot : 0),11:P1}");
        }
        Console.WriteLine($"{"",-16} {"ratio max/min:",12} {(gMin > 0 ? gMax / gMin : 0),12:G4}");
    }

    private static string Label(Skeleton skel, int a)
        => a switch
        {
            0 => "dphi",
            1 => "delta_y",
            2 => "delta_x",
            _ => a - 3 < skel.Count ? "th:" + skel.Bones[a - 3].Name : "th:?",
        };
}
