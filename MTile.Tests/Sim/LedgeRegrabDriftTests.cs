using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Regression for the ledge re-grab drift bug: releasing Up partway through a ledge
// pull re-grabs the ledge (correct), but for a pull held long enough the body carried
// the maneuver's away-from-wall velocity into LedgeGrabState — which applied force on
// Y only, so the one-sided wall pin couldn't stop it and the body coasted off the
// screen forever, still "grabbing". The fix makes the hang a 2D anchor (spring-damped
// to the hang point on X as well as Y). See LedgeGrabState.Update.
public class LedgeRegrabDriftTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;
    private const float TS = Chunk.TileSize;

    // 2-tile wall on the right; top at the grounded body's head. Corner inner edge
    // X ≈ 144; the body hangs against the left face at X ≈ 135.
    private static ChunkMap BuildLedgeTerrain() => SimTerrain.FromAscii(@"
            ..........
            ..........
            ..........
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    private const float GroundTop = 5 * TS;
    private const float WallLeft  = 9 * TS;   // 144
    private const float HalfW     = 8.227f;
    private const float HangX     = 135f;     // approx resting X against the wall face

    // Sweep how long Up is held before release — short pulls release low, long pulls
    // release high (near/above the lip) where the body has picked up the most
    // away-from-wall velocity. Every case must settle at the hang, not drift.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void ReleaseMidPull_AcrossPullDurations_SettlesAtHang_NoDrift(int pullFrames)
    {
        var script = new InputScript()
            .For(30, new PlayerInput { })
            .For(3,  new PlayerInput { Up = true })   // tap → grab
            .For(10, new PlayerInput { })             // hang
            .For(pullFrames, new PlayerInput { Up = true })  // pull
            .Forever(new PlayerInput { });            // release

        var cfg = new SimConfig
        {
            Terrain       = BuildLedgeTerrain(),
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = script,
            Frames        = 200,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        var last = frames[^1];

        float tailMaxAbsVx = frames.Where(f => f.Frame >= 120).Max(f => MathF.Abs(f.Vx));
        output.WriteLine($"pull={pullFrames,2} endX={last.X,8:F2} endVx={last.Vx,7:F2} tailMaxVx={tailMaxAbsVx:F2} state={last.State}");

        // Settled, not coasting: tail horizontal velocity is ~zero and the body is at
        // the hang X, not somewhere off in the distance.
        Assert.True(tailMaxAbsVx < 2f, $"pull={pullFrames}: still drifting, tailMaxAbsVx={tailMaxAbsVx:F2}");
        Assert.True(MathF.Abs(last.X - HangX) < 6f,
            $"pull={pullFrames}: ended at X={last.X:F2}, not near the hang X≈{HangX}");
    }

    // A plain fresh grab (no pull involved) still rests against the wall — the 2D
    // anchor must not shove the body off its natural hang position.
    [Fact]
    public void FreshGrab_RestsAtWall()
    {
        var script = new InputScript()
            .For(30, new PlayerInput { })
            .Forever(new PlayerInput { Up = true });
        var cfg = new SimConfig
        {
            Terrain       = BuildLedgeTerrain(),
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = script,
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        var last = frames[^1];
        Assert.Equal("LedgeGrabState", last.State);
        Assert.True(MathF.Abs(last.X - HangX) < 6f, $"fresh grab rested at X={last.X:F2}, expected ≈ {HangX}");
        Assert.True(MathF.Abs(last.Vx) < 2f, $"fresh grab not settled horizontally: Vx={last.Vx:F2}");
    }
}
