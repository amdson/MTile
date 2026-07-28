using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Reproduces the "CoveredJump occasionally doesn't activate" bug for a left-facing corridor.
// Spec from the user: the jump should fire any time a portion of the body's bounding box is
// sticking out from under the overcrop. Half-width hex body: X extent ±6.
// Corridor here has its left exit corner at x=80; "any portion sticking out" means
// body.Bounds.Left < 80, i.e. body center X < 86.
public class CoveredJumpLeftCorridorTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    // Mirror of HoldSpaceRight_CoveredJumpOutOfTunnel. Ceiling slab at cols 5..19,
    // so its LEFT edge (the exit corner) is at x = 5 * 16 = 80. Floor top at y=80.
    private const string Terrain = @"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOXXXXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXX";

    // For each startX in the "sticking out" range, CoveredJump must activate.
    // body.Bounds.Left = startX − 8.23; corner is at x=80; sticking out ↔ body.Left < 80
    //                                                       ↔ startX < 88.23.
    [Theory]
    [InlineData(85.5f)]   // body.Left = 79.5 — barely sticking out (0.5 px)
    [InlineData(85.0f)]   // body.Left = 79 — 1 px sticking out
    [InlineData(82.0f)]   // body.Left = 76 — 4 px sticking out
    [InlineData(80.0f)]   // body center at corner; half body sticking out
    [InlineData(79.5f)]   // body center just past corner into open air
    [InlineData(78.0f)]   // body center 2 px past corner
    [InlineData(75.0f)]   // body center 5 px past corner; most of body in open air
    [InlineData(75.5f)]   // body.Right = 81.5 — ~1.5 px still under the slab (the ceiling
                          // probe's 2px horizontal inset sets the detection floor; below
                          // ~0.5px of probed overlap a plain jump is the correct read)
    public void HoldSpaceLeft_StickingOutOfLeftFacingCorridor_FiresCoveredJump(float startX)
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            // Standing-rest center.Y for floor at y=80: matches the right-facing test's value.
            StartPosition = new Vector2(startX, 60.5f),
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
            $"startX={startX} (body.Left≈{startX-8.23f:F2}, corner=80.0): body has " +
            $"{80f-(startX-6f):F2} px sticking out past corner — CoveredJump should fire.");
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
