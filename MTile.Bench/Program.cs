using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;

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

    private static void Main()
    {
        Console.WriteLine($"MTile.Bench — {(Debugger.IsAttached ? "DEBUGGER ATTACHED — numbers invalid" : "release profiling")}");
#if DEBUG
        Console.WriteLine("WARNING: Debug build — run with -c Release for real numbers.");
#endif
        Console.WriteLine($"{"scenario",-26} {"µs/tick",10} {"worst 60-tick bucket",22}");

        Measure("flat rest (no input)",   FlatFloor(),   new Vector2(100f, 72f), default,   1800);
        Measure("flat run",               FlatFloor(),   new Vector2(24f, 72f),  HoldRight, 1800);
        Measure("bumpy corridor",         Corridor(),    new Vector2(24f, 74f),  HoldRight, 1800,
                populate: g => g.Player.RestrictToFallAndStand());
        Measure("vault-heavy course",     VaultCourse(), new Vector2(12f, 72f),  HoldRight, 1800);

        MeasureSnapshot();
        MeasureRollback();

        Budget();
    }

    private static void Measure(string name, ChunkMap terrain, Vector2 spawn, PlayerInput input,
                                int frames, Action<Simulation> populate = null)
    {
        var sim = new Simulation(terrain, spawn, populate);
        for (int f = 0; f < 120; f++) sim.Step(input);   // warmup: JIT + first-touch

        const int Bucket = 60;
        double worstBucketUs = 0;
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
        double avgUs = total.Elapsed.TotalMilliseconds * 1000.0 / frames;
        Console.WriteLine($"{name,-26} {avgUs,10:F1} {worstBucketUs,22:F1}");
    }

    private static void MeasureSnapshot()
    {
        var sim = new Simulation(VaultCourse(), new Vector2(12f, 72f));
        for (int f = 0; f < 120; f++) sim.Step(HoldRight);

        const int N = 600;
        var snap = sim.Snapshot();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) { snap = sim.Snapshot(); sim.Step(HoldRight); }
        sw.Stop();
        // Subtract the known step cost? No — report the pair as measured, and
        // restore separately below; the composite is what rollback pays.
        double pairUs = sw.Elapsed.TotalMilliseconds * 1000.0 / N;

        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) sim.Restore(snap);
        sw2.Stop();
        double restoreUs = sw2.Elapsed.TotalMilliseconds * 1000.0 / N;
        Console.WriteLine($"{"snapshot+step (pair)",-26} {pairUs,10:F1}");
        Console.WriteLine($"{"restore",-26} {restoreUs,10:F1}");
    }

    private static void MeasureRollback()
    {
        // GGPO worst case, amortized: every visual frame = restore an 8-frame-old
        // snapshot, resimulate 7 confirmed frames, step the new one, snapshot.
        const int Window = 8;
        var sim = new Simulation(VaultCourse(), new Vector2(12f, 72f));
        for (int f = 0; f < 120; f++) sim.Step(HoldRight);

        const int Frames = 600;
        var snap = sim.Snapshot();
        var sw = Stopwatch.StartNew();
        for (int f = 0; f < Frames; f++)
        {
            sim.Restore(snap);
            for (int r = 0; r < Window; r++) sim.Step(HoldRight);
            snap = sim.Snapshot();
        }
        sw.Stop();
        double perVisualFrameUs = sw.Elapsed.TotalMilliseconds * 1000.0 / Frames;
        Console.WriteLine($"{"rollback frame (win=8)",-26} {perVisualFrameUs,10:F1}");
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
