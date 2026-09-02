using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// Constraint-builder fixtures (BALLISTIC_CORRECTOR_PLAN build step 2): ASCII
// terrains + hand-built coast polylines, rows asserted against hand-computed
// {tick, normal, depth}. The hex body: R = 12, supports ±10.3923 in x, ±12 in y.
public class ClearanceConstraintTests
{
    private const float HexX = 10.3923048f;   // 12·sin(60°) — side support
    private const float HexY = 12f;           // top/bottom vertex support
    private const float Tol  = 1e-3f;

    private static readonly Polygon Hex = Polygon.CreateRegular(PlayerCharacter.Radius, 6);

    private static CoastSample[] Samples(params Vector2[] positions)
    {
        var s = new CoastSample[positions.Length];
        for (int i = 0; i < positions.Length; i++)
            s[i] = new CoastSample { Pos = positions[i], FloorY = float.PositiveInfinity };
        return s;
    }

    private static int Build(ChunkMap terrain, CoastSample[] samples, float margin,
                             ClearanceRow[] rows, out int truncatedAt,
                             float deep = ClearanceConstraintBuilder.DefaultDeepViolation)
        => ClearanceConstraintBuilder.Build(terrain, Hex, samples, samples.Length,
                                            margin, deep, rows, out truncatedAt);

    [Fact]
    public void FloorGraze_SingleTile_TopRowWithSupportDepth()
    {
        // Tile at (2,6): x ∈ [22,33), y ∈ [66,77). Body over its center, bottom
        // support + margin crossing the top plane by 1.5px.
        var terrain = SimTerrain.FromAscii("X", originTileX: 2, originTileY: 6);
        float topY = 6 * Chunk.TileSize;
        float cx = 2 * Chunk.TileSize + Chunk.TileSize / 2f;
        var samples = Samples(new Vector2(cx, topY - HexY - 2f + 1.5f));
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        int n = Build(terrain, samples, margin: 2f, rows, out int trunc);

        Assert.Equal(1, n);
        Assert.Equal(samples.Length, trunc);
        Assert.Equal(0, rows[0].Tick);
        Assert.Equal(new Vector2(0f, -1f), rows[0].Normal);
        Assert.True(MathF.Abs(rows[0].Depth - 1.5f) < Tol, $"depth {rows[0].Depth}");
    }

    [Fact]
    public void ContiguousViolation_OneRow_AnchoredAtWorstTick()
    {
        var terrain = SimTerrain.FromAscii("X", originTileX: 2, originTileY: 6);
        float topY = 6 * Chunk.TileSize;
        float cx = 2 * Chunk.TileSize + Chunk.TileSize / 2f;
        float yFor(float depth) => topY - HexY - 2f + depth;
        var samples = Samples(
            new Vector2(cx, yFor(1f)),
            new Vector2(cx, yFor(3f)),
            new Vector2(cx, yFor(2f)),
            new Vector2(cx, 20f));            // clear — closes the run
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        int n = Build(terrain, samples, margin: 2f, rows, out int trunc);

        Assert.Equal(1, n);
        Assert.Equal(samples.Length, trunc);
        Assert.Equal(1, rows[0].Tick);        // the depth-3 tick
        Assert.Equal(new Vector2(0f, -1f), rows[0].Normal);
        Assert.True(MathF.Abs(rows[0].Depth - 3f) < Tol, $"depth {rows[0].Depth}");
    }

    [Fact]
    public void DeepViolation_TruncatesScan_RowStillEmitted()
    {
        var terrain = SimTerrain.FromAscii("X", originTileX: 2, originTileY: 6);
        float topY = 6 * Chunk.TileSize;
        float cx = 2 * Chunk.TileSize + Chunk.TileSize / 2f;
        var samples = Samples(
            new Vector2(cx, topY - HexY - 2f + 10f),   // 10px ≥ 8px deep threshold
            new Vector2(cx, 20f),                       // never scanned
            new Vector2(cx, 20f));
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        int n = Build(terrain, samples, margin: 2f, rows, out int trunc);

        Assert.Equal(1, n);
        Assert.Equal(0, trunc);               // truncated AT the impact tick
        Assert.Equal(0, rows[0].Tick);
        Assert.Equal(new Vector2(0f, -1f), rows[0].Normal);
        Assert.True(MathF.Abs(rows[0].Depth - 10f) < Tol, $"depth {rows[0].Depth}");
    }

    [Fact]
    public void Tunnel_CeilingGraze_EmitsDownwardRow()
    {
        // Ceiling row 3, floor row 6, 8 tiles wide.
        var terrain = SimTerrain.FromAscii(
            """
            XXXXXXXX
            ........
            ........
            XXXXXXXX
            """, originTileX: 0, originTileY: 3);
        float ceilBottomY = 4 * Chunk.TileSize;
        float floorTopY = 6 * Chunk.TileSize;
        float cx = 4 * Chunk.TileSize;   // 8-tile-wide tunnel center
        // Body mid-tunnel: no rows. Head within margin of the ceiling: one (0,1) row.
        var clear = Samples(new Vector2(cx, (ceilBottomY + floorTopY) / 2f));
        var graze = Samples(new Vector2(cx, ceilBottomY + HexY + 2f - 1f));   // m = 1
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        Assert.Equal(0, Build(terrain, clear, margin: 2f, rows, out _));

        int n = Build(terrain, graze, margin: 2f, rows, out int trunc);
        Assert.Equal(1, n);
        Assert.Equal(graze.Length, trunc);
        Assert.Equal(new Vector2(0f, 1f), rows[0].Normal);
        Assert.True(MathF.Abs(rows[0].Depth - 1f) < Tol, $"depth {rows[0].Depth}");
    }

