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
    private static ChunkMap TunnelStep() => SimTerrain.FromAscii(@"
        XXXXXXXXXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 2);

    private static readonly PlayerInput HoldRight = new() { Right = true };

    [Fact]
    public void CaptureOn_PublishesAllThree_AndExpiresAfterManeuver()
    {
        var sim = new Simulation(TunnelStep(), new Vector2(12f, 72f));
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

        // After the maneuver ends the buffers expire (Exit + per-frame BeginFrame).
        var end = sim.Player.CorrectorDebug;
        Assert.False(sim.Player.CurrentStateName.Contains("Parkour"));
        Assert.Equal(0, end.ReferenceCount);
        Assert.Equal(0, end.BallisticCount);
        Assert.Equal(0, end.SolvedCount);
        output.WriteLine($"capture verified through vault ending at frame {lastVaultFrame}");
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
