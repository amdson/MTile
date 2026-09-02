using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// A body must never end up permanently embedded in a block that GREW into it.
//
// PhysicsWorld's depenetration pre-pass resolves the deepest overlap per iteration on a
// fixed budget. Two surfaces whose push-out directions disagree defeat it: every
// iteration ejects from one straight into the other, the budget runs out, and the body
// settles into a stable embedded rest state with no damage and no signal.
//
// The rule: when the solver gives up still overlapping, any SPROUT it is overlapping is
// destroyed (CrushOverlappingSprouts) and billed to the body as SproutCrushCount.
// Committed tiles are left alone — see StaticWedge_DoesNotBreakTerrain.
public class SproutCrushTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    private static PhysicsBody PlayerBody(Vector2 pos)
        => new PhysicsBody(PlayerCharacter.CreateBodyPolygon(), pos);

    // Deepest penetration of the body into any solid shape at its current position.
    private static float MaxOverlap(PhysicsBody body, ChunkMap chunks)
    {
        float worst = 0f;
        foreach (var shape in WorldQuery.SolidShapesInRect(chunks, body.Bounds))
        {
            var hit = Collision.Check(body.Polygon, body.Position, 0f, shape.Polygon, shape.Position, 0f);
            if (hit.Intersects && hit.Depth > worst) worst = hit.Depth;
        }
        return worst;
    }

    // ── The oversight: a Sprouting cell used to be indestructible ───────────────────
    // BreakCell guarded on IsSolid (== TileState.Solid), so the one tile a body can be
    // actively crushed by was the one tile nothing could break.
    [Fact]
    public void BreakCell_DestroysAGrowingSprout()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            XOOOO
            XOOOO
            XXXXX");

        var node = terrain.TryRequestTile(1, 2);
        Assert.NotNull(node);
        Assert.Equal(TileState.Sprouting, terrain.GetCellState(1, 2));
        Assert.Single(terrain.Graph.Growing);

        Assert.True(terrain.BreakCell(1, 2), "BreakCell must accept a Sprouting cell");

        Assert.Equal(TileState.Empty, terrain.GetCellState(1, 2));
        // The node must leave Graph.Growing too: its face volumes are live physics
        // shapes read every step, so a cancelled sprout left in the graph would keep
        // colliding after its cell went Empty.
        Assert.Empty(terrain.Graph.Growing);
    }

    // ── The real scenario ──────────────────────────────────────────────────────────
    // Free gap is rows 2-3 (y 32..64 = 32 px). The player body is 24 px tall, so it
    // fits. A sprout requested at (2,2) grows DOWN out of the ceiling into the gap,
    // leaving only row 3 (16 px) — less than the body. The body is pinned against the
    // floor and cannot be pushed clear, so the sprout must be destroyed.
    [Fact]
    public void SproutGrowingIntoPinnedBody_IsDestroyed()
    {
        var terrain = SimTerrain.FromAscii(@"
            XXXXX
            XXXXX
            OOOOO
            OOOOO
            XXXXX");

        // Floor top = row 4 (Chunk.TileSize-scaled); start a few px above it and let
        // physics settle the rest of the way, same as the original fixed offset.
        float floorTopY = 4 * Chunk.TileSize;
        var body = PlayerBody(new Vector2(40f, floorTopY - 12f));
        var bodies = new List<PhysicsBody> { body };

        // Settle onto the floor first.
        for (int f = 0; f < 10; f++) PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
        Assert.Equal(0f, MaxOverlap(body, terrain));
        output.WriteLine($"settled at y={body.Position.Y:0.###}, overlap=0");

        var node = terrain.TryRequestTile(2, 2);
        Assert.NotNull(node);
        Assert.Equal(TileState.Sprouting, terrain.GetCellState(2, 2));

        int crushes = 0;
        for (int f = 0; f < 40; f++)
        {
            terrain.TickSprouts(Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            crushes += body.SproutCrushCount;
            if (body.SproutCrushCount > 0)
                output.WriteLine($"frame {f}: crushed {body.SproutCrushCount} sprout cell(s), " +
                                 $"state={terrain.GetCellState(2, 2)}, y={body.Position.Y:0.###}");
        }

        Assert.True(crushes > 0, "the sprout wedging the body should have been destroyed");
        // It must not have quietly finalized into a Solid tile on top of the body.
        Assert.NotEqual(TileState.Solid, terrain.GetCellState(2, 2));
        Assert.Equal(0f, MaxOverlap(body, terrain));
        output.WriteLine($"final: state={terrain.GetCellState(2, 2)}, " +
                         $"y={body.Position.Y:0.###}, overlap={MaxOverlap(body, terrain):0.###}");
    }

    // ── The exploit guard ──────────────────────────────────────────────────────────
    // A 1x1 pocket of COMMITTED tiles wedges the body just as hard (it is 24 px tall in
    // a 16 px pocket), but nothing may be destroyed — otherwise standing in the right
    // corner becomes a way to dig through permanent terrain.
    [Fact]
    public void StaticWedge_DoesNotBreakTerrain()
    {
        var terrain = SimTerrain.FromAscii(@"
            XXXXX
            XXXXX
            XX.XX
            XXXXX
            XXXXX");

        var body = PlayerBody(new Vector2(40f, 40f));
        var bodies = new List<PhysicsBody> { body };

        for (int f = 0; f < 30; f++) PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

        Assert.Equal(0, body.SproutCrushCount);
        foreach (var (gtx, gty) in new[] { (2, 1), (2, 3), (1, 2), (3, 2) })
            Assert.Equal(TileState.Solid, terrain.GetCellState(gtx, gty));
        output.WriteLine($"embedded by {MaxOverlap(body, terrain):0.###} px, terrain intact");
    }

    // A sprout the body is merely STANDING on (resolvable contact, no wedge) must
    // survive — the trigger is "the solver gave up", not "a sprout touched me".
    [Fact]
    public void SproutTouchingUnpinnedBody_Survives()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOO
            OOOOO
            OOOOO
            OOOOO
            XXXXX");

        // Body resting on the ground at y=64, plenty of open space above.
        var body = PlayerBody(new Vector2(40f, 52f));
        var bodies = new List<PhysicsBody> { body };
        for (int f = 0; f < 10; f++) PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);

        // Grow a block out of the ground right next to the body.
        var node = terrain.TryRequestTile(2, 3);
        Assert.NotNull(node);

        int crushes = 0;
        for (int f = 0; f < 40; f++)
        {
            terrain.TickSprouts(Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            crushes += body.SproutCrushCount;
        }

        Assert.Equal(0, crushes);
        Assert.Equal(TileState.Solid, terrain.GetCellState(2, 3));
        output.WriteLine($"sprout finalized normally, body pushed to y={body.Position.Y:0.###}");
    }
}
