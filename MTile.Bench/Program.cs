using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using MTile.Bench;

// Release-mode profiling harness for the corrector-era sim
// (Plans/CORRECTOR_CONSOLIDATION_PLAN.md §6):
//
//   dotnet run -c Release --project MTile.Bench
//
// Measures µs/tick for the locomotion scenarios that exercise each corrector
// path, plus snapshot/restore and a GGPO-style rollback resimulation, and
// closes with the multiplayer frame-budget arithmetic. The Debug-build cost
// gate lives in CorrectorSnapshotTests.CorrectorCost_VaultHeavyCourse — this
// harness is the Release-numbers side of that story.
//
// Methodology: fixed warmup then a timed run per scenario (fresh Simulation
// each), Stopwatch around the whole loop — the sim allocates nothing per tick
// in steady state, so mean = the honest number; a p99-ish spread is reported
// from coarse 60-tick buckets to expose GC/JIT spikes.
internal static class Program
{
    private static readonly PlayerInput HoldRight = new() { Right = true };
    private static readonly BenchReport Report = new();

    // Default baseline location. Committed, so a regression shows up in review as a diff
    // on this file rather than as a number nobody re-measured.
    private const string DefaultBaseline = "MTile.Bench/baseline.txt";

    private static int Main(string[] args)
    {
        Console.WriteLine($"MTile.Bench — {(Debugger.IsAttached ? "DEBUGGER ATTACHED — numbers invalid" : "release profiling")}");
#if DEBUG
        Console.WriteLine("WARNING: Debug build — run with -c Release for real numbers.");
#endif
        // The FSM transition trace prints from inside the timed loop; leaving it on both
        // inflates the numbers and buries the table in scrollback.
        SimTrace.Enabled = false;

        if (Array.IndexOf(args, "--corrector") >= 0) { CorrectorDiag.Run(); return 0; }
        if (Array.IndexOf(args, "--ftol") >= 0) { JtJDiff.Run(); AnimDiag.Run(); return 0; }
        if (Array.IndexOf(args, "--simd") >= 0) { FtolStudy.SimdCheck(); return 0; }
        if (Array.IndexOf(args, "--hover") >= 0) { HoverDiag.Run(); return 0; }
        if (Array.IndexOf(args, "--columns") >= 0) { ColumnDiag.Run(); return 0; }
        if (Array.IndexOf(args, "--breakdown") >= 0) { FtolStudy.BreakdownCsv(System.IO.Path.Combine(RepoRoot(), "solver_breakdown.csv")); return 0; }
        if (Array.IndexOf(args, "--double") >= 0) { FtolStudy.DoubleCheck(); return 0; }
        if (Array.IndexOf(args, "--qr") >= 0) { FtolStudy.QrCheck(); FtolStudy.QrStabilityCheck(); return 0; }
        // Regenerate the frozen problem QpBench replays on both runtimes.
        if (Array.IndexOf(args, "--dump-problem") >= 0)
        {
            string src = QpBench.DumpProblemLiteral();
            if (src == null) { Console.WriteLine("dump-problem: no fold solve captured."); return 1; }
            string path = System.IO.Path.Combine(RepoRoot(), "Diagnostics", "QpProblem.g.cs");
            System.IO.File.WriteAllText(path, src);
            Console.WriteLine($"wrote {path} ({src.Length} chars)");
            return 0;
        }
        string save = ArgValue(args, "--save");
        string compare = ArgValue(args, "--compare") ?? (Array.IndexOf(args, "--check") >= 0 ? DefaultBaseline : null);

        BenchReport.Header();

        Measure("flat rest (no input)",   FlatFloor(),   new Vector2(100f, 72f), default,   1800);
        Measure("flat run",               FlatFloor(),   new Vector2(24f, 72f),  HoldRight, 1800);
        Measure("bumpy corridor",         Corridor(),    new Vector2(24f, 74f),  HoldRight, 1800,
                populate: g => g.Player.RestrictToFallAndStand());
        Measure("vault-heavy course",     VaultCourse(), new Vector2(12f, 72f),  HoldRight, 1800);

        MeasureSnapshot();
        MeasureRollback();

        // Render-side CPU: the animation solver, which has no fixed-timestep budget
        // protecting it and which nothing else here measures.
        AnimationBench.Run(Report);

        if (Array.IndexOf(args, "--diag") >= 0) { AnimDiag.Run(); AnimDiag.PoseSplit(); SolverCore.Run(); JtJLayout.Run(); AnimDiag.Convergence(); FtolStudy.Run(); CorrectorDiag.Run(); }

        Budget();

        if (save != null) Report.Save(save);
        // Non-zero exit on regression so this can gate a build; --compare alone is
        // advisory-only until someone wires it into CI.
        return compare != null && !Report.CompareTo(compare) ? 1 : 0;
    }

    // Walks up from the bin directory to the repo root (the folder holding MTile.sln).
    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "MTile.sln"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    private static string ArgValue(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        if (i < 0) return null;
        return i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : DefaultBaseline;
    }

