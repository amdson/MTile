using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Reproduces the "CoveredJump occasionally doesn't activate" bug for a left-facing corridor.
// Spec from the user: the jump should fire any time a portion of the body's bounding box is
// sticking out from under the overcrop.
// Corridor here has its left exit corner at x = 5 * Chunk.TileSize (the slab's left edge);
// "any portion sticking out" means body.Bounds.Left < corner.X.
public class CoveredJumpLeftCorridorTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    // Mirror of HoldSpaceRight_CoveredJumpOutOfTunnel. Ceiling slab at cols 5..19,
    // so its LEFT edge (the exit corner) is at x = 5 * Chunk.TileSize. Floor top at
    // y = 5 * Chunk.TileSize (row 5); ceiling bottom at y = 3 * Chunk.TileSize (row 2's bottom).
    private const string Terrain = @"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOXXXXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXX";

    private const float Corner = 5 * Chunk.TileSize;

    // For each startX in the "sticking out" range, CoveredJump must activate. The body-scale
    // geometry (hex half-width, sticking-out threshold) doesn't depend on tile size, so these
    // are the original left-corridor fixture's startX values shifted by the corner's move
    // (was 5*16=80, now 5*Chunk.TileSize) — the same relative offsets from the corner.
    [Theory]
    [InlineData(Corner + 5.5f)]   // barely sticking out (0.5 px)
    [InlineData(Corner + 5.0f)]   // 1 px sticking out
    [InlineData(Corner + 2.0f)]   // 4 px sticking out
    [InlineData(Corner + 0.0f)]   // body center at corner; half body sticking out
    [InlineData(Corner - 0.5f)]   // body center just past corner into open air
    [InlineData(Corner - 2.0f)]   // body center 2 px past corner
    [InlineData(Corner - 5.0f)]   // body center 5 px past corner; most of body in open air
    [InlineData(Corner - 4.5f)]   // still mostly under the slab — the shallowest "sticking out"
                                  // case in this fixture besides the 0.5px one
    public void HoldSpaceLeft_StickingOutOfLeftFacingCorridor_FiresCoveredJump(float startX)
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        float ceilingBottomY = 3 * Chunk.TileSize;
        float floorTopY      = 5 * Chunk.TileSize;

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            // Anywhere in the open interior band settles under gravity within the run —
            // start at the band's midpoint so it drops onto the floor and compresses
            // against the low ceiling regardless of the exact starting offset.
            StartPosition = new Vector2(startX, (ceilingBottomY + floorTopY) / 2f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Left = true, Space = true }),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.WriteCsv(frames, $"covered_jump_left_x{startX:F1}", outputDir: null);

        bool fired = frames.Any(f => f.State.Contains("CoveredJump"));

        if (!fired)
        {
            // Surface a state transition log so failures are diagnosable without re-running.
            output.WriteLine($"FAILURE at startX={startX}:");
            string prevState = "";
            foreach (var f in frames)
            {
                if (f.State == prevState) continue;
                output.WriteLine($"  frame {f.Frame,3} x={f.X,7:F2} y={f.Y,6:F2}  {f.State}");
                prevState = f.State;
            }
        }

        Assert.True(fired,
            $"startX={startX}, corner={Corner:F1}: body should have part of its bounds " +
            $"sticking out past the corner — CoveredJump should fire.");
    }

    // Pins the new precondition: CoveredJump requires a direction to be held.
    // Same geometry as above (body sticking out past the corner) but only Space pressed —
    // no Left/Right input. Must NOT fire CoveredJump.
    [Theory]
    [InlineData(80.0f)]   // body half-out
    [InlineData(75.0f)]   // body mostly out
    public void HoldSpaceOnly_StickingOutOfLeftFacingCorridor_DoesNotFireCoveredJump(float startX)
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(startX, 60.5f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Space = true }),  // no direction
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        bool fired = frames.Any(f => f.State.Contains("CoveredJump"));
        Assert.False(fired,
            $"startX={startX}: Space pressed but no direction held — CoveredJump must not fire.");
    }
}
