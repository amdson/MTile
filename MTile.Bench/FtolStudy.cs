using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;

namespace MTile.Bench;

// What a standard relative-cost stopping test is worth, measured rather than projected.
//
// The convergence trace shows idle reaching 0.001 of its starting cost after ONE iteration
// and then flatlining for nine more, because LeastSquaresSolver had no ftol. This sweeps
// LeastSquaresSolver.Ftol and reports iterations and wall-clock at each setting, so the
// speedup can be weighed against how much the solved pose moves.
//
// `maxdev` is the largest per-bone angle difference (degrees) between the ftol pose and the
// ftol=0 pose on the same frame — the number that decides whether this is visible. A tenth
// of a degree on a 19px-tall character is not.
internal static class FtolStudy
{
    // Mean final solve cost of the last Measure() call — the quality metric for changes
    // that alter the LM path (see LeastSquaresSolver.LastCost).
    private static double LastMeanCost;

    // Mean per-constraint-block sum of squares of the last Measure() call — the same total
    // LastMeanCost reports, split by which block produced it. Requires
    // CharacterAnimator.CaptureResidualBreakdown.
    private static readonly Dictionary<string, double> LastBlockCost = new();
    private static readonly Dictionary<string, int> LastBlockRows = new();

    // Mean |δ| and |d.x| (world px) over the solved frames of the last Measure() — the RAW
    // magnitudes behind ComOffsetConstraint, whose residual is nondimensionalized by rig reach.
    private static double LastMeanDy, LastMeanDx;

