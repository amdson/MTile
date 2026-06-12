using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Regression coverage for: ParkourState's SteeringRamp was sometimes anchored on a
// block INSET into a raised platform instead of the block at the edge.
//
// Root cause in ExposedUpperCornerChecker.TryFindInRange (and its lower twin):
// the side probe slab is ~18 px wide — slightly wider than one tile (16) — so when
// the body sits flush against the platform's facing face the probe can include both
// the edge tile and the next interior tile, both with identical WorldTop. The
// checker then picks by iteration order (tx ascending) with a strict `>` on
// tile.WorldTop, which for wallDir = -1 reliably selects the LEFTMOST (inset) tile
// instead of the rightmost (edge), because tx ascending sees the inset tile first
// and the strict comparison rejects the later edge tile at the tie.
//
// The structural fix is to require the tile's body-facing horizontal neighbor be
// empty — that is precisely what makes a tile an actual outer corner rather than
// a flat-top interior tile. After the fix, only the edge tile passes filtering,
// so iteration order is irrelevant.
//
// Body-relative geometry (standing posture floats Radius above ground):
//   body.Bottom    = groundTop − Radius
//   probe (Upper)  = StripBeside(wallDir, 18).WithVerticalRange(body.CenterY, body.Bottom)
//   probe (Lower)  = StripBeside(wallDir, 15).WithVerticalRange(body.Top,      body.CenterY)
//
// We drive the checkers directly so a failing assertion points at corner selection,
// not at any downstream ramp math.
public class ExposedCornerEdgeSelectionTests(ITestOutputHelper output)
{
    private const float TS = Chunk.TileSize; // 16

