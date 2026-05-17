using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// DropdownState should only activate when some portion of the body's bounding box is
// hanging over the edge — analogous to CoveredJump's "any portion sticking out" gate.
// Hex body is ~16.46 wide (vertices at ±8.23 in X).
//
// Terrain has a platform at row 5 cols 0..9, so the drop edge to the right is at x=160.
public class DropdownTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    private const string Terrain = @"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX";

    // body.Bounds.Right = startX + 8.23; edge x=160; hanging ↔ body.Right > 160 ↔ startX > 151.77.
    [Theory]
    [InlineData(152.0f)]  // body.Right ≈ 160.23 — barely hanging (~0.2 px)
    [InlineData(156.0f)]  // body.Right ≈ 164.23 — ~4 px hanging
    [InlineData(160.0f)]  // body center at edge; half hanging
    [InlineData(165.0f)]  // body center 5 px past edge; mostly hanging
    public void HoldDown_HangingOverRightEdge_FiresDropdown(float startX)
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            // Platform top at y=80, body radius 9.5 → standing-rest center.Y = 60.5.
            StartPosition = new Vector2(startX, 60.5f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Down = true }),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        bool fired = frames.Any(f => f.State.Contains("Dropdown"));

        if (!fired)
        {
            output.WriteLine($"FAILURE at startX={startX}:");
            string prev = "";
            foreach (var f in frames)
            {
                if (f.State == prev) continue;
                output.WriteLine($"  frame {f.Frame,3} x={f.X,7:F2} y={f.Y,6:F2}  {f.State}");
                prev = f.State;
            }
        }

        Assert.True(fired,
            $"startX={startX} (body.Right≈{startX+8.23f:F2}, edge=160): body has " +
            $"{startX+8.23f-160f:F2} px hanging over edge — DropdownState should fire.");
    }

    // Body fully on the platform (no portion past the drop edge): DropdownState must NOT
    // activate. This is the user-reported bug — Dropdown was firing too eagerly.
    [Theory]
    [InlineData(80.0f)]   // way back on platform
    [InlineData(120.0f)]  // middle of platform
    [InlineData(144.0f)]  // body center on col 9 (last solid col), body.Right ≈ 152.23 < 160
    [InlineData(151.0f)]  // body.Right ≈ 159.23 — just barely still on the platform
    public void HoldDown_FullyOnPlatform_DoesNotFireDropdown(float startX)
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(startX, 60.5f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Down = true }),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        bool fired = frames.Any(f => f.State.Contains("Dropdown"));

        if (fired)
        {
            output.WriteLine($"UNEXPECTED Dropdown at startX={startX}:");
            string prev = "";
            foreach (var f in frames)
            {
                if (f.State == prev) continue;
                output.WriteLine($"  frame {f.Frame,3} x={f.X,7:F2} y={f.Y,6:F2}  {f.State}");
                prev = f.State;
            }
        }

        Assert.False(fired,
            $"startX={startX} (body.Right≈{startX+8.23f:F2}, edge=160): body is fully on " +
            $"the platform — DropdownState must not fire.");
    }

    // Hanging over the right edge with no horizontal input → Dropdown must slide RIGHT
    // (the side the body is actually hanging off), not left. Standstill direction-confusion
    // bug: the body's column being empty made the algorithm report a spurious left edge,
    // and the closer-edge tiebreak could pick the wrong side.
    [Theory]
    [InlineData(152.0f)]
    [InlineData(156.0f)]
    [InlineData(160.0f)]
    [InlineData(165.0f)]
    public void HoldDown_HangingOverRightEdge_SlidesRight(float startX)
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(startX, 60.5f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Down = true }),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        // Find the first frame inside DropdownState; body's X by end of run must be
        // strictly to the right of where it started.
        var last = frames[^1];
        Assert.True(last.X > startX + 1f,
            $"startX={startX}: body should slide right off the platform, but ended at " +
            $"X={last.X:F2}. Final state: {last.State}.");
    }

    // Mirror: a LEFT-edge drop. Platform cols 10..19, drop edge at x=160 (left side of col 10).
    // Body hanging over the left edge with no horizontal input must slide LEFT.
    private const string LeftEdgeTerrain = @"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX";

    [Theory]
    [InlineData(168.0f)]  // body.Left ≈ 159.77 — barely hanging (~0.2 px past edge)
    [InlineData(164.0f)]  // body.Left ≈ 155.77 — ~4 px hanging
    [InlineData(160.0f)]  // body center at edge
    [InlineData(155.0f)]  // body mostly off (center 5 px past edge)
    public void HoldDown_HangingOverLeftEdge_SlidesLeft(float startX)
    {
        var terrain = SimTerrain.FromAscii(LeftEdgeTerrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(startX, 60.5f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Down = true }),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        var last = frames[^1];
        Assert.True(last.X < startX - 1f,
            $"startX={startX}: body should slide left off the platform, but ended at " +
            $"X={last.X:F2}. Final state: {last.State}.");
    }
}
