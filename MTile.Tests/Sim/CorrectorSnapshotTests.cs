using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Corrector determinism + cost gates (BALLISTIC_CORRECTOR_PLAN step 9).
// Snapshot/restore MID-MANEUVER: the corrector's only cross-frame state is
// MovementVars.CorrectorPrevDv (the Δu anchor) plus the Mantle* scalars — the
// scratch buffers are derived per tick. A restore inside the vault must replay
// bit-identically; a missed field or a stale transient shows up as divergence.
public class CorrectorSnapshotTests(ITestOutputHelper output)
{
    private static string Probe(Simulation sim)
    {
        var p = sim.Player;
        var b = p.Body;
        return $"{Bits(b.Position.X)},{Bits(b.Position.Y)};{Bits(b.Velocity.X)},{Bits(b.Velocity.Y)}|" +
               $"{p.CurrentStateName}/{p.CurrentActionName}|f{p.Frame}|cons{b.Constraints.Count}";
    }

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    // Floor row 3 (top 48) with a 1-block step at cols 8..15 (top 32, lip x=128).
    private static ChunkMap StepTerrain() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static PlayerInput HoldRight => new() { Right = true };

    [Fact]
    public void SnapshotMidVault_RestoreReplaysBitForBit()
    {
        const int N = 90;   // total frames — vault + landing + continued run

        // ── Live run: step until the vault is ACTIVE, two more frames into the
        //    arc, then snapshot mid-maneuver. ──
        var live = new Simulation(StepTerrain(), new Vector2(12f, 20f));
        int k = 0;
        while (!live.Player.CurrentStateName.Contains("Parkour") && k < 60) { live.Step(HoldRight); k++; }
        Assert.True(live.Player.CurrentStateName.Contains("Parkour"), "fixture never entered the vault");
        live.Step(HoldRight); k++;
        live.Step(HoldRight); k++;
        Assert.True(live.Player.CurrentStateName.Contains("Parkour"), "snapshot frame is not mid-vault");

        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = k; f < N; f++) { live.Step(HoldRight); liveTrace.Add(Probe(live)); }
        // Sanity: the replayed window actually contains the rest of the vault + landing.
        Assert.Contains(liveTrace, t => t.Contains("Parkour"));
        Assert.Contains(liveTrace, t => t.Contains("Standing"));

        // ── Restore into the vault and replay identical inputs ──
        live.Restore(snap);
        Assert.True(live.Player.CurrentStateName.Contains("Parkour"), "restore did not land mid-vault");
        var replayTrace = new List<string>();
        for (int f = k; f < N; f++) { live.Step(HoldRight); replayTrace.Add(Probe(live)); }

        Assert.Equal(liveTrace.Count, replayTrace.Count);
        for (int i = 0; i < liveTrace.Count; i++)
        {
            if (liveTrace[i] != replayTrace[i])
                output.WriteLine($"Divergence at replay frame {k + i}:\nLIVE:   {liveTrace[i]}\nREPLAY: {replayTrace[i]}");
            Assert.Equal(liveTrace[i], replayTrace[i]);
        }
        output.WriteLine($"Mid-vault round-trip identical across {liveTrace.Count} frames (snapshot at frame {k}).");
    }

    // Cost gate: budget is "well under 0.1 ms/player/tick". Time a vault-heavy
    // course (repeated steps ⇒ trigger solves + per-tick corrector solves every
    // few frames) and a flat run as the baseline. Informational numbers + a
    // generous ceiling so CI noise doesn't flake it.
    [Fact]
    public void CorrectorCost_VaultHeavyCourse_StaysUnderBudget()
    {
        var course = SimTerrain.FromAscii(@"
            OOOOOOOOXXOOOOXXOOOOXXOOOOXXOOOOXXOOOOXX
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 5);
        var sim = new Simulation(course, new Vector2(12f, 72f));

        // Warm-up (JIT + first-touch).
        for (int f = 0; f < 60; f++) sim.Step(HoldRight);

        const int Frames = 600;
        var sw = Stopwatch.StartNew();
        for (int f = 0; f < Frames; f++) sim.Step(HoldRight);
        sw.Stop();

        double msPerTick = sw.Elapsed.TotalMilliseconds / Frames;
        output.WriteLine($"vault-heavy course: {msPerTick * 1000:F1} µs/tick over {Frames} ticks " +
                         $"(budget: well under 100 µs/player/tick)");
        Assert.True(msPerTick < 0.5, $"sim tick cost blew the budget: {msPerTick:F3} ms/tick");
    }
}
