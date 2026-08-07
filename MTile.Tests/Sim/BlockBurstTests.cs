using System;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// BlockBurstAction — hold RMB with the cursor over dead air, click LMB, get a
// plus-shaped puff of foam. The block-side twin of BurstAction.
//
// The interesting part is the gating, not the placement: the LMB press-edge is
// contested by ReadyAction (the wind-up, Passive 15) and the RMB gesture is contested
// by the drag-build and the eruption. These tests pin which one wins.
//
// RunMulti with a single player rather than Run — it's the overload with an onFrame
// hook, and the action name only lives on PlayerCharacter (SimFrame carries the
// MOVEMENT state).
public class BlockBurstTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    // Solid floor at tile row 3 (y = 48..64); open air above it.
    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 Start = new(40f, 36f);

    // Dead air: ~100px right and a tile up. Past BuildReach (64px) so the drag-build
    // can't paint there, inside BurstReach (128px) so the puff can.
    private static readonly Vector2 DeadAir = new(140f, 20f);

    // A cell the drag-build CAN paint: sitting on the floor, well within 64px.
    private static readonly Vector2 Paintable = new(72f, 40f);

    // Inside the floor.
    private static readonly Vector2 InSolid = new(56f, 56f);

    // Close in (~26px, well inside BuildReach) but floating: tile row 1, which touches
    // no solid — row 2 is what sits against the floor. The drag-build can't place here
    // (nothing to sprout from), so the burst SHOULD take it.
    private static readonly Vector2 CloseFloatingAir = new(60f, 20f);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain) => new SimConfigMulti
    {
        Terrain = terrain,
        Frames  = 40,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = Start, Script = script } },
    };

    // RMB down for a few frames (so the FSM settles into BlockReadyAction — the burst
    // requires the gesture to already be in its paint phase), then LMB press.
    private static InputScript RmbThenLmb(Vector2 cursor) => new InputScript()
        .For(10, new PlayerInput { MouseWorldPosition = cursor })
        .For(5,  new PlayerInput { RightClick = true, MouseWorldPosition = cursor })
        .Forever(new PlayerInput { RightClick = true, LeftClick = true, MouseWorldPosition = cursor });

    private static (int gtx, int gty) Cell(Vector2 p) =>
        ((int)MathF.Floor(p.X / Chunk.TileSize), (int)MathF.Floor(p.Y / Chunk.TileSize));

    private static bool FiredFor(Vector2 cursor, ITestOutputHelper output, out ChunkMap terrain,
                                out bool sawReady)
    {
        terrain = FlatGround();
        bool burst = false, ready = false;
        var t = terrain;
        SimRunner.RunMulti(Build(RmbThenLmb(cursor), t),
            onFrame: (f, ps) =>
            {
                if (ps[0].CurrentActionName == "BlockBurstAction") burst = true;
                if (ps[0].CurrentActionName == "ReadyAction")      ready = true;
            });
        output.WriteLine($"cursor {cursor}: burst={burst}, ready={ready}");
        sawReady = ready;
        return burst;
    }

    [Fact]
    public void RmbHeldOverDeadAir_ThenLmb_FiresBlockBurst()
        => Assert.True(FiredFor(DeadAir, output, out _, out _),
            "LMB while RMB holds over dead air should fire BlockBurstAction.");

    [Fact]
    public void BlockBurst_PlacesFoam_InThePlusOfCells()
    {
        Assert.True(FiredFor(DeadAir, output, out var terrain, out _));

        var (gtx, gty) = Cell(DeadAir);
        Assert.Equal(TileState.Solid, terrain.GetCellState(gtx, gty));
        Assert.Equal(TileType.Foam,   terrain.GetCellType(gtx, gty));

        int arms = 0;
        foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            if (terrain.GetCellState(gtx + dx, gty + dy) != TileState.Empty) arms++;
        output.WriteLine($"center solid foam, arms present = {arms}/4");
        Assert.Equal(4, arms);
    }

    // The press must NOT be stolen when RMB is actually painting: a placeable cell in
    // build reach belongs to the drag-build, so LMB falls through to the wind-up.
    [Fact]
    public void CursorWhereDragBuildCanPaint_DoesNotFireBlockBurst()
    {
        bool burst = FiredFor(Paintable, output, out _, out bool sawReady);
        Assert.False(burst, "A paintable cell in build reach belongs to the drag-build.");
        Assert.True(sawReady, "The LMB press should fall through to the normal wind-up instead.");
    }

    // Distance is NOT the gate — placeability is. A cell inside build reach but with no
    // solid or sprout 4-neighbour is one TryRequestTile would reject, so RMB is placing
    // nothing there and the burst takes the press.
    [Fact]
    public void CursorInReachButNotAdjacentToTerrain_FiresBlockBurst()
    {
        Assert.True(FiredFor(CloseFloatingAir, output, out var terrain, out _),
            "Unsupported air is unpaintable at any range — the burst should fire.");
        var (gtx, gty) = Cell(CloseFloatingAir);
        Assert.Equal(TileState.Solid, terrain.GetCellState(gtx, gty));
        Assert.Equal(TileType.Foam,   terrain.GetCellType(gtx, gty));
    }

    // Cursor inside solid terrain is BlockReady's charge / the eruption gesture.
    [Fact]
    public void CursorInSolid_DoesNotFireBlockBurst()
        => Assert.False(FiredFor(InSolid, output, out _, out _),
            "Cursor in solid belongs to BlockReady's charge, not the burst.");
}
