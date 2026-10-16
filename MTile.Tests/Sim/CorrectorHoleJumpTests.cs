using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// A neutral jump from under a 1-wide hole in a 1-thick ceiling must go up and
// through it from anywhere the hole's lips are in hand reach. The body is
// 10.08 px wide against the hole's 11, so an off-center launch needs a small
// sideways correction mid-air — the contact push off the hole's lips (both
// can only push away from themselves, so the solve can only center). Before
// it (and before LatticePathPlanner.Clearance < 0.46) only a dead-center
// launch got through; an off-center one stopped with its head in the hole.
public class CorrectorHoleJumpTests(ITestOutputHelper output)
{
    // Ceiling row 5 (y 55..66) with the hole at col 10 (x 110..121); floor row 10 (top 110).
    private const float HoleMid = 115.5f, CeilTop = 55f;

    private SimFrame[] Jump(float dx, int dir)
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXOXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 4);
        var held = new PlayerInput { Left = dir < 0, Right = dir > 0 };
        var script = new InputScript()
            .For(30, default)
            .Then(held with { Space = true }).For(40)
            .Then(held).For(30);
        var res = SimRunner.Run(new SimConfig
        {
            Terrain = terrain, StartPosition = new Vector2(HoleMid + dx, 85f), Script = script,
            Frames = 100, Dt = 1f / 60f, Gravity = new Vector2(0f, 600f),
        });
        foreach (var f in res.Where(f => f.Frame >= 29 && f.Frame <= 60 && (f.Frame % 3 == 0 || f.Transition)))
            output.WriteLine($"{f.Frame,3} {f.State,-14} x={f.X,7:F2} y={f.Y,6:F2} vx={f.Vx,7:F2} vy={f.Vy,7:F2} F=({f.Fx,7:F1},{f.Fy,7:F1})");
        return res;
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(2.5f)]
    [InlineData(-2.5f)]
    [InlineData(4f)]
    public void NeutralJump_OffCenter_ThreadsHole(float dx)
    {
        var res = Jump(dx, 0);
        // Through = the whole body (bottom at center + 7.2) above the ceiling's top face.
        float bottom = PlayerCharacter.Radius * (2f * PlayerCharacter.BodyHeightScale - 1f);
        float minY = res.Min(f => f.Y);
        Assert.True(minY + bottom < CeilTop, $"did not clear the hole: min center y={minY:F2} (needs < {CeilTop - bottom:F2})");
    }
}
