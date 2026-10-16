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

    // The inverse: crouched in a 2-high corridor, holding Down over a 1-wide hole
    // in its floor must drop the body through. A flat floor contact stamped while
    // the body was still over solid floor used to outlive the floor — physics
    // refreshed it from any tile under the bounding box, which over an 11 px hole
    // is almost always one of the neighbours — so its plane held the crouched body
    // up over the gap and its friction pinned DropdownState in place.
    [Theory]
    [InlineData(0)]   // crouch-walk in (Down+Right), stop over the hole, keep Down
    [InlineData(1)]   // Down+Right throughout: crawl straight across it
    public void CrouchedInCorridor_DownOverHole_DropsThrough(int mode)
    {
        // Ceiling bottom y=55, corridor rows 5-6 (22 px), floor row 7 (top 77)
        // with the hole at col 10 (x 110..121); lower floor row 12.
        var terrain = SimTerrain.FromAscii(@"
            XXXXXXXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXOXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 2);
        var sim = new Simulation(terrain, new Vector2(60f, 66f));
        bool stopped = false;
        float maxY = 0f;
        for (int f = 0; f < 240; f++)
        {
            var b = sim.Player.Body;
            if (mode == 0 && b.Position.X >= HoleMid - 2.5f) stopped = true;
            sim.Step(new PlayerInput { Down = true, Right = mode == 1 || !stopped });
            maxY = MathF.Max(maxY, sim.Player.Body.Position.Y);
        }
        output.WriteLine($"mode {mode}: max center y {maxY:F1}");
        Assert.True(maxY > 100f, $"stayed in the corridor: max center y {maxY:F1}");
    }
}
