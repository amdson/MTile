using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// DropdownState should only activate when some portion of the body's bounding box is
// hanging over the edge — analogous to CoveredJump's "any portion sticking out" gate.
// Half-width hex body: X extent ±6 (PlayerCharacter.BodyWidthScale), so
// hanging over a right edge at x=110 (10 * Chunk.TileSize) ⇔ startX > 104.
//
// Terrain has a platform at row 5 cols 0..9, so the drop edge to the right is at
// x = 10 * Chunk.TileSize = 110.
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

    // body.Bounds.Right = startX + 6 (half-width hexagon); edge x=110 (10 * Chunk.TileSize);
    // hanging ↔ body.Right > 110 ↔ startX > 104.
    [Theory]
    [InlineData(105.5f)]  // body.Right ≈ 111.5 — ~1.5 px hanging
    [InlineData(107.0f)]  // body.Right ≈ 113 — ~3 px hanging
    [InlineData(110.0f)]  // body center at edge; half hanging
    [InlineData(113.0f)]  // body center 3 px past edge; mostly hanging
    public void HoldDown_HangingOverRightEdge_FiresDropdown(float startX)
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            // Platform top now at y=55 (row 5 * Chunk.TileSize). 60.5 (carried over from
            // the old grid, where platform top was 80) put the body 5.5px BELOW the new
            // platform top — embedded in the corner rather than resting just above it,
            // which sent the overlap-resolution push sideways into open air instead of
            // straight up, masking the hang test entirely. 35.5 sits above the new
            // platform top and settles onto it normally (verified empirically: settles
            // to a standing rest ~31-34 within a few frames on flat interior ground).
            StartPosition = new Vector2(startX, 35.5f),
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
            $"startX={startX} (body.Right≈{startX+6f:F2}, edge=110): body has " +
            $"{startX+6f-110f:F2} px hanging over edge — DropdownState should fire.");
    }

    // Body fully on the platform (no portion past the drop edge): DropdownState must NOT
    // activate. This is the user-reported bug — Dropdown was firing too eagerly.
    [Theory]
    [InlineData(80.0f)]   // way back on platform
    [InlineData(120.0f)]  // middle of platform
    [InlineData(148.0f)]  // body center on col 9 (last solid col), body.Right = 154 < 160
    [InlineData(153.5f)]  // body.Right = 159.5 — just barely still on the platform
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
            $"startX={startX} (body.Right≈{startX+6f:F2}, edge=160): body is fully on " +
            $"the platform — DropdownState must not fire.");
    }

    // Hanging over the right edge with no horizontal input → Dropdown must slide RIGHT
    // (the side the body is actually hanging off), not left. Standstill direction-confusion
    // bug: the body's column being empty made the algorithm report a spurious left edge,
    // and the closer-edge tiebreak could pick the wrong side.
    [Theory]
    [InlineData(105.5f)]
    [InlineData(107.0f)]
    [InlineData(110.0f)]
    [InlineData(113.0f)]
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

    // Mirror: a LEFT-edge drop. Platform cols 10..19, drop edge at x=110 (left side of col 10,
    // = 10 * Chunk.TileSize).
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

    // Dropdown → LedgeGrab chain: releasing Down once the slide is COMMITTED (body
    // center past the drop edge) catches the lip instead of cancelling — Dropdown.Exit
    // offers the corner through abilities (DropChainDir) and LedgeGrab's path D takes
    // it the same frame. The body should end up hanging on the wall face below the
    // edge, not standing back on the platform or falling away.
    [Fact]
    public void ReleaseDown_PastTheEdge_ChainsIntoLedgeGrab()
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            // 35.5, not 60.5 (see HoldDown_HangingOverRightEdge_FiresDropdown): 60.5 sits
            // below the new platform top (55) and embeds the body in the corner.
            StartPosition = new Vector2(107f, 35.5f),
            StartVelocity = Vector2.Zero,
            // Hold Down until the body center is past the drop edge (x=110), then release.
            Script        = new InputScript()
                .Until(new PlayerInput { Down = true }, f => f.X > 111f)
                .Forever(default),
            Frames        = 90,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        bool grabbed = frames.Any(f => f.State.Contains("LedgeGrab"));
        var last = frames[^1];

        if (!grabbed || !last.State.Contains("LedgeGrab"))
        {
            string prev = "";
            foreach (var f in frames)
            {
                if (f.State == prev) continue;
                output.WriteLine($"  frame {f.Frame,3} x={f.X,7:F2} y={f.Y,6:F2}  {f.State}");
                prev = f.State;
            }
        }

        Assert.True(grabbed, "late Down release (center past the edge) should chain into LedgeGrab.");
        Assert.True(last.State.Contains("LedgeGrab"),
            $"the chained grab should hold (nothing pressed after release), but final state is {last.State}.");
        // Hanging pose: on the wall face right of the edge, body dropped below standing height.
        // (Corner/platform-top Y = row 5 * Chunk.TileSize = 55, down from the old grid's 80,
        // so the hang-pose Y threshold drops by the same 25 px as the corner itself.)
        Assert.True(last.X > 110f, $"hang should be off the right face of the edge, but X={last.X:F2}.");
        Assert.True(last.Y > 45f, $"hang should sit below the platform top, but Y={last.Y:F2}.");
    }

    // Early release — body center still short of the edge — stays a plain cancel:
    // no grab; the body settles back on the platform. Runs at 1/60 (the clip-driven
    // slide crosses the whole pre-lip stretch in a single 1/30 step) and releases on
    // a position threshold safely short of x=160.
    [Fact]
    public void ReleaseDown_BeforeTheEdge_CancelsWithoutGrab()
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(155.5f, 60.5f),
            StartVelocity = Vector2.Zero,
            // Release as soon as the slide has visibly started, well before center
            // crosses x=160 (Until reads the previous frame; the exit-frame check
            // reads the pre-step position, so no overshoot past the lip).
            Script        = new InputScript()
                .Until(new PlayerInput { Down = true }, f => f.X > 156.5f)
                .Forever(default),
            Frames        = 120,
            Dt            = 1f / 60f,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        if (frames.Any(f => f.State.Contains("LedgeGrab")))
        {
            string prev = "";
            foreach (var f in frames)
            {
                if (f.State == prev) continue;
                output.WriteLine($"  frame {f.Frame,3} x={f.X,7:F2} y={f.Y,6:F2}  {f.State}");
                prev = f.State;
            }
        }

        Assert.DoesNotContain(frames, f => f.State.Contains("LedgeGrab"));
    }

    // Held throughout: the classic slide-off. The chain must NOT fire — the offer
    // requires Down to be RELEASED.
    [Fact]
    public void HoldDown_Throughout_NeverChainsIntoLedgeGrab()
    {
        var terrain = SimTerrain.FromAscii(Terrain, originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            // 35.5, not 60.5 — see HoldDown_HangingOverRightEdge_FiresDropdown.
            StartPosition = new Vector2(107f, 35.5f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Down = true }),
            Frames        = 60,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        Assert.Contains(frames, f => f.State.Contains("Dropdown"));
        Assert.DoesNotContain(frames, f => f.State.Contains("LedgeGrab"));
    }

    [Theory]
    [InlineData(114.5f)]  // body.Left ≈ 108.5 — ~1.5 px hanging past edge
    [InlineData(113.0f)]  // body.Left ≈ 107 — ~3 px hanging
    [InlineData(110.0f)]  // body center at edge
    [InlineData(107.0f)]  // body mostly off (center 3 px past edge)
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
