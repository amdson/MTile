using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// The reference-rollout stand-fold engine (MovementConfig.FoldEngine "ref" →
// FoldReference). Pins the engine's contracts: bit-determinism (rollback),
// hover support, forward progress, station-keeping, and the automatic
// corridor duck-in (the reference bends under the lip via PathDeform — the
// regression that killed the "lm" engine as a live candidate). The qp engine
// stays the config default, so the rest of the sim suite is unaffected.
public class FoldRefEngineTests : IDisposable
{
    private readonly string _prevEngine;

    public FoldRefEngineTests()
    {
        _prevEngine = MovementConfig.Current.FoldEngine;
        MovementConfig.Current.FoldEngine = "ref";
    }

    public void Dispose() => MovementConfig.Current.FoldEngine = _prevEngine;

    private static ChunkMap Flat() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX");

    private static readonly PlayerInput HoldRight = new() { Right = true };

    private static Vector2 Spawn()
    {
        float floorTop = 3 * Chunk.TileSize;
        return new Vector2(4 * Chunk.TileSize, floorTop - 2f * PlayerCharacter.Radius);
    }

    [Fact]
    public void Deterministic_BitIdentical()
    {
        var a = new Simulation(Flat(), Spawn());
        var b = new Simulation(Flat(), Spawn());
        b.Player.CorrectorDebug.CaptureTrajectories = true;   // capture must not perturb

        for (int f = 0; f < 180; f++)
        {
            a.Step(HoldRight);
            b.Step(HoldRight);
            Assert.Equal(a.Player.Body.Position, b.Player.Body.Position);
            Assert.Equal(a.Player.Body.Velocity, b.Player.Body.Velocity);
        }
    }

    [Fact]
    public void HoldsHover_AndMakesProgress_OnFlatGround()
    {
        var sim = new Simulation(Flat(), Spawn());
        float floorTop = 3 * Chunk.TileSize;
        float startX = sim.Player.Body.Position.X;

        for (int f = 0; f < 180; f++)
        {
            sim.Step(HoldRight);
            var p = sim.Player.Body.Position;
            Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y), $"non-finite pos at frame {f}: {p}");
            Assert.True(p.Y < floorTop + 1f, $"sank to {p.Y:F1} (floor top {floorTop}) at frame {f}");
            Assert.True(p.Y > floorTop - 6f * PlayerCharacter.Radius,
                $"flew to {p.Y:F1} at frame {f}");
        }

        float dx = sim.Player.Body.Position.X - startX;
        Assert.True(dx > 1.5f * MovementConfig.Current.MaxWalkSpeed,
            $"only advanced {dx:F1}px over 180 frames");
    }

    [Fact]
    public void AtRest_StaysPut()
    {
        var sim = new Simulation(Flat(), Spawn());
        for (int f = 0; f < 120; f++) sim.Step(default);

        var v = sim.Player.Body.Velocity;
        Assert.True(MathF.Abs(v.X) < 5f, $"drifting: vx={v.X:F2}");
        Assert.True(MathF.Abs(v.Y) < 5f, $"bobbing: vy={v.Y:F2}");
    }

    // The bumpy tunnel (the in-game "corridor" stage shape): 3-tile interior,
    // alternating floor/ceiling bumps — every crossing is a duck or step-up
    // solved by the vertical deform. The squeeze over each floor bump
    // (interior 32px < body 24 + hover 10) is served by hover compression
    // emerging from the ceiling rows.
    [Fact]
    public void BumpyTunnel_HoldRight_TraversesAtSpeed()
    {
        const int W = 64;
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(W);
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
        var sim = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                 new Vector2(24f, 6 * ts - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 600; f++) sim.Step(HoldRight);

        float x = sim.Player.Body.Position.X;
        float avg = (x - 24f) / (600f / 60f);
        Assert.True(x > 600f, $"stalled in the tunnel at x={x:F1} (mouth at 256)");
        Assert.True(avg > 55f, $"tunnel traversal too slow: {avg:F1} px/s");
    }

    // 1-high step: the wall classification redirects the step's face to an
    // up-escape (≤ FoldClimbReachUp) and the deform lifts the carry onto it.
    [Fact]
    public void OneHighStep_HoldRight_ClimbsAndContinues()
    {
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(48);
            for (int c = 0; c < 48; c++)
                sb.Append(r == 6 || (r == 5 && c >= 10) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTop = 6 * ts;
        var sim = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                 new Vector2(40f, floorTop - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 240; f++) sim.Step(HoldRight);

        var p = sim.Player.Body.Position;
        Assert.True(p.X > 18 * ts, $"never climbed past the step: x={p.X:F1} (step at {10 * ts})");
        Assert.True(p.Y < floorTop - ts, $"on the ledge the body should ride higher: y={p.Y:F1}");
    }

    // Tall wall: no vertical escape fits the budgets, so row building
    // truncates at the wall — the give-up. The servo degenerates to the raw
    // carry, the body bonks honestly and stays pinned: no climb, no planned
    // brake, no backward push.
    [Fact]
    public void TallWall_HoldRight_HonestStop()
    {
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(24);
            for (int c = 0; c < 24; c++)
                sb.Append(r == 6 || (r >= 2 && c >= 12) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTop = 6 * ts;
        var sim = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                 new Vector2(40f, floorTop - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 300; f++) sim.Step(HoldRight);

        var p = sim.Player.Body.Position;
        float wallX = 12 * ts;
        Assert.True(p.X > wallX - 3 * ts, $"never reached the wall: x={p.X:F1}");
        // The squeezed-hexagon body's x half-width is ~6px, so flush contact
        // pins the center ≈ 6px from the face.
        Assert.True(p.X < wallX - 4f,
            $"passed through / into the wall: x={p.X:F1} (face at {wallX})");
        Assert.True(MathF.Abs(p.Y - (floorTop - 2f * PlayerCharacter.Radius)) < 8f,
            $"should stand at the wall, not climb it: y={p.Y:F1}");
    }

    // The automatic duck-in: the carry reference runs into the lip, the hard
    // rows bend it down (PathDeform), and the servo rides the bent path into
    // the corridor. This is the behavior the isotropic-hinge "lm" engine
    // could not produce (it stalled at the mouth).
    [Fact]
    public void HoldRight_DucksIntoLowCorridor()
    {
        int ts = Chunk.TileSize;
        var chunks = SimTerrain.FromAscii(@"
            XXXXXXXXXXXXXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXX");
        float floorTop = 6 * ts;
        var sim = new Simulation(chunks, new Vector2(2.5f * ts, floorTop - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 300; f++) sim.Step(HoldRight);

        float x = sim.Player.Body.Position.X;
        Assert.True(x > 14 * ts,
            $"stalled at x={x:F1} (corridor mouth at {8 * ts}) — never ducked in");
    }
}
