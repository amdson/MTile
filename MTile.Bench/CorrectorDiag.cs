using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;

namespace MTile.Bench;

// How much of a sim tick is the CORRECTOR's solver?
//
// The sim has a second, unrelated solver: CorrectionSolver, a projected-descent QP over
// (channels × horizon) with a fixed 4 inner iterations. It shares nothing with the
// animation solver's LM — different algorithm, different file — so the animation findings
// say nothing about it, and `bumpy corridor` being 6-12x the cost of every other sim
// scenario points straight at it.
//
// (The LM-based TrajectoryLm oracle is NOT in this path: movement_config.json ships
// FoldEngine "ref", and the "lm" engine is a freeze-frame diagnostic.)
internal static class CorrectorDiag
{
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("corrector QP share of a sim tick:");
        Console.WriteLine($"{"scenario",-24} {"us/tick",9} {"qp us/tick",11} {"share",7} {"qp calls/tick",14} {"us/call",9}");

        Measure("flat run",       FlatFloor(), new Vector2(24f, 72f), new PlayerInput { Right = true });
        Measure("vault course",   VaultCourse(), new Vector2(12f, 72f), new PlayerInput { Right = true });
        Measure("bumpy corridor", Corridor(), new Vector2(24f, 74f), new PlayerInput { Right = true },
                g => g.Player.RestrictToFallAndStand());
        Dims();
        Console.WriteLine();
        Console.WriteLine("  " + QpBench.Run() + "   (same code path the web host runs)");
    }

    // Solve dimensions for the scenario that actually costs something.
    private static void Dims()
    {
        var sim = new Simulation(Corridor(), new Vector2(24f, 74f), g => g.Player.RestrictToFallAndStand());
        var input = new PlayerInput { Right = true };
        for (int f = 0; f < 120; f++) sim.Step(input);
        CorrectionSolver.Profile = true;
        CorrectionSolver.ResetProfile();
        for (int f = 0; f < 1800; f++) sim.Step(input);
        CorrectionSolver.Profile = false;

        Console.WriteLine();
        Console.WriteLine($"  bumpy corridor dims: H={(double)CorrectionSolver.SumH / CorrectionSolver.Calls:F1} " +
                          $"channels={(double)CorrectionSolver.SumC / CorrectionSolver.Calls:F1} " +
                          $"rows={(double)CorrectionSolver.SumR / CorrectionSolver.Calls:F1} " +
                          $"sweeps={(double)CorrectionSolver.SumIters / CorrectionSolver.Calls:F1}");
    }

    private static void Measure(string name, ChunkMap terrain, Vector2 spawn, PlayerInput input,
                                Action<Simulation> populate = null)
    {
        const int Warm = 120, N = 1800;
        double bestUs = double.MaxValue, qpUs = 0, callsPerTick = 0;

        for (int rep = 0; rep < Bench.Reps; rep++)
        {
            var sim = new Simulation(terrain, spawn, populate);
            for (int f = 0; f < Warm; f++) sim.Step(input);

            CorrectionSolver.Profile = true;
            CorrectionSolver.ResetProfile();
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < N; f++) sim.Step(input);
            sw.Stop();
            CorrectionSolver.Profile = false;

            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
            if (us < bestUs)
            {
                bestUs = us;
                qpUs = CorrectionSolver.Ticks * (1e6 / Stopwatch.Frequency) / N;
                callsPerTick = (double)CorrectionSolver.Calls / N;
            }
        }

        // NOTE: the timed run has Profile on, so each Solve pays two Stopwatch reads. At
        // ~20 ns each against a call that should be microseconds, that is well under the
        // measurement noise — but it is why qp us/tick is a slight over-estimate.
        Console.WriteLine($"{name,-24} {bestUs,9:F1} {qpUs,11:F1} {qpUs / bestUs,7:P0} " +
                          $"{callsPerTick,14:F2} {(callsPerTick > 0 ? qpUs / callsPerTick : 0),9:F2}");
    }

    private static ChunkMap FlatFloor() => SimTerrain.FromAscii(
        new string('X', 80), originTileX: 0, originTileY: 6);

    private static ChunkMap VaultCourse() => SimTerrain.FromAscii(@"
        OOOOOOOOXXOOOOXXOOOOXXOOOOXXOOOOXXOOOOXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 5);

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