    public static void Run()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;
        Console.WriteLine();
        Console.WriteLine("StaticFtol sweep (0 = no convergence test, the historical behaviour):");
        Console.WriteLine($"{"scenario",-22} {"ftol",8} {"iters",7} {"update us",11} {"speedup",8} {"maxdev deg",11}");

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            Sweep(rig, clipDir, "idle", new Vector2(100f, 72f), default);
            Sweep(rig, clipDir, "run",  new Vector2(24f, 72f), new PlayerInput { Right = true });
        }
        AnimSolverConfig.Current.StaticFtol = 0f;   // leave the process as we found it
    }

    private static void Sweep(string rig, string clipDir, string label, Vector2 spawn, PlayerInput input)
    {
        double baseUs = 0;
        float[][] reference = null;
        foreach (float ftol in new[] { 0f, 1e-4f, 1e-3f, 1e-2f })
        {
            AnimSolverConfig.Current.StaticFtol = ftol;
            var (us, iters, poses) = Measure(rig, clipDir, spawn, input);
            if (poses == null) return;
            if (ftol == 0f) { baseUs = us; reference = poses; }

            float maxDev = 0f;
            if (reference != null && reference != poses)
                for (int f = 0; f < poses.Length && f < reference.Length; f++)
                    for (int b = 0; b < poses[f].Length; b++)
                        maxDev = MathF.Max(maxDev, MathF.Abs(poses[f][b] - reference[f][b]));

            Console.WriteLine($"{(ftol == 0f ? rig + "/" + label : ""),-22} {ftol,8:G2} {iters,7:F1} " +
                              $"{us,11:F1} {(baseUs > 0 ? baseUs / us : 1),8:F2}x " +
                              $"{maxDev * 180f / MathF.PI,11:F3}");
        }
    }


    // Scalar vs vectorized normal equations, pose-for-pose.
    //
    // The lane split changes the summation order, so this is NOT a pure speed change: the
    // LM's trust-region path depends on the exact JᵀJ, and the iteration counts move. So
    // the honest question is not "how much faster" alone but "how much faster, and does the
    // drawn pose move" — `maxdev` is the largest per-bone angle difference (degrees) against
    // the scalar build on the same frame.
    public static void SimdCheck()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;
        Console.WriteLine();
        Console.WriteLine($"normal equations: scalar vs Vector<float> " +
                          $"[Vector<float>.Count={System.Numerics.Vector<float>.Count}]");
        Console.WriteLine($"{"scenario",-22} {"scalar us",10} {"vector us",10} {"speedup",8} " +
                          $"{"iters s>v",10} {"maxdev deg",11} {"cost scal",12} {"cost vec",12}");

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            Pair(rig, clipDir, "idle", new Vector2(100f, 72f), default);
            Pair(rig, clipDir, "run",  new Vector2(24f, 72f), new PlayerInput { Right = true });
        }
        AnimSolverConfig.Current.StaticVectorize = true;
    }

    private static void Pair(string rig, string clipDir, string label, Vector2 spawn, PlayerInput input)
    {
        AnimSolverConfig.Current.StaticVectorize = false;
        var (us0, it0, p0) = Measure(rig, clipDir, spawn, input);
        double cost0 = LastMeanCost;
        AnimSolverConfig.Current.StaticVectorize = true;
        var (us1, it1, p1) = Measure(rig, clipDir, spawn, input);
        double cost1 = LastMeanCost;
        if (p0 == null || p1 == null) return;

        float maxDev = 0f;
        for (int f = 0; f < p0.Length && f < p1.Length; f++)
        {
            if (p0[f] == null || p1[f] == null) continue;
            for (int b = 0; b < p0[f].Length; b++)
                maxDev = MathF.Max(maxDev, MathF.Abs(p0[f][b] - p1[f][b]));
        }

        Console.WriteLine($"{rig + "/" + label,-22} {us0,10:F1} {us1,10:F1} {(us1 > 0 ? us0 / us1 : 0),8:F2}x " +
                          $"{it0.ToString("F1") + ">" + it1.ToString("F1"),10} {maxDev * 180f / MathF.PI,11:F3} " +
                          $"{cost0,12:G4} {cost1,12:G4}");
    }

    // Normal equations vs QR on the real scenarios.
    //
    // Two questions, and they are separate. (1) Does QR cost what the structure predicts —
    // roughly 3× the algebra, offset by never building JᵀJ? (2) Does it remove the
    // fragility that keeps CadenceVectorize off — i.e. with QR in place, does reordering the
    // sum still move the answer? A solver working on J (cond ~1e3) instead of JᵀJ (~1e6)
    // should not care.
    // Three routes to the SAME damped system, to separate two different defects:
    //   float NE  — forms JᵀJ in float32: suffers both within-sum cancellation (the loss
    //               weights span ~5..5000, so w² spans ~1e6 inside every dot product) and
    //               the squared condition number of the factorization.
    //   double NE — same formation and same factorization, wider accumulator: removes the
    //               cancellation, leaves the squared conditioning.
    //   QR        — never forms JᵀJ: removes the squared conditioning.
    // Whichever column moves the cost tells us which defect was actually costing us.
    // Jacobi scaling is forced OFF throughout so all three share one damping metric.
    // Per-penalty cost breakdown, written as CSV.
    //
    // The single cost number says a route found a better answer without saying what it made
    // better — and the blocks are not interchangeable. A hard constraint (weight 4700) and a
    // limb pose prior (weight 4) contribute to the same sum, so an apparent 10× win could be
    // a real geometric improvement or could be the solver quietly trading a satisfied
    // constraint for a relaxed prior. This splits the same total by block so the two read
    // differently.
    //
    // Columns: scenario, method, block, rows, sumsq, share of that method's total.
    public static void BreakdownCsv(string path)
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;

        var rows = new List<string> { "scenario,method,block,rows,sumsq,share" };
        bool savedJacobi = LeastSquaresSolver.JacobiScale;
        LeastSquaresSolver.JacobiScale = false;
        CharacterAnimator.CaptureResidualBreakdown = true;

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            foreach (var (label, spawn, input) in new (string, Vector2, PlayerInput)[]
                     {
                         ("idle", new Vector2(100f, 72f), default),
                         ("run",  new Vector2(24f, 72f), new PlayerInput { Right = true }),
                     })
            {
                foreach (string method in new[] { "f32", "f64", "f64_to_f32", "qr" })
                {
                    LeastSquaresSolver.UseQr = method == "qr";
                    LeastSquaresSolver.DoubleAccum = method is "f64" or "f64_to_f32";
                    LeastSquaresSolver.DoubleAccumRoundToFloat = method == "f64_to_f32";

                    Measure(rig, clipDir, spawn, input);

                    Console.WriteLine($"  {rig}/{label,-5} {method,-11} " +
                                      $"mean |dy|={LastMeanDy,7:F3}px  mean dphi={LastMeanDx,8:F5}");

                    double total = 0;
                    foreach (var kv in LastBlockCost) total += kv.Value;
                    foreach (var kv in LastBlockCost)
                        rows.Add($"{rig}/{label},{method},{kv.Key},{LastBlockRows[kv.Key]}," +
                                 $"{kv.Value:G6},{(total > 0 ? kv.Value / total : 0):G4}");
                }
            }
        }

        CharacterAnimator.CaptureResidualBreakdown = false;
        LeastSquaresSolver.UseQr = false;
        LeastSquaresSolver.DoubleAccum = false;
        LeastSquaresSolver.DoubleAccumRoundToFloat = false;
        LeastSquaresSolver.JacobiScale = savedJacobi;

        File.WriteAllLines(path, rows);
        Console.WriteLine($"wrote {rows.Count - 1} rows to {path}");
    }

    public static void DoubleCheck()
    {
        foreach (int mode in new[] { 0, 1, 2 })
        {
            AnimSolverConfig.Current.PhaseFloorMode = mode;
            Console.WriteLine();
            Console.WriteLine("=== PhaseFloorMode = " + mode + " ===");
            DoubleCheckOne();
        }
        AnimSolverConfig.Current.PhaseFloorMode = 0;
    }

    private static void DoubleCheckOne()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;
        Console.WriteLine();
        Console.WriteLine("float NE vs double NE vs QR (same damped system, three routes):");
        Console.WriteLine($"{"scenario",-22} {"f32 us",8} {"f64 us",8} {"QR us",8} " +
                          $"{"cost f32",11} {"cost f64",11} {"f64->f32",11} {"cost QR",11} {"iters",18}");

        bool savedJacobi = LeastSquaresSolver.JacobiScale;
        LeastSquaresSolver.JacobiScale = false;
        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            DoublePair(rig, clipDir, "idle", new Vector2(100f, 72f), default);
            DoublePair(rig, clipDir, "run",  new Vector2(24f, 72f), new PlayerInput { Right = true });
        }
        LeastSquaresSolver.JacobiScale = savedJacobi;
        LeastSquaresSolver.DoubleAccum = false;
        LeastSquaresSolver.UseQr = false;
    }

    private static void DoublePair(string rig, string clipDir, string label, Vector2 spawn, PlayerInput input)
    {
        LeastSquaresSolver.UseQr = false; LeastSquaresSolver.DoubleAccum = false;
        var (us0, it0, _) = Measure(rig, clipDir, spawn, input);
        double c0 = LastMeanCost;

        LeastSquaresSolver.DoubleAccum = true;
        var (us1, it1, _) = Measure(rig, clipDir, spawn, input);
        double c1 = LastMeanCost;

        // Double accumulation, then rounded back to float32 and factored in float: isolates
        // what the wide FORMATION is worth from what the wide FACTORIZATION is worth.
        LeastSquaresSolver.DoubleAccumRoundToFloat = true;
        var (us3, it3, _) = Measure(rig, clipDir, spawn, input);
        double c3 = LastMeanCost;
        LeastSquaresSolver.DoubleAccumRoundToFloat = false;

        LeastSquaresSolver.DoubleAccum = false; LeastSquaresSolver.UseQr = true;
        var (us2, it2, _) = Measure(rig, clipDir, spawn, input);
        double c2 = LastMeanCost;
        LeastSquaresSolver.UseQr = false;

        Console.WriteLine($"{rig + "/" + label,-22} {us0,8:F1} {us1,8:F1} {us2,8:F1} " +
                          $"{c0,11:G4} {c1,11:G4} {c3,11:G4} {c2,11:G4} " +
                          $"{it0.ToString("F1") + ">" + it1.ToString("F1") + ">" +
                           it3.ToString("F1") + ">" + it2.ToString("F1"),18}");
    }

    public static void QrCheck()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;
        Console.WriteLine();
        Console.WriteLine("normal equations vs QR (same damped system, different factorization):");
        Console.WriteLine($"{"scenario",-22} {"NE us",9} {"QR us",9} {"cost NE",11} {"cost QR",11} " +
                          $"{"iters NE>QR",12}");

        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            QrPair(rig, clipDir, "idle", new Vector2(100f, 72f), default);
            QrPair(rig, clipDir, "run",  new Vector2(24f, 72f), new PlayerInput { Right = true });
        }
        LeastSquaresSolver.UseQr = false;
        AnimSolverConfig.Current.CadenceVectorize = false;
    }

    private static void QrPair(string rig, string clipDir, string label, Vector2 spawn, PlayerInput input)
    {
        LeastSquaresSolver.UseQr = false;
        AnimSolverConfig.Current.CadenceVectorize = false;
        var (us0, it0, _) = Measure(rig, clipDir, spawn, input);
        double c0 = LastMeanCost;
        LeastSquaresSolver.UseQr = true;
        // Under QR the reordering fragility is gone (QrStabilityCheck), so the QR arm runs
        // with vectorization on BOTH paths — that is the configuration QR would actually ship
        // as, and comparing it against a half-vectorized NE arm would understate it.
        AnimSolverConfig.Current.CadenceVectorize = true;
        var (us1, it1, _) = Measure(rig, clipDir, spawn, input);
        double c1 = LastMeanCost;

        Console.WriteLine($"{rig + "/" + label,-22} {us0,9:F1} {us1,9:F1} {c0,11:G4} {c1,11:G4} " +
                          $"{it0.ToString("F1") + ">" + it1.ToString("F1"),12}");
    }

    // Does QR make the summation order stop mattering? Same comparison as SimdCheck, but
    // with QR active — if the answer is "cost unchanged", the fragility was the normal
    // equations' squared conditioning and CadenceVectorize can come back on.
    public static void QrStabilityCheck()
    {
        string rigDir = FindDir("SkeletonStates");
        if (rigDir == null) return;
        Console.WriteLine();
        Console.WriteLine("under QR: does vectorizing still move the answer?");
        Console.WriteLine($"{"scenario",-22} {"cost scalar",12} {"cost vector",12} {"iters s>v",11}");
        LeastSquaresSolver.UseQr = true;
        // Under QR the reordering fragility is gone (QrStabilityCheck), so the QR arm runs
        // with vectorization on BOTH paths — that is the configuration QR would actually ship
        // as, and comparing it against a half-vectorized NE arm would understate it.
        AnimSolverConfig.Current.CadenceVectorize = true;
        foreach (string rig in new[] { "biped", "biped_rabbit" })
        {
            string clipDir = Path.Combine(rigDir, rig);
            if (!Directory.Exists(clipDir)) continue;
            StabPair(rig, clipDir, "run", new Vector2(24f, 72f), new PlayerInput { Right = true });
            StabPair(rig, clipDir, "idle", new Vector2(100f, 72f), default);
        }
        LeastSquaresSolver.UseQr = false;
        AnimSolverConfig.Current.CadenceVectorize = false;
        AnimSolverConfig.Current.CadenceVectorize = false;
    }

    private static void StabPair(string rig, string clipDir, string label, Vector2 spawn, PlayerInput input)
    {
        AnimSolverConfig.Current.CadenceVectorize = false;
        AnimSolverConfig.Current.StaticVectorize = false;
        var (_, it0, _) = Measure(rig, clipDir, spawn, input);
        double c0 = LastMeanCost;
        AnimSolverConfig.Current.CadenceVectorize = true;
        AnimSolverConfig.Current.StaticVectorize = true;
        var (_, it1, _) = Measure(rig, clipDir, spawn, input);
        double c1 = LastMeanCost;
        Console.WriteLine($"{rig + "/" + label,-22} {c0,12:G4} {c1,12:G4} " +
                          $"{it0.ToString("F1") + ">" + it1.ToString("F1"),11}");
    }
    private static (double us, double iters, float[][] poses) Measure(
        string rig, string clipDir, Vector2 spawn, PlayerInput input)
    {
        List<AnimationDocument> clips;
        Skeleton skel;
        try { clips = AnimationStore.LoadAll(clipDir); skel = SkeletonExamples.Load(rig); }
        catch { return (0, 0, null); }
        if (clips == null || clips.Count == 0) return (0, 0, null);

        const float Scale = 0.6f;
        const int Warm = 180, N = 600;
        var anim = new CharacterAnimator(skel, Scale, clips);
        var sim  = new Simulation(FlatFloor(), spawn);
        var surfaces = new SolverSurface[8];
        var poses = new float[N][];

        LastBlockCost.Clear(); LastBlockRows.Clear();
        double bestUs = double.MaxValue, iterSum = 0, costSum = 0; int solved = 0;
        double dySum = 0, dxSum = 0;
        for (int rep = 0; rep < Bench.Reps; rep++)
        {
            anim = new CharacterAnimator(skel, Scale, clips);
            sim  = new Simulation(FlatFloor(), spawn);
            for (int f = 0; f < Warm; f++) Tick(sim, anim, input, surfaces, Scale);

            var sw = Stopwatch.StartNew();
            for (int f = 0; f < N; f++)
            {
                Tick(sim, anim, input, surfaces, Scale);
                if (rep == Bench.Reps - 1)
                {
                    // Capture the DRAWN local angles, which is what a viewer would notice.
                    var p = new float[skel.Count];
                    for (int b = 0; b < skel.Count; b++) p[b] = anim.Pose.Local[b].Rotation;
                    poses[f] = p;
                }
            }
            sw.Stop();
            bestUs = Math.Min(bestUs, sw.Elapsed.TotalMilliseconds * 1000.0 / N);

            // Iterations averaged over every SOLVED frame of this rep, not sampled from the
            // final frame — solve depth varies frame to frame, and a single sample of it was
            // producing non-monotonic nonsense in the sweep table.
            if (rep == Bench.Reps - 1)
            {
                iterSum = 0; solved = 0; costSum = 0; dySum = 0; dxSum = 0;
                for (int f = 0; f < N; f++)
                {
                    Tick(sim, anim, input, surfaces, Scale);
                    var w = anim.LastSolveWork;
                    if (w.Iterations > 0)
                    {
                        iterSum += w.Iterations; costSum += anim.LastSolveCost; solved++;
                        dySum += Math.Abs(anim.LastSolveDy); dxSum += anim.LastSolveDphi;
                        foreach (var (block, rows, sumSq) in anim.LastResidualBreakdown)
                        {
                            LastBlockCost.TryGetValue(block, out double acc);
                            LastBlockCost[block] = acc + sumSq;
                            LastBlockRows[block] = rows;
                        }
                    }
                }
            }
        }
        foreach (string k in new List<string>(LastBlockCost.Keys))
            LastBlockCost[k] = solved > 0 ? LastBlockCost[k] / solved : 0;
        LastMeanDy = solved > 0 ? dySum / solved : 0;
        LastMeanDx = solved > 0 ? dxSum / solved : 0;
        LastMeanCost = solved > 0 ? costSum / solved : 0;
        return (bestUs, solved > 0 ? iterSum / solved : 0, poses);
    }

    internal static void Tick(Simulation sim, CharacterAnimator anim, PlayerInput input,
                             SolverSurface[] surfaces, float scale)
    {
        sim.Step(input);
        var p = sim.Player;
        int tc = TerrainSurfaces.Extract(sim.Chunks, anim, p.Body.Position, p.Facing, scale,
                                         surfaces, out bool near);
        anim.Update(CharacterAnimSample.From(p, Simulation.FixedDt, surfaces, tc, near, sim.Chunks));
    }

    internal static ChunkMap FlatFloor() => SimTerrain.FromAscii(
        new string('X', 80), originTileX: 0, originTileY: 6);

    internal static string FindDir(string name)
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
