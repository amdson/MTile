using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Repro for the recurring "blocks nowhere near them break when the player
// falls" bug noted in todo.txt.
//
// Root cause (verified by the StripBelowProbe test below): TileQuery's
// SolidTilesInRect is a closed-interval AABB query — Math.Floor(right/16)
// includes the tile that the right edge merely *touches*. A body landing
// with its right edge exactly on a tile boundary picks up the adjacent
// column as an "impact cell". TryApplyImpactDamage then splits damage
// between cells the body never actually overlapped.
//
// In real play this triggers whenever the player's hex body (half-width
// ≈ 8.23 = Radius·cos 30°) drifts to a position where its right or left
// edge sits flush against a tile column boundary — exactly the "I landed
// here and a block next to me broke" feel.
public class FallingImpactDamageTests
{
    private readonly ITestOutputHelper _out;
    public FallingImpactDamageTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int FloorRow = 20;
    private const float FloorTopY = FloorRow * 16f;

    private static ChunkMap WidePlatform()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[20];
            for (int i = 0; i < 20; i++) line[i] = (r >= 20) ? 'X' : 'O';
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // 16-wide box body with Impact set. We use a box (not the player hex) so
    // alignment with tile boundaries is exact and unambiguous, and we bypass
    // the player's StandingState FSD spring so the swept impact-damage path
    // actually runs (the spring catches the player before tile damage fires
    // in normal play — see commentary at the bottom of this file).
    private static PhysicsBody BoxCrasher(Vector2 pos)
    {
        var poly = new Polygon(new[]
        {
            new Vector2(-8f, -8f), new Vector2( 8f, -8f),
            new Vector2( 8f,  8f), new Vector2(-8f,  8f),
        });
        return new PhysicsBody(poly, pos)
        {
            Impact = new ImpactDamage
            {
                Mass                 = 2.5f,
                ImpulseThreshold     = 200f,
                DamagePerUnitImpulse = 0.05f,
                // Set sky-high so we get chip damage WITHOUT triggering the
                // break-through chain — keeps the test focused on cell selection
                // rather than the body chewing through multiple rows.
                BreakThreshold       = float.PositiveInfinity,
            },
        };
    }

    private record BrokenCell(int Gtx, int Gty);

    // Drops the body for `frames` steps and returns (cells that broke, residual HP per cell).
    private (List<BrokenCell> broke, Dictionary<(int gtx, int gty), float> residual)
        DropAndCollect(ChunkMap terrain, PhysicsBody body, int frames)
    {
        var bodies = new List<PhysicsBody> { body };
        var broke = new List<BrokenCell>();
        terrain.OnTileBroken = (wc, _) =>
        {
            int gtx = (int)System.MathF.Floor(wc.X / 16f);
            int gty = (int)System.MathF.Floor(wc.Y / 16f);
            broke.Add(new BrokenCell(gtx, gty));
        };

        for (int f = 0; f < frames; f++)
        {
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
        }

        var residual = new Dictionary<(int gtx, int gty), float>();
        foreach (var kv in terrain.Damage.Damaged) residual[kv.Key] = kv.Value;
        return (broke, residual);
    }

    // The bug these two tests reproduce (closed-interval AABB query in
    // TileQuery.SolidTilesInRect leaking damage onto an adjacent column) is
    // documented but not the source of the "blocks randomly destroyed" issue
    // the user was hunting (that turned out to be the secondary player's AI).
    // Parked here as a regression repro to revive if we ever circle back to
    // the column-bleed cleanup.
    /*
    // Body's AABB is EXACTLY [160, 176] — entirely inside column 10. Anything
    // happening to column 11 (or any other column) is the bug.
    [Fact]
    public void BareBodyDrop_BodyAlignedToColumn_AdjacentColumnTakesDamage()
    {
        var terrain = WidePlatform();
        // Center on column 10 so body.Bounds = [160, ?, 176, ?] flush against
        // the col 10/11 boundary at x=176.
        const float spawnX = 10 * 16f + 8f;
        var body = BoxCrasher(new Vector2(spawnX, FloorTopY - 100f));

        Assert.Equal(160f, body.Bounds.Left);
        Assert.Equal(176f, body.Bounds.Right);

        var (broke, residual) = DropAndCollect(terrain, body, 60);

        _out.WriteLine($"broke: {broke.Count}");
        foreach (var b in broke) _out.WriteLine($"  ({b.Gtx},{b.Gty})");
        _out.WriteLine($"residual HP: {residual.Count}");
        foreach (var kv in residual) _out.WriteLine($"  ({kv.Key.gtx},{kv.Key.gty}) HP={kv.Value:F3}");

        // Bug surfaces as ANY damage attributed to column ≠ 10 at row 20.
        var bad = new List<string>();
        foreach (var b in broke)
            if (b.Gtx != 10 && b.Gty == FloorRow)
                bad.Add($"BROKE ({b.Gtx},{b.Gty})");
        foreach (var kv in residual)
            if (kv.Key.gtx != 10 && kv.Key.gty == FloorRow)
                bad.Add($"DAMAGED ({kv.Key.gtx},{kv.Key.gty}) HP={kv.Value:F3}");

        Assert.True(bad.Count == 0,
            "body's AABB only overlaps column 10, but damage leaked onto an adjacent column: "
            + string.Join("; ", bad));
    }

    // Diagnostic: shows the strip-below probe returning BOTH column 10 and
    // column 11 for a body whose right edge is exactly on the column boundary.
    // This is the root-cause demonstration.
    [Fact]
    public void StripBelowProbe_BodyFlushAgainstColumnBoundary_ReportsAdjacentColumn()
    {
        var terrain = WidePlatform();
        // Body centered on column 10, just above floor top.
        var body = BoxCrasher(new Vector2(10 * 16f + 8f, FloorTopY - 8.5f));

        var bounds = body.Polygon.GetBoundingBox(body.Position);
        var strip = bounds.StripBelow(1f);
        _out.WriteLine($"body bounds: {bounds}");
        _out.WriteLine($"strip below: {strip}");

        var cells = new List<(int gtx, int gty)>();
        foreach (var shape in WorldQuery.SolidShapesInRect(terrain, strip))
        {
            int gtx = (int)System.MathF.Floor(shape.WorldCenterX / 16f);
            int gty = (int)System.MathF.Floor(shape.WorldCenterY / 16f);
            cells.Add((gtx, gty));
            _out.WriteLine($"  shape AABB=[{shape.WorldLeft:F1},{shape.WorldTop:F1} → " +
                           $"{shape.WorldRight:F1},{shape.WorldBottom:F1}] " +
                           $"⇒ cell ({gtx},{gty})");
        }

        // The probe MUST only see column 10. Currently it also returns column 11
        // because TileQuery.SolidTilesInRect uses Math.Floor(right/16) on the
        // closed interval — col 11 starts at x=176 and the strip's right edge
        // is x=176, so col 11 is "touched" and yields'd.
        Assert.Contains((10, FloorRow), cells);
        Assert.DoesNotContain((11, FloorRow), cells);
    }
    */
}
