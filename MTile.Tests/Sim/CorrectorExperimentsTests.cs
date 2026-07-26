using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Experiments around the corrector's operating envelope: the A/B flag actually
// gates behavior, the vault fires across the whole running-speed band, and the
// same verbs work at both 30 and 60 fps. Informational traces + coarse asserts —
// tighter numbers live in the per-scenario suites.
public class CorrectorExperimentsTests(ITestOutputHelper output)
{
    // One-block step: floor row 3 (top 48), step cols 8..15 (top 32, lip x=128).
    private static ChunkMap StepTerrain() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static SimFrame[] Run(ChunkMap terrain, Vector2 start, Vector2 vel, int frames, float dt)
        => SimRunner.Run(new SimConfig
        {
            Terrain = terrain, StartPosition = start, StartVelocity = vel,
            Script = InputScript.Always(new PlayerInput { Right = true }),
            Frames = frames, Dt = dt, Gravity = new Vector2(0f, 600f),
        });

    // ── A/B: the config flag really is the corrector's off switch ──
    [Fact]
    public void FlagOff_NoCorrectorVault_BodyStallsAtStep()
    {
        var cfg = MovementConfig.Current;
        bool saved = cfg.CorrectorVaultEnabled;
        try
        {
            cfg.CorrectorVaultEnabled = false;
            var frames = Run(StepTerrain(), new Vector2(12f, 20f), Vector2.Zero, 120, 1f / 30f);
            Assert.DoesNotContain(frames, f => f.State.Contains("Parkour") || f.State.Contains("ArcJump"));
            output.WriteLine($"flag off: final x={frames[^1].X:F1} (stalled at the step or mantled slow)");

            cfg.CorrectorVaultEnabled = true;
            frames = Run(StepTerrain(), new Vector2(12f, 20f), Vector2.Zero, 120, 1f / 30f);
            Assert.Contains(frames, f => f.State.Contains("Parkour"));
            Assert.True(frames.Any(f => f.X > 140f && f.Y < 12f), "flag on: vault should deliver on top");
        }
        finally
        {
            cfg.CorrectorVaultEnabled = saved;
        }
    }

    // ── Speed band: the vault family covers walking-fast through full sprint ──
    [Theory]
    [InlineData(80f)]
    [InlineData(120f)]
    [InlineData(160f)]
    [InlineData(200f)]
    public void Vault_FiresAcrossRunningSpeedBand(float entrySpeed)
    {
        // Start close enough that the approach doesn't re-equilibrate the speed:
        // trigger distance is 32, so begin 64px out with the target velocity.
        var frames = Run(StepTerrain(), new Vector2(64f, 24f), new Vector2(entrySpeed, 0f), 90, 1f / 30f);
        bool vaulted = frames.Any(f => f.State.Contains("Parkour") || f.State.Contains("Mantle"));
        bool delivered = frames.Any(f => f.X > 140f && f.Y < 12f);
        output.WriteLine($"entry {entrySpeed}: vaulted={vaulted} delivered={delivered} " +
                         $"finalX={frames[^1].X:F1} minY={frames.Min(f => f.Y):F1}");
        Assert.True(vaulted, $"no climb state engaged at entry speed {entrySpeed}");
        Assert.True(delivered, $"not delivered on top at entry speed {entrySpeed}");
    }

    // ── Ambient air-graze: the case the ramps never covered ──
    // An airborne arc that clips a platform corner: without the ambient corrector
    // the bonk zeroes horizontal speed; with it the body is deflected around
    // whichever side it grazed (greedy, no route planning) and keeps its speed.
    [Fact]
    public void AmbientAirGraze_PreservesSpeedThroughCorner()
    {
        var cfg = MovementConfig.Current;
        bool saved = cfg.AmbientCorrectorEnabled;
        try
        {
            var terrain = SimTerrain.FromAscii(@"
                OOOOOOOOOOXXXXXXXXXXXX
                OOOOOOOOOOXXXXXXXXXXXX
                OOOOOOOOOOOOOOOOOOOOOO
                OOOOOOOOOOOOOOOOOOOOOO
                OOOOOOOOOOOOOOOOOOOOOO
                XXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 4);
            SimFrame[] Arc() => Run(terrain, new Vector2(90f, 130f), new Vector2(150f, -200f), 60, 1f / 30f);

            cfg.AmbientCorrectorEnabled = false;
            float minVxOff = Arc().Where(f => f.X > 130f && f.X < 175f).Min(f => f.Vx);
            cfg.AmbientCorrectorEnabled = true;
            float minVxOn = Arc().Where(f => f.X > 130f && f.X < 175f).Min(f => f.Vx);

            output.WriteLine($"minVx near corner: ambient off={minVxOff:F0}, on={minVxOn:F0}");
            Assert.True(minVxOff < 40f, $"baseline should bonk the corner: minVx={minVxOff:F0}");
            Assert.True(minVxOn > 150f, $"ambient should preserve speed through the graze: minVx={minVxOn:F0}");
        }
        finally { cfg.AmbientCorrectorEnabled = saved; }
    }

    // ── Frame-rate: the same verb at 30 and 60 fps lands in the same place ──
    [Theory]
    [InlineData(1f / 30f)]
    [InlineData(1f / 60f)]
    public void Vault_DtInvariantDelivery(float dt)
    {
        int frames = (int)(3f / dt);   // ~3 seconds either way
        var trace = Run(StepTerrain(), new Vector2(12f, 20f), Vector2.Zero, frames, dt);
        Assert.True(trace.Any(f => f.State.Contains("Parkour")), $"no vault at dt={dt}");
        Assert.True(trace.Any(f => f.X > 140f && f.Y < 12f), $"not delivered at dt={dt}");
        // Delivered height is the same gate either way (rest on step ≈ 8, spring hover ±1).
        var onTop = trace.Where(f => f.X > 150f && f.X < 250f && f.State.Contains("Standing")).ToArray();
        Assert.True(onTop.Length > 0, $"never stood on the step at dt={dt}");
        float restY = onTop[^1].Y;
        output.WriteLine($"dt={dt}: rest y on step = {restY:F2}");
        Assert.True(MathF.Abs(restY - 8f) < 2.5f, $"rest height off-gate at dt={dt}: y={restY:F2}");
    }
}
