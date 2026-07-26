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
        // Tile at (2,6): x ∈ [32,48), y ∈ [96,112). Body over its center, bottom
        // support + margin crossing the top plane by 1.5px.
        var terrain = SimTerrain.FromAscii("X", originTileX: 2, originTileY: 6);
        var samples = Samples(new Vector2(40f, 96f - HexY - 2f + 1.5f));
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
        float yFor(float depth) => 96f - HexY - 2f + depth;
        var samples = Samples(
            new Vector2(40f, yFor(1f)),
            new Vector2(40f, yFor(3f)),
            new Vector2(40f, yFor(2f)),
            new Vector2(40f, 20f));           // clear — closes the run
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
        var samples = Samples(
            new Vector2(40f, 96f - HexY - 2f + 10f),   // 10px ≥ 8px deep threshold
            new Vector2(40f, 20f),                      // never scanned
            new Vector2(40f, 20f));
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
        // Ceiling row 3 (y ∈ [48,64)), floor row 6 (y ∈ [96,112)), 8 tiles wide.
        var terrain = SimTerrain.FromAscii(
            """
            XXXXXXXX
            ........
            ........
            XXXXXXXX
            """, originTileX: 0, originTileY: 3);
        // Body mid-tunnel: no rows. Head within margin of the ceiling: one (0,1) row.
        var clear = Samples(new Vector2(64f, 80f));
        var graze = Samples(new Vector2(64f, 64f + HexY + 2f - 1f));   // m = 1
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
        // Gap 32px, body 24px tall + margin 5 ⇒ both planes violated by 1px.
        var terrain = SimTerrain.FromAscii(
            """
            XXXXXXXX
            ........
            ........
            XXXXXXXX
            """, originTileX: 0, originTileY: 3);
        var samples = Samples(new Vector2(64f, 80f));   // dead center
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        int n = Build(terrain, samples, margin: 5f, rows, out _);

        Assert.Equal(2, n);
        Assert.Contains(rows[..n], r => r.Normal == new Vector2(0f, -1f) && MathF.Abs(r.Depth - 1f) < Tol);
        Assert.Contains(rows[..n], r => r.Normal == new Vector2(0f, 1f)  && MathF.Abs(r.Depth - 1f) < Tol);
    }

    [Fact]
    public void Staircase_SideOfStep_EmitsSideRow_NotInteriorFaces()
    {
        // Flat floor row 6, one step tile at (6,5): x ∈ [96,112), y ∈ [80,96).
        // Its bottom face borders the floor row — NOT exposed; approach from the
        // left must yield a (-1,0) row from the step's left face.
        var terrain = SimTerrain.FromAscii(
            """
            ......X...
            XXXXXXXXXX
            """, originTileX: 0, originTileY: 5);
        var samples = Samples(new Vector2(85f, 78f));   // running height, face 1.39px into the plane
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        int n = Build(terrain, samples, margin: 2f, rows, out _);

        Assert.Equal(1, n);
        Assert.Equal(new Vector2(-1f, 0f), rows[0].Normal);
        float expected = (85f + HexX + 2f) - 96f;   // infR − step left plane
        Assert.True(MathF.Abs(rows[0].Depth - expected) < Tol, $"depth {rows[0].Depth} vs {expected}");
    }

    [Fact]
    public void Overcrop_LipAhead_RowsOnlyWhereTheCoastGoes()
    {
        // Overhang: ceiling block hanging into the corridor ahead; a coast that
        // stays low never violates it, a coast through it does. Relevance is the
        // prediction, not a scan pattern.
        var terrain = SimTerrain.FromAscii(
            """
            ....XX....
            ..........
            ..........
            XXXXXXXXXX
            """, originTileX: 0, originTileY: 3);   // lip y ∈ [48,64), floor row 6 (y ∈ [96,112))
        var rows = new ClearanceRow[ClearanceConstraintBuilder.MaxEvents];

        // Low path under the lip: inflated body spans [66,94] at y=80 — clears the
        // lip bottom (64) and the floor top (96) on both sides.
        var low = Samples(new Vector2(48f, 80f), new Vector2(64f, 80f), new Vector2(80f, 80f));
        Assert.Equal(0, Build(terrain, low, margin: 2f, rows, out _));

        // Rising path clipping the lip's bottom face by 2px at tick 1.
        var high = Samples(new Vector2(48f, 80f), new Vector2(72f, 64f + HexY + 2f - 2f), new Vector2(96f, 80f));
        int n = Build(terrain, high, margin: 2f, rows, out int trunc);
        Assert.Equal(1, n);
        Assert.Equal(1, rows[0].Tick);
        Assert.Equal(new Vector2(0f, 1f), rows[0].Normal);
        Assert.True(MathF.Abs(rows[0].Depth - 2f) < Tol, $"depth {rows[0].Depth}");
    }
}
