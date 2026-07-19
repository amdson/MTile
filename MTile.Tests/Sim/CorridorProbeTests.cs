using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Direct unit coverage for CorridorProbe.Scan (Plans/CORRIDOR_MANEUVER_PLAN.md piece 1).
// Fixtures mirror the plan's anchor scenarios; assertions are on the probe's OUTPUT
// (gates, corners, truncation) — no simulation stepping, no emergent motion. The probe
// is a pure function, so a body placed at standing rest is all the setup needed.
//
// Geometry conventions used throughout (Chunk.TileSize = 16, y-down):
//   row r spans y [16r, 16r+16); a floor "top" at row r is y = 16r.
//   A body standing on a floor at top Y rests with center at Y - 2·Radius (= Y - 19).
//   Corner x values are absolute: column c's left edge is 16c.
public class CorridorProbeTests(ITestOutputHelper output)
{
    private static PlayerCharacter StandingAt(float x, float floorTopY)
        => new(new Vector2(x, floorTopY - 2f * PlayerCharacter.Radius));

    private static Corridor Scan(ChunkMap terrain, PlayerCharacter p, int dir)
        => CorridorProbe.Scan(p.Body, terrain, dir);

    private void Dump(Corridor c)
    {
        output.WriteLine($"dir={c.Dir} firstCol={c.FirstColumn} count={c.ColumnCount} " +
                         $"trunc={c.Truncation}@{c.TruncationColumn} base={c.StandingBaseY}");
        for (int i = 0; i < c.ColumnCount; i++)
            output.WriteLine($"  col[{i}] x={c.ColumnEdgeX(i)} floor={c.FloorY[i]} ceil={c.CeilY[i]}");
        for (int i = 0; i < c.FloorCornerCount; i++)
            output.WriteLine($"  floorCorner {c.FloorCorners[i].Pos} Δ={c.FloorCorners[i].Delta} col={c.FloorCorners[i].Column}");
        for (int i = 0; i < c.CeilCornerCount; i++)
            output.WriteLine($"  ceilCorner  {c.CeilCorners[i].Pos} Δ={c.CeilCorners[i].Delta} col={c.CeilCorners[i].Column}");
    }

    // ── Open floor: nothing to report ─────────────────────────────────────