    // Build a standing body whose facing face (Right for wallDir=+1, Left for wallDir=-1)
    // sits exactly at wallFaceX, floating Radius above groundTop — matching the standing
    // posture GroundChecker maintains in steady state. Body.Left = wallFaceX (wallDir=-1)
    // puts the 18-px probe slab fully overlapping the platform's last AND second-to-last
    // tiles, which is the geometry that triggers the wrong-corner selection.
    private static PhysicsBody MakeBodyAtWall(float wallFaceX, int wallDir, float groundTop)
    {
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6),
                                   new Vector2(0f, 0f));
        float halfW = body.Position.X - body.Bounds.Left;
        float halfH = body.Position.Y - body.Bounds.Top;
        float centerX = wallDir == +1 ? wallFaceX - halfW : wallFaceX + halfW;
        body.Position = new Vector2(centerX, groundTop - PlayerCharacter.Radius - halfH);
        return body;
    }

    // Variant for an airborne body — caller chooses position.Y directly (used by the
    // above-head ledge-grab path, where the body's head must land in the platform row).
    private static PhysicsBody MakeBodyAtWallAtY(float wallFaceX, int wallDir, float centerY)
    {
        var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6),
                                   new Vector2(0f, 0f));
        float halfW = body.Position.X - body.Bounds.Left;
        float centerX = wallDir == +1 ? wallFaceX - halfW : wallFaceX + halfW;
        body.Position = new Vector2(centerX, centerY);
        return body;
    }

    // ── Upper corner / vault (Over) ────────────────────────────────────────

    // wallDir = -1: body approaches a wide raised platform from the right.
    // The edge corner is the platform's TOP-RIGHT corner = (platformRight, platformTop).
    // Pre-fix this returns (platformRight − TS, platformTop) — one tile inset.
    [Fact]
    public void UpperCorner_WidePlatform_FromRight_PicksRightEdge()
    {
        // Row 1 = wide platform top (y:16..31). Row 2 = ground (y:32..47).
        // Platform tiles at cols 2..6 inclusive (x:32..112).
        //
        //   0  1  2  3  4  5  6  7  8
        // 0 .  .  .  .  .  .  .  .  .
        // 1 .  .  X  X  X  X  X  .  .   ← raised platform, top at y=16, right at x=112
        // 2 X  X  X  X  X  X  X  X  X   ← ground, top at y=32
        var terrain = SimTerrain.FromAscii(@"
            .........
            ..XXXXX..
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float groundTop     = 2 * TS;   // 32
        const float platformTop   = 1 * TS;   // 16
        const float platformRight = 7 * TS;   // 112 (col 6 right = col 7 left)

        // Body's left face exactly at the platform's right face — both col 6 (edge)
        // and col 5 (inset) end up inside the 18-px probe slab, with identical
        // WorldTop, which is what trips the iteration-order tie-break bug.
        var body = MakeBodyAtWall(wallFaceX: platformRight, wallDir: -1, groundTop: groundTop);

        output.WriteLine(
            $"body bounds: {body.Bounds}, probe X = [{body.Bounds.Left - 18:F2}, {body.Bounds.Left:F2}]");

        bool found = ExposedUpperCornerChecker.TryFind(
            body, terrain, wallDir: -1, out var corner);

        Assert.True(found, "Expected the wide platform's upper-right corner to be detected.");
        output.WriteLine($"corner.InnerEdge = {corner.InnerEdge}");
        Assert.Equal(platformRight, corner.InnerEdge.X);
        Assert.Equal(platformTop,   corner.InnerEdge.Y);
    }

    // wallDir = +1: body approaches the platform from the left. Edge corner is
    // (platformLeft, platformTop). Same probe-slab tie geometry, mirrored: pre-fix
    // this direction usually passes because tx ascending coincidentally picks the
    // leftmost = edge tile, but document the expectation so the fix can't regress it.
    [Fact]
    public void UpperCorner_WidePlatform_FromLeft_PicksLeftEdge()
    {
        var terrain = SimTerrain.FromAscii(@"
            .........
            ..XXXXX..
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float groundTop    = 2 * TS;
        const float platformTop  = 1 * TS;
        const float platformLeft = 2 * TS;    // 32

        var body = MakeBodyAtWall(wallFaceX: platformLeft, wallDir: +1, groundTop: groundTop);

        bool found = ExposedUpperCornerChecker.TryFind(
            body, terrain, wallDir: 1, out var corner);

        Assert.True(found, "Expected the wide platform's upper-left corner to be detected.");
        output.WriteLine($"corner.InnerEdge = {corner.InnerEdge}");
        Assert.Equal(platformLeft, corner.InnerEdge.X);
        Assert.Equal(platformTop,  corner.InnerEdge.Y);
    }

    // The same bug-prone iteration also drives TryFindAboveHead (ledge-grab probe).
    // Body floats in midair with its head inside the platform's vertical range so
    // the single-line head probe lands on the platform row. Same pattern.
    [Fact]
    public void UpperCorner_AboveHead_WidePlatform_FromRight_PicksRightEdge()
    {
        var terrain = SimTerrain.FromAscii(@"
            .........
            ..XXXXX..
            .........
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float platformTop   = 1 * TS;
        const float platformRight = 7 * TS;

        // Body in midair with centerY chosen so body.Top falls inside row 1's y range.
        var body = MakeBodyAtWallAtY(wallFaceX: platformRight, wallDir: -1, centerY: 30f);

        output.WriteLine(
            $"body bounds: {body.Bounds}, headY = {body.Bounds.Top:F2}, " +
            $"probe X = [{body.Bounds.Left - 18:F2}, {body.Bounds.Left:F2}]");

        bool found = ExposedUpperCornerChecker.TryFindAboveHead(
            body, terrain, wallDir: -1, out var corner);

        Assert.True(found, "Expected the wide platform's upper-right corner above head.");
        output.WriteLine($"corner.InnerEdge = {corner.InnerEdge}");
        Assert.Equal(platformRight, corner.InnerEdge.X);
        Assert.Equal(platformTop,   corner.InnerEdge.Y);
    }

    // ── Lower corner / overcrop (Under) ────────────────────────────────────

    // The lower-corner probe's ProbeSlack is 15 (one px narrower than a tile), so a
    // body sitting flush outside a wide slab generally only overlaps ONE column — the
    // multi-tile tie that breaks the upper checker doesn't fire here in steady state.
    // These tests pin the correct edge so the body-facing-empty fix (applied to the
    // lower checker for symmetry) doesn't regress single-column lookups.

    [Fact]
    public void LowerCorner_WideSlab_FromRight_PicksRightEdge()
    {
        // Slab at row 1 (Y=[16, 32]) so it overlaps the body's [Top, CenterY] probe
        // when the body stands floating above row 3 ground (groundTop=48).
        var terrain = SimTerrain.FromAscii(@"
            .........
            ..XXXXX..
            .........
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float groundTop  = 3 * TS;   // 48
        const float slabBottom = 2 * TS;   // 32 (= bottom of row 1)
        const float slabRight  = 7 * TS;   // 112

        var body = MakeBodyAtWall(wallFaceX: slabRight, wallDir: -1, groundTop: groundTop);

        bool found = ExposedLowerCornerChecker.TryFind(
            body, terrain, wallDir: -1, out var corner);

        Assert.True(found, "Expected the wide slab's lower-right corner to be detected.");
        output.WriteLine($"corner.InnerEdge = {corner.InnerEdge}");
        Assert.Equal(slabRight,  corner.InnerEdge.X);
        Assert.Equal(slabBottom, corner.InnerEdge.Y);
    }

    // ── Vault precondition band: ExposedUpperCornerChecker.TryFind ────────
    //
    // The new check is gated on a 0.5..1.2-tile band relative to the body's
    // standing base (= body.Position.Y + 2 * Radius). A 1-tile step falls
    // squarely in the band; a 2-tile-tall wall corner sits below the lower
    // edge of the band and must not fire ParkourState.

    // 1-tile platform: edge corner must be detected for a standing body.
    [Fact]
    public void UpperCorner_OneTileStep_FiresAtStandingHeight()
    {
        // Row 2 col 8 is a 1-tile-tall step above the row 3 ground.
        var terrain = SimTerrain.FromAscii(@"
            .........
            .........
            ........X
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float groundTop = 3 * TS;          // 48
        const float stepTop   = 2 * TS;          // 32 — col 8 row 2 (1-tile step)
        const float stepLeft  = 8 * TS;          // 128

        var body = MakeBodyAtWall(wallFaceX: stepLeft, wallDir: +1, groundTop: groundTop);

        bool found = ExposedUpperCornerChecker.TryFind(
            body, terrain, wallDir: 1, out var corner);

        output.WriteLine($"standingBase={body.Position.Y + 2 * PlayerCharacter.Radius}, corner={corner.InnerEdge}");
        Assert.True(found, "Expected 1-tile step to fire vault precondition.");
        Assert.Equal(stepLeft, corner.InnerEdge.X);
        Assert.Equal(stepTop,  corner.InnerEdge.Y);
    }

    // 2-tile-tall wall: corner top is 2 tiles above standing base. Outside the
    // 0.5..1.2 band → TryFind must return false (the old check fired here too,
    // but the new band is the explicit guarantee).
    [Fact]
    public void UpperCorner_TwoTileWall_DoesNotFire()
    {
        // 2-block-tall wall starting at col 8, ground at row 4.
        var terrain = SimTerrain.FromAscii(@"
            .........
            ........X
            ........X
            ........X
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float groundTop = 4 * TS;
        const float wallLeft  = 8 * TS;

        var body = MakeBodyAtWall(wallFaceX: wallLeft, wallDir: +1, groundTop: groundTop);

        bool found = ExposedUpperCornerChecker.TryFind(
            body, terrain, wallDir: 1, out _);

        output.WriteLine($"standingBase={body.Position.Y + 2 * PlayerCharacter.Radius}, found={found}");
        Assert.False(found, "2-tile-tall wall must NOT fire the vault precondition.");
    }

    // Half-tile-or-less "step": corner is at or above the upper edge of the
    // band, so the body would just walk over it — vault must not fire.
    // (We can't actually express a half-tile step in tile-aligned terrain, so
    // approximate: place the body slightly elevated so the floor-aligned step
    // sits less than 0.5 tiles below the band.)
    [Fact]
    public void UpperCorner_StepAtOrAboveStandingHeight_DoesNotFire()
    {
        var terrain = SimTerrain.FromAscii(@"
            .........
            ........X
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float groundTop = 2 * TS;       // 32
        const float wallLeft  = 8 * TS;

        // Lift the body so its standing base is one whole tile ABOVE ground top.
        // Then the 1-tile step (top at groundTop - TS = 16) is exactly at the
        // standing base, i.e. 0 tiles above it — under the 0.5-tile minimum.
        var body = MakeBodyAtWall(wallFaceX: wallLeft, wallDir: +1, groundTop: groundTop);
        body.Position = new Vector2(body.Position.X, body.Position.Y - TS);

        bool found = ExposedUpperCornerChecker.TryFind(
            body, terrain, wallDir: 1, out _);

        output.WriteLine($"standingBase={body.Position.Y + 2 * PlayerCharacter.Radius}, found={found}");
        Assert.False(found, "Step at the body's standing height must NOT fire vault.");
    }

    [Fact]
    public void LowerCorner_WideSlab_FromLeft_PicksLeftEdge()
    {
        var terrain = SimTerrain.FromAscii(@"
            .........
            ..XXXXX..
            .........
            XXXXXXXXX", originTileX: 0, originTileY: 0);

        const float groundTop  = 3 * TS;
        const float slabBottom = 2 * TS;
        const float slabLeft   = 2 * TS;   // 32

        var body = MakeBodyAtWall(wallFaceX: slabLeft, wallDir: +1, groundTop: groundTop);

        bool found = ExposedLowerCornerChecker.TryFind(
            body, terrain, wallDir: 1, out var corner);

        Assert.True(found, "Expected the wide slab's lower-left corner to be detected.");
        output.WriteLine($"corner.InnerEdge = {corner.InnerEdge}");
        Assert.Equal(slabLeft,   corner.InnerEdge.X);
        Assert.Equal(slabBottom, corner.InnerEdge.Y);
    }
}