    private static void Measure(string name, ChunkMap terrain, Vector2 spawn, PlayerInput input,
                                int frames, Action<Simulation> populate = null)
    {
        // Report the MINIMUM over Bench.Reps repetitions, not the mean of one. A single
        // timed pass here swung 2× between back-to-back runs on an idle desktop, which is
        // enough noise to make any regression gate useless. Everything that perturbs a
        // benchmark — scheduler preemption, a background build, thermal throttle — only
        // ever ADDS time, so the minimum is the closest estimate of the true cost and it
        // converges fast. The worst-bucket column stays a max across all reps: that one is
        // deliberately measuring the hitches.
        double bestUs = double.MaxValue, worstBucketUs = 0;
        for (int rep = 0; rep < Bench.Reps; rep++)
        {
            var sim = new Simulation(terrain, spawn, populate);
            for (int f = 0; f < 120; f++) sim.Step(input);   // warmup: JIT + first-touch

            const int Bucket = 60;
            var sw = new Stopwatch();
            var total = Stopwatch.StartNew();
            for (int b = 0; b < frames / Bucket; b++)
            {
                sw.Restart();
                for (int f = 0; f < Bucket; f++) sim.Step(input);
                sw.Stop();
                worstBucketUs = Math.Max(worstBucketUs, sw.Elapsed.TotalMilliseconds * 1000.0 / Bucket);
            }
            total.Stop();
            bestUs = Math.Min(bestUs, total.Elapsed.TotalMilliseconds * 1000.0 / frames);
        }
        Report.Add(name, bestUs, worstBucketUs);
    }

    private static void MeasureSnapshot()
    {
        const int N = 600;
        double bestPair = double.MaxValue, bestRestore = double.MaxValue;
        for (int rep = 0; rep < Bench.Reps; rep++)
        {
            var sim = new Simulation(VaultCourse(), new Vector2(12f, 72f));
            for (int f = 0; f < 120; f++) sim.Step(HoldRight);

            var snap = sim.Snapshot();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) { snap = sim.Snapshot(); sim.Step(HoldRight); }
            sw.Stop();
            // Subtract the known step cost? No — report the pair as measured, and
            // restore separately below; the composite is what rollback pays.
            bestPair = Math.Min(bestPair, sw.Elapsed.TotalMilliseconds * 1000.0 / N);

            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) sim.Restore(snap);
            sw2.Stop();
            bestRestore = Math.Min(bestRestore, sw2.Elapsed.TotalMilliseconds * 1000.0 / N);
        }
        Report.Add("snapshot+step (pair)", bestPair);
        Report.Add("restore", bestRestore);
    }

    private static void MeasureRollback()
    {
        // GGPO worst case, amortized: every visual frame = restore an 8-frame-old
        // snapshot, resimulate 7 confirmed frames, step the new one, snapshot.
        const int Window = 8;
        const int Frames = 600;
        double best = double.MaxValue;
        for (int rep = 0; rep < Bench.Reps; rep++)
        {
            var sim = new Simulation(VaultCourse(), new Vector2(12f, 72f));
            for (int f = 0; f < 120; f++) sim.Step(HoldRight);

            var snap = sim.Snapshot();
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < Frames; f++)
            {
                sim.Restore(snap);
                for (int r = 0; r < Window; r++) sim.Step(HoldRight);
                snap = sim.Snapshot();
            }
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1000.0 / Frames);
        }
        Report.Add("rollback frame (win=8)", best);
    }

    private static void Budget()
    {
        Console.WriteLine();
        Console.WriteLine("budget: 16667 µs per 60fps frame.");
        Console.WriteLine("multiplayer worst case ≈ 2 players × 8-frame rollback resim each visual");
        Console.WriteLine("frame → keep (µs/tick × 16) comfortably under the frame budget with");
        Console.WriteLine("render headroom — i.e. per-tick cost target ≲ 250 µs on min-spec.");
    }

    // ── Courses ──────────────────────────────────────────────────────────────

    private static ChunkMap FlatFloor() => SimTerrain.FromAscii(
        new string('X', 80), originTileX: 0, originTileY: 6);

    // The CorrectorCost fixture's twin: repeating 1-block vaults at speed.
    private static ChunkMap VaultCourse() => SimTerrain.FromAscii(@"
        OOOOOOOOXXOOOOXXOOOOXXOOOOXXOOOOXXOOOOXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 5);

    // The FoldScenarioTests corridor: flat runway into the 3-high bumpy tunnel
    // (floor bumps ≡1 mod 4, ceiling bumps ≡3 mod 4) — the fold's stress case.
    private static ChunkMap Corridor()
    {
        const int W = 64;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                sb.Append(r switch
                {
                    6 => 'X',
                    2 when tunnel => 'X',
                    3 when tunnel && c % 4 == 3 => 'X',
                    5 when tunnel && c % 4 == 1 => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }
}
