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
//   3. A wall taller than CorridorMaxRise is beyond the probe's maneuver envelope →
//      no arc, honest bonk. (4 blocks at TileSize 11 — 44px clears the 42px envelope;
//      3 blocks no longer does, since 33px now sits inside ArcJumpState's own band.)
public class ArcJumpStateTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float TS = Chunk.TileSize;
    private static readonly float R = PlayerCharacter.Radius;
    private static readonly float HalfW = R * MathF.Sin(MathF.PI / 3f);       // R·sin60°
    private static readonly float RestOffset = R * (1f + MathF.Sin(MathF.PI / 3f)); // tile-top → resting center

    // Two-block step: floor row 3 (top y=3·TS), step rows 1-2 at cols 8..19 (lip x=8·TS,
    // top y=1·TS, rise 2·TS). Standing rest: center ≈ top − RestOffset — "on top" asserts
    // use y < 2 to split floor-rest from step-rest.
    private static ChunkMap TwoBlockStep() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXXXXX
        OOOOOOOOXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private const float FloorTop = 3 * TS;
    private const float StepTop  = 1 * TS;
    private const float LipX     = 8 * TS;

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
        // Face ~1px from the lip (face = x + HalfW).
        float floorRestY = FloorTop - RestOffset;
        var frames = Run(TwoBlockStep(), new Vector2(LipX - 1f - HalfW, floorRestY), 90);

        Assert.True(frames.Any(f => f.State.Contains("ArcJump")), "expected ArcJumpState to engage");
        Assert.True(frames.Any(f => f.Y < 2f && f.X > LipX),
            "expected the body to be delivered on top of the 2-block step");
    }

    [Fact]
    public void LowLipOverApproach_ArcRefuses_NoCornerJam()
    {
        // A slab at row 0 over cols 6-7 caps the approach — the climb volume over the
        // body's own columns is blocked (the course-corridor stalactite trap). The slab's
        // BOTTOM sits flush with the step's TOP (both row-1 boundary at y=1·TS), so the
        // clearance available to stand under it equals the step's rise. At TileSize 11 a
        // 2-block rise (22px) undercuts PlayerCharacter.StandingHeight (~32.8px) — the body
        // can't even stand there without shoving out from under the slab — so this scenario
        // needs a 3-block rise (33px, still within ArcJumpState's own band) to leave the
        // ~same standing clearance the original 16px-scale test had.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOXXOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        const float LowLipFloorTop = 4 * TS;
        float floorRestY = LowLipFloorTop - RestOffset;
        var frames = Run(terrain, new Vector2(LipX - 1f - HalfW, floorRestY), 90);

        Assert.DoesNotContain(frames, f => f.State.Contains("ArcJump"));
        // Honest refusal: the body stays at ground level before the step, not wedged mid-air
        // under the slab corner. Same ~7.6px slack below floor-rest as the original test.
        const float GroundLevelSlack = 7.6f;
        Assert.True(frames.All(f => f.X < LipX), "body should not pass the lip");
        Assert.True(frames[^1].Y > floorRestY - GroundLevelSlack,
            $"body should settle at ground level, got y={frames[^1].Y:F2}");
    }

    [Fact]
    public void AgainstThreeBlockWall_NoArc_StaysPut()
    {
        // 4 blocks tall (44px) at TileSize 11 — a 3-block wall (33px) now sits INSIDE
        // ArcJumpState's own rise band (MantleMaxRise..CorridorMaxRise = 20..42), so it
        // no longer proves "beyond the envelope"; 4 blocks (44px) does.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        // Start a few px above the floor surface (row 4), letting gravity settle it.
        float wallFloorTop = 4 * TS;
        var frames = Run(terrain, new Vector2(LipX - 1f - HalfW, wallFloorTop - 6f), 60);

        Assert.DoesNotContain(frames, f => f.State.Contains("ArcJump"));
        Assert.True(frames.All(f => f.X < LipX), "body should not pass through the wall");
    }
}