    [Fact]
    public void Pinch_FloorAndCeiling_TwoRowsSameTick()
    {
        // Gap = 2 tiles (22px), body 24px tall + margin 5 ⇒ both planes violated
        // by margin + (2·HexY − gap)/2 = 5 + (24−22)/2 = 6px.
        var terrain = SimTerrain.FromAscii(
            """
            XXXXXXXX
            ........
            ........
            XXXXXXXX
            """, originTileX: 0, originTileY: 3);
        float ceilBottomY = 4 * Chunk.TileSize;
        float floorTopY = 6 * Chunk.TileSize;
        float cx = 4 * Chunk.TileSize;
        var samples = Samples(new Vector2(cx, (ceilBottomY + floorTopY) / 2f));   // dead center
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];
        float margin = 5f;
        float gap = floorTopY - ceilBottomY;
        float expectedDepth = margin + (2f * HexY - gap) / 2f;

        int n = Build(terrain, samples, margin, rows, out _);

        Assert.Equal(2, n);
        Assert.Contains(rows[..n], r => r.Normal == new Vector2(0f, -1f) && MathF.Abs(r.Depth - expectedDepth) < Tol);
        Assert.Contains(rows[..n], r => r.Normal == new Vector2(0f, 1f)  && MathF.Abs(r.Depth - expectedDepth) < Tol);
    }

    [Fact]
    public void Staircase_SideOfStep_EmitsSideRow_NotInteriorFaces()
    {
        // Flat floor row 6, one step tile at (6,5).
        // Its bottom face borders the floor row — NOT exposed; approach from the
        // left must yield a (-1,0) row from the step's left face.
        var terrain = SimTerrain.FromAscii(
            """
            ......X...
            XXXXXXXXXX
            """, originTileX: 0, originTileY: 5);
        float stepLeftX = 6 * Chunk.TileSize;
        float stepTopY = 5 * Chunk.TileSize;
        // 11px left of the step's left face, 2px above its top face — same
        // relative offsets as the original fixture (body-radius-scaled, not
        // tile-size-scaled), so the same facet (left) wins with the same depth.
        var samples = Samples(new Vector2(stepLeftX - 11f, stepTopY - 2f));
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        int n = Build(terrain, samples, margin: 2f, rows, out _);

        Assert.Equal(1, n);
        Assert.Equal(new Vector2(-1f, 0f), rows[0].Normal);
        float expected = (stepLeftX - 11f + HexX + 2f) - stepLeftX;   // infR − step left plane
        Assert.True(MathF.Abs(rows[0].Depth - expected) < Tol, $"depth {rows[0].Depth} vs {expected}");
    }

    [Fact]
    public void Overcrop_LipAhead_RowsOnlyWhereTheCoastGoes()
    {
        // Overhang: ceiling block hanging into the corridor ahead; a coast that
        // stays low never violates it, a coast through it does. Relevance is the
        // prediction, not a scan pattern. Interior widened to 4 rows (44px) so a
        // body (24px) + margin genuinely fits under the lip on the new grid.
        var terrain = SimTerrain.FromAscii(
            """
            ....XX....
            ..........
            ..........
            ..........
            ..........
            XXXXXXXXXX
            """, originTileX: 0, originTileY: 3);   // lip row 3, floor row 8
        float lipBottomY = 4 * Chunk.TileSize;
        float floorTopY = 8 * Chunk.TileSize;
        float clearY = (lipBottomY + floorTopY) / 2f;
        float col3X = 3 * Chunk.TileSize;                       // before the lip
        float col4MidX = 4 * Chunk.TileSize + Chunk.TileSize / 2f;   // under the lip
        float col6X = 6 * Chunk.TileSize;                       // just past the lip
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        // Low path under the lip: stays clear of both the lip bottom and the floor top.
        var low = Samples(new Vector2(col3X, clearY), new Vector2(col4MidX, clearY), new Vector2(col6X, clearY));
        Assert.Equal(0, Build(terrain, low, margin: 2f, rows, out _));

        // Rising path clipping the lip's bottom face by 2px at tick 1.
        float clipY = lipBottomY + HexY + 2f - 2f;
        var high = Samples(new Vector2(col3X, clearY), new Vector2(col4MidX, clipY), new Vector2(col6X, clearY));
        int n = Build(terrain, high, margin: 2f, rows, out int trunc);
        Assert.Equal(1, n);
        Assert.Equal(1, rows[0].Tick);
        Assert.Equal(new Vector2(0f, 1f), rows[0].Normal);
        Assert.True(MathF.Abs(rows[0].Depth - 2f) < Tol, $"depth {rows[0].Depth}");
    }
}