    [Fact]
    public void FlatFloor_FullHorizon_NoCorners()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Horizon, c.Truncation);
        Assert.Equal(MovementConfig.Current.CorridorHorizonColumns, c.ColumnCount);
        Assert.Equal(0, c.FloorCornerCount);
        Assert.Equal(0, c.CeilCornerCount);
        for (int i = 0; i < c.ColumnCount; i++)
        {
            Assert.Equal(48f, c.FloorY[i]);
            Assert.True(float.IsNegativeInfinity(c.CeilY[i]));
        }
    }

    // ── Anchor 1: one block up onto flat ──────────────────────────────────

    [Fact]
    public void OneBlockStep_SingleRiseCorner()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Horizon, c.Truncation);
        Assert.True(c.TryFirstRise(out var rise));
        Assert.Equal(new Vector2(128f, 32f), rise.Pos);
        Assert.Equal(16f, rise.Delta);
        Assert.Equal(c.ColumnIndexOf(8), rise.Column);
        Assert.Equal(1, c.FloorCornerCount);
        Assert.Equal(0, c.CeilCornerCount);
        Assert.Equal(48f, c.FloorY[rise.Column - 1]);
        Assert.Equal(32f, c.FloorY[rise.Column]);
    }

    [Fact]
    public void OneBlockStep_MirroredLeft_SameCornerAbsolutePosition()
    {
        // Step occupies cols 0..7; body approaches leftward from the right side.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(156f, 48f), -1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Horizon, c.Truncation);
        Assert.True(c.TryFirstRise(out var rise));
        Assert.Equal(new Vector2(128f, 32f), rise.Pos);   // col 7's right edge — same lip as the +1 case
        Assert.Equal(16f, rise.Delta);
        Assert.Equal(c.ColumnIndexOf(7), rise.Column);
    }

    // ── Anchor 2: 45° staircase — one 16px rise per column, no truncation ─

    [Fact]
    public void Staircase45_RiseCornerPerColumn()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOXXXXXXXXXX
            OOOOOOOOOXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Horizon, c.Truncation);
        Assert.Equal(2, c.FloorCornerCount);
        Assert.Equal(new Vector2(128f, 32f), c.FloorCorners[0].Pos);
        Assert.Equal(16f, c.FloorCorners[0].Delta);
        Assert.Equal(new Vector2(144f, 16f), c.FloorCorners[1].Pos);
        Assert.Equal(16f, c.FloorCorners[1].Delta);
    }

    // ── Anchor 3: tall risers — 2-block step is MEASURED, not truncated ───

    [Fact]
    public void TwoBlockRiser_MeasuredWithinManeuverEnvelope()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Horizon, c.Truncation);
        Assert.True(c.TryFirstRise(out var rise));
        Assert.Equal(32f, rise.Delta);                    // 2 blocks — ArcJump territory, still feasible
        Assert.Equal(new Vector2(128f, 16f), rise.Pos);
    }

    [Fact]
    public void ThreeBlockWall_TruncatesTallRise()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.TallRise, c.Truncation);
        Assert.Equal(c.ColumnIndexOf(8), c.TruncationColumn);
        Assert.Equal(c.TruncationColumn, c.ColumnCount);  // feasible prefix ends at the wall
        Assert.Equal(128f, c.ColumnEdgeX(c.TruncationColumn));
    }

    // ── Anchor 5: level entry into a 2-high tunnel ────────────────────────

    [Fact]
    public void TwoHighTunnelEntry_FeasibleWithCeilingLip()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Horizon, c.Truncation);
        Assert.Equal(0, c.FloorCornerCount);
        Assert.True(c.TryFirstCeilingLip(out var lip));
        Assert.Equal(new Vector2(128f, 16f), lip.Pos);    // roof bottom at the tunnel mouth
        Assert.Equal(c.ColumnIndexOf(8), lip.Column);
        Assert.Equal(32f, c.MinGap());                    // 2 tiles — tight standing fit, feasible
    }

    [Fact]
    public void OneHighGap_TruncatesPinch()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Pinch, c.Truncation);
        Assert.Equal(c.ColumnIndexOf(8), c.TruncationColumn);
        Assert.Equal(128f, c.ColumnEdgeX(c.TruncationColumn));
    }

    // ── Drop deeper than the window: not a corridor, a cliff ──────────────

    [Fact]
    public void DeepDropOff_TruncatesNoFloor()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXOOOOOOOOOOOO", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 48f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.NoFloor, c.Truncation);
        Assert.Equal(c.ColumnIndexOf(8), c.TruncationColumn);
    }

    // ── Anchor 6: one-block drop into a 2-high tunnel mouth ───────────────

    [Fact]
    public void OneBlockDropIntoTunnel_DropAndLipBothRecorded()
    {
        // Upper floor row 2 (cols 0..7); lower floor row 3 (cols 8+); roof row 0 (cols 8+).
        // The tunnel interior (rows 1-2 free, 16..48) is exactly 2 tiles high.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var c = Scan(terrain, StandingAt(100f, 32f), +1);
        Dump(c);

        Assert.Equal(CorridorTruncation.Horizon, c.Truncation);
        Assert.True(c.TryFirstDrop(out var drop));
        Assert.Equal(new Vector2(128f, 32f), drop.Pos);   // the old floor's exposed lip
        Assert.Equal(-16f, drop.Delta);
        Assert.True(c.TryFirstCeilingLip(out var lip));
        Assert.Equal(new Vector2(128f, 16f), lip.Pos);
        Assert.Equal(32f, c.MinGap());
        Assert.Equal(drop.Column, lip.Column);            // same mouth column: fused gates, one event
    }
}
