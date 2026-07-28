using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// ArcJumpState (Plans/CORRIDOR_MANEUVER_PLAN.md scenario 3): hop + guided arc onto a rise
// above the mantle band (~2 blocks). Contracts pinned here:
//   1. Held-direction against a 2-block step → one-shot hop delivers the body on top,
//      apex bounded by the ballistic envelope (no pop past the landing gate).
//   2. Blocked climb volume (a low lip over the body's trailing half) → the arc REFUSES
//      rather than wedging the body into the corner and thrashing on its timeout.
//   3. A 3-block wall is beyond the probe's maneuver envelope → no arc, honest bonk.
public class ArcJumpStateTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;

    // Two-block step: floor row 3 (top y=48), step rows 1-2 at cols 8..19 (lip x=128, top
    // y=16, rise 32px). Standing rest (R=12): center ≈ top − 22.4 → y ≈ 25.6 on the floor,
    // y ≈ −6.4 on the step — "on top" asserts use y < 2 to split the two.
    private static ChunkMap TwoBlockStep() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXXXXX
        OOOOOOOOXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private SimFrame[] Run(ChunkMap terrain, Vector2 start, int frames)
    {
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = start,
            // Up held: the 2-block arc is a deliberate move (movement_todo #6).
            Script        = InputScript.Always(new PlayerInput { Right = true, Up = true }),
            Frames        = frames,
            Dt            = Dt,
            Gravity       = new Vector2(0f, 600f),
        };
        var result = SimRunner.Run(cfg);
        foreach (var f in result)
            if (f.Frame % 5 == 0 || f.State.Contains("ArcJump"))
                output.WriteLine($"{f.Frame,3} {f.State,-22} x={f.X,7:F2} y={f.Y,6:F2} vx={f.Vx,7:F2} vy={f.Vy,7:F2}");
        return result;
    }

    [Fact]
    public void AgainstTwoBlockStep_HoldToward_ArcJumpsOntoTop()
    {
        // Face ~1px from the lip (face = x + R·sin60° ≈ x + 10.4), at standing rest.
        var frames = Run(TwoBlockStep(), new Vector2(116f, 26f), 90);

        Assert.True(frames.Any(f => f.State.Contains("ArcJump")), "expected ArcJumpState to engage");
        Assert.True(frames.Any(f => f.Y < 2f && f.X > 128f),
            "expected the body to be delivered on top of the 2-block step");
        // Ballistic envelope: apex must stay near the landing gate (gate target −8, margin 4),
        // not pop into the sky.
        float apexY = frames.Min(f => f.Y);
        Assert.True(apexY >= -24f, $"arc overshot the landing: apex y={apexY:F2} (gate ≈ −8)");
    }

    [Fact]
    public void LowLipOverApproach_ArcRefuses_NoCornerJam()
    {
        // Same 2-block step, but a slab at row 0 over cols 6-7 caps the approach — the climb
        // volume over the body's own columns is blocked (the course-corridor stalactite trap).
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOXXOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var frames = Run(terrain, new Vector2(116f, 26f), 90);

        Assert.DoesNotContain(frames, f => f.State.Contains("ArcJump"));
        // Honest refusal: the body stays at ground level before the step, not wedged mid-air
        // under the slab corner.
        Assert.True(frames.All(f => f.X < 128f), "body should not pass the lip");
        Assert.True(frames[^1].Y > 18f, $"body should settle at ground level, got y={frames[^1].Y:F2}");
    }

    [Fact]
    public void AgainstThreeBlockWall_NoArc_StaysPut()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var frames = Run(terrain, new Vector2(116f, 42f), 60);

        Assert.DoesNotContain(frames, f => f.State.Contains("ArcJump"));
        Assert.True(frames.All(f => f.X < 128f), "body should not pass through the wall");
    }
}
