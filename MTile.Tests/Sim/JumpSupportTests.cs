using System;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// JumpSupportChecker: a jump press with a floor or a ledge lip in leg reach is a ground
// jump, not the double jump — even where the standing ground probe sees nothing.
public class JumpSupportTests
{
    const int T = Chunk.TileSize;
    const int LedgeCol = 30, LedgeRow = 16;      // ledge: cols >= 30, rows >= 16 (top y = 176)

    static ChunkMap Terrain()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 40; r++)
        {
            for (int c = 0; c < 60; c++) sb.Append(c >= LedgeCol && r >= LedgeRow ? 'X' : 'O');
            if (r < 39) sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // Body beside the ledge's left face, its standing foot line `below` px under the lip.
    static Simulation BesideLedge(float below, float gap = 0.5f)
    {
        var sim = new Simulation(Terrain(), new Vector2(100, 100));
        var body = sim.Player.Body;
        var b = body.Bounds;
        float halfW = (b.Right - b.Left) / 2f, bottomOff = b.Bottom - body.Position.Y;
        float footY = LedgeRow * T + below;
        body.Position = new Vector2(LedgeCol * T - halfW - gap,
                                    footY - MovementConfig.Current.FoldHoverOffset - bottomOff);
        body.Velocity = new Vector2(0f, 40f);
        return sim;
    }

    static MovementState PressJump(Simulation sim)
    {
        sim.Step(new PlayerInput());
        sim.Step(new PlayerInput { Space = true });
        return sim.Player.CurrentState;
    }

    [Fact]
    public void JustBelowALedgeLip_IsAGroundJump()
    {
        var sim = BesideLedge(below: 3f);
        Assert.False(sim.Player.IsGrounded);
        var s = PressJump(sim);
        Assert.IsType<JumpingState>(s);
    }

    [Fact]
    public void JustBelowALedgeLip_AwayPress_IsStillAGroundJump()
    {
        var sim = BesideLedge(below: 3f);
        sim.Step(new PlayerInput { Left = true });
        sim.Step(new PlayerInput { Left = true, Space = true });
        Assert.IsType<JumpingState>(sim.Player.CurrentState);
    }

    [Fact]
    public void FarBelowTheLip_IsTheDoubleJump()
    {
        var sim = BesideLedge(below: 3f * T);
        Assert.IsType<DoubleJumpingState>(PressJump(sim));
    }

    [Fact]
    public void OpenAir_IsTheDoubleJump()
    {
        var sim = new Simulation(Terrain(), new Vector2(100, 60));
        Assert.IsType<DoubleJumpingState>(PressJump(sim));
    }
}
