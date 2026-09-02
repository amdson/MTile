using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The trajectory-capture pipeline behind the DebugDrawCorrector* flags: when the
// host sets CaptureTrajectories, the active maneuver publishes what it actually
// computed this timestep (reference at entry, ballistic + solved per tick); with
// the flag off nothing is captured; buffers expire with the frame / the maneuver.
public class CorrectorTrajectoryCaptureTests(ITestOutputHelper output)
{
    // Tunnel-vault course — guarantees nonzero rows mid-flight, so Solved captures too.
    // Approach side (cols 0..7) has 4 open rows above its floor — plain headroom. Past
    // the lip (col 8+) the floor rises one tile, leaving only 3 open rows (≈33px) above
    // the raised step — just under PlayerCharacter's ~32.78px standing height, so
    // clearing it forces the reflex vault rather than a walk-through. Same relative
    // squeeze the original 16px-grid authoring gave (2 rows ≈ 32px against the same
    // body height).
    private static ChunkMap TunnelStep() => SimTerrain.FromAscii(@"
        XXXXXXXXXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 2);

    // Approach-side floor is the terrain's last row (gty 7 given originTileY 2 above
    // five preceding rows). Standing rest offset (R + R·sin60°) is a body-scale
    // constant, unrelated to Chunk.TileSize.
    private static readonly float ApproachFloorTopY = 7 * Chunk.TileSize;
    private static readonly float StandingRestOffset =
        PlayerCharacter.Radius + PlayerCharacter.Radius * MathF.Sin(MathF.PI / 3f);

    private static readonly PlayerInput HoldRight = new() { Right = true };

    [Fact]
    public void CaptureOn_PublishesAllThree_AndExpiresAfterManeuver()
    {
        var sim = new Simulation(TunnelStep(), new Vector2(12f, ApproachFloorTopY - StandingRestOffset));
        sim.Player.CorrectorDebug.CaptureTrajectories = true;

        bool sawReference = false, sawBallistic = false, sawSolved = false;
        int lastVaultFrame = -1;
        for (int f = 0; f < 90; f++)
        {
            sim.Step(HoldRight);
            var cd = sim.Player.CorrectorDebug;
            if (sim.Player.CurrentStateName.Contains("Parkour"))
            {
                lastVaultFrame = f;
                sawReference |= cd.ReferenceCount > 0;
                sawBallistic |= cd.BallisticCount > 0;
                sawSolved    |= cd.SolvedCount > 0;
                // Captured trajectories start at (or near) the live body: the first
                // ballistic sample is one integration step from the current state.
                if (cd.BallisticCount > 0)
                {
                    float gap = Vector2.Distance(cd.BallisticTrajectory[0].Pos, sim.Player.Body.Position);
                    Assert.True(gap < 12f, $"ballistic capture detached from the body: {gap:F1}px");
                }
            }
        }
        Assert.True(lastVaultFrame > 0, "fixture never vaulted");
        Assert.True(sawReference, "reference trajectory never captured");
        Assert.True(sawBallistic, "ballistic trajectory never captured");
        Assert.True(sawSolved, "solved trajectory never captured (tunnel arc should carry rows)");

        // After the maneuver ends the MANEUVER buffers expire (Exit + per-frame
        // BeginFrame). Reference is maneuver-only, so it must be gone; ballistic/
        // solved may legitimately be repopulated the same frame by the AMBIENT
        // corrector (it captures whenever the held-input coast implies rows).
        var end = sim.Player.CorrectorDebug;
        Assert.False(sim.Player.CurrentStateName.Contains("Parkour"));
        Assert.Equal(0, end.ReferenceCount);
        output.WriteLine($"capture verified through vault ending at frame {lastVaultFrame}; " +
                         $"post-maneuver ambient ballistic={end.BallisticCount} solved={end.SolvedCount}");
    }

    [Fact]
    public void CaptureOff_PublishesNothing_AndSimIsBitIdentical()
    {
        // Off by default ⇒ no buffers; and toggling capture must not perturb the
        // sim (write-only diagnostics): identical trajectory either way.
        var a = new Simulation(TunnelStep(), new Vector2(12f, 72f));
        var b = new Simulation(TunnelStep(), new Vector2(12f, 72f));
        b.Player.CorrectorDebug.CaptureTrajectories = true;

        for (int f = 0; f < 90; f++)
        {
            a.Step(HoldRight);
            b.Step(HoldRight);
            Assert.Equal(0, a.Player.CorrectorDebug.BallisticCount);
            Assert.Equal(0, a.Player.CorrectorDebug.ReferenceCount);
            Assert.Equal(a.Player.Body.Position, b.Player.Body.Position);
            Assert.Equal(a.Player.Body.Velocity, b.Player.Body.Velocity);
        }
    }
}
