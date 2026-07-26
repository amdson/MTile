using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// DIAGNOSTIC (no behavior asserts): spawn the player at rest, face d px from a 1-block step
// lip, hold Right, and report which maneuver claims the climb (ArcJump / Mantle / Parkour /
// none) and whether the body ends up on top. Sweeps d = 0..16 px (one tile). Written to
// answer "mantle doesn't actually trigger for me" — run with the sweep output visible.
public class OneBlockTriggerSweepTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const float HalfW = 10.392f;   // R·sin60°: face = x + HalfW; lip at x = 128

    // One-block step: floor row 3 (top y=48), step row 2 at cols 8..15 (lip x=128, top y=32).
    // Standing rest center ≈ top − 22.4 → ≈ 25.6 on the floor, ≈ 9.6 on the step.
    private static ChunkMap StepTerrain() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    [Fact(Skip = "Diagnostic sweep with no behavior asserts — run manually when triaging 1-block trigger claims")]
    public void HoldRight_FromStandstill_DistanceSweep_ReportClaimingState()
    {
        output.WriteLine("  d(px)   states seen (in order)                          on top?  final(x,y)");
        output.WriteLine("  ─────────────────────────────────────────────────────────────────────────────");

        for (int d = 0; d <= 16; d++)
        {
            var cfg = new SimConfig
            {
                Terrain       = StepTerrain(),
                StartPosition = new Vector2(128f - HalfW - d, 25.6f),
                StartVelocity = Vector2.Zero,
                Script        = InputScript.Always(new PlayerInput { Right = true }),
                Frames        = 120,
                Dt            = Dt,
                Gravity       = new Vector2(0f, 600f),
            };
            var frames = SimRunner.Run(cfg);

            // Ordered distinct state sequence, collapsed (e.g. Standing→Parkour→Mantle→Standing).
            var seq = new List<string>();
            foreach (var f in frames)
            {
                string s = f.State.Replace("State", "");
                if (seq.Count == 0 || seq[^1] != s) seq.Add(s);
            }
            bool onTop = frames.Any(f => f.Y < 14f && f.X > 128f);
            var last = frames[^1];
            output.WriteLine($"  {d,4}    {string.Join("→", seq),-46}  {(onTop ? "YES" : "no "),-6}  ({last.X,6:F1},{last.Y,5:F1})");
        }

        // Diagnostic only — the table above is the deliverable.
        Assert.True(true);
    }
}
