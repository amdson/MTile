using System;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The block-charge gesture: hold RMB inside terrain to fill the avalanche meter the
// normal way, release, then double-click a block. A full meter buys one binary charge
// flag on that cell (ChunkMap.Charge, rendered as a white tint) and is spent entirely.
//
// These pin what the gesture's feel depends on: a double-click on a block with a full
// meter charges it and empties the meter, a double-click on a SHORT meter does nothing
// and keeps what's banked, and the far more common single-click-after-a-long-hold isn't
// mistaken for the second half of a double-click. The last two cover the paint lockout
// that ships with it — a banked charge keeps charging out of ground instead of being
// spent by an accidental paint stroke, but an empty meter still paints as before.
public class BlockChargeTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    // Cell (TargetGtx, TargetGty) is the floor row of FlatGround; derive its world coords
    // from Chunk.TileSize instead of pinning them to the old 16px grid.
    private const int TargetGtx = 3, TargetGty = 3;
    private static readonly float FloorTopY = TargetGty * Chunk.TileSize;
    private static readonly float TargetCellCenterX = TargetGtx * Chunk.TileSize + Chunk.TileSize / 2f;
    private static readonly float TargetCellCenterY = TargetGty * Chunk.TileSize + Chunk.TileSize / 2f;

    // Standing rest height above the floor: floatHeight (= R) + hexagon bottom extent (R·sin60°).
    private static readonly float RestOffset = PlayerCharacter.Radius * (1f + MathF.Sin(MathF.PI / 3f));

    private static readonly Vector2 Start = new(TargetCellCenterX - Chunk.TileSize, FloorTopY - RestOffset);

    // Buried in the floor row (gty 3), well inside a solid cell.
    private static readonly Vector2 InSolid = new(TargetCellCenterX, TargetCellCenterY);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain, int frames) => new SimConfigMulti
    {
        Terrain = terrain,
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = Start, Script = script } },
    };

    // Open air just above the floor, inside build reach — so a demoted stroke here would
    // visibly paint, which is what the lockout tests are measuring the absence of.
    private static readonly Vector2 InAir = new(TargetCellCenterX, TargetCellCenterY - Chunk.TileSize);

    private static PlayerInput Rmb(bool down) => new()
        { RightClick = down, MouseWorldPosition = InSolid };

    private static int SolidCount(ChunkMap c, int gtx0, int gtx1, int gty0, int gty1)
    {
        int n = 0;
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (c.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }

    // Long charging hold -> release -> click, gap, click. The final press is the one the
    // gesture fires on.
    private static InputScript ChargeThenDoubleClick(int holdFrames) => new InputScript()
        .For(6,          Rmb(false))
        .For(holdFrames, Rmb(true))    // charge the meter the normal way
        .For(6,          Rmb(false))
        .For(4,          Rmb(true))    // click 1
        .For(5,          Rmb(false))   // gap
        .Forever(        Rmb(true));   // click 2 -> charge the block

    [Fact]
    public void DoubleClickOnBlock_WithFullMeter_ChargesItAndSpendsTheMeter()
    {
        var terrain = FlatGround();
        Assert.Equal(TileState.Solid, terrain.GetCellState(TargetGtx, TargetGty));

        float meterAtEnd = 0f;
        float peakCharge = 0f;
        SimRunner.RunMulti(Build(ChargeThenDoubleClick(holdFrames: 130), terrain, frames: 155),
            onFrame: (f, ps) =>
            {
                peakCharge = MathF.Max(peakCharge, ps[0].Abilities.Meters.EruptMove);
                meterAtEnd = ps[0].Abilities.Meters.EruptMove;
            });

        output.WriteLine($"peak={peakCharge:F1} (gate {BuildMeters.BlockChargeMin:F1}), " +
                         $"end={meterAtEnd:F1}, charged={terrain.Charge.Count}");
        Assert.True(peakCharge >= BuildMeters.BlockChargeMin,
            $"The hold should have banked past the gate; got {peakCharge:F1}.");
        Assert.True(terrain.Charge.IsCharged(TargetGtx, TargetGty),
            "The double-clicked block should be charged.");
        // The spend is the whole meter. It refills immediately (the second click is still
        // a charging hold), so this is bounded rather than exactly zero.
        Assert.True(meterAtEnd < BuildMeters.BlockChargeMin,
            $"The charge should have been consumed; {meterAtEnd:F1} left.");
    }

    // The gate is the point of the mechanic: without a real commitment the gesture is
    // inert, and - critically - it doesn't quietly eat the small charge that IS banked.
    [Fact]
    public void DoubleClickOnBlock_WithShortMeter_DoesNothing()
    {
        var terrain = FlatGround();

        float meterAtEnd = 0f;
        SimRunner.RunMulti(Build(ChargeThenDoubleClick(holdFrames: 8), terrain, frames: 35),
            onFrame: (f, ps) => meterAtEnd = ps[0].Abilities.Meters.EruptMove);

        output.WriteLine($"end={meterAtEnd:F1}, charged={terrain.Charge.Count}");
        Assert.False(terrain.Charge.IsCharged(TargetGtx, TargetGty));
        Assert.Equal(0, terrain.Charge.Count);
        Assert.True(meterAtEnd > 0f, "A failed gesture must not swallow the banked charge.");
    }

    // The failure mode the hold-length budget exists for: charging is itself a long RMB
    // hold, so hold -> release -> ONE press has the same rising-edge shape as a double
    // click. It must not fire.
    [Fact]
    public void SingleClickAfterALongHold_DoesNotCharge()
    {
        var terrain = FlatGround();

        float peakCharge = 0f;
        SimRunner.RunMulti(Build(new InputScript()
                .For(6,   Rmb(false))
                .For(130, Rmb(true))     // the charging hold
                .For(5,   Rmb(false))
                .Forever( Rmb(true)),    // a single press, not a double click
            terrain, frames: 150),
            onFrame: (f, ps) => peakCharge = MathF.Max(peakCharge, ps[0].Abilities.Meters.EruptMove));

        output.WriteLine($"peak={peakCharge:F1}, charged={terrain.Charge.Count}");
        Assert.True(peakCharge >= BuildMeters.BlockChargeMin, "The hold should have filled the meter.");
        Assert.Equal(0, terrain.Charge.Count);
    }

    // Quality-of-life rule that ships with the gesture: once there's a charge worth
    // protecting, a held RMB keeps charging even out in open air instead of demoting into
    // a paint stroke — which would spend the charge (SpendForTiles falls through to
    // EruptMove) on tiles the player never asked for.
    [Fact]
    public void HeldRmbOutOfGround_KeepsChargingInsteadOfPainting()
    {
        var terrain = FlatGround();
        int before = SolidCount(terrain, 0, 8, 0, 3);

        float meterOnLeaving = 0f, meterAtEnd = 0f;
        SimRunner.RunMulti(Build(new InputScript()
                .For(6,  Rmb(false))
                .For(60, Rmb(true))                                             // charge, buried
                .Forever(new PlayerInput { RightClick = true, MouseWorldPosition = InAir }),
            terrain, frames: 126),
            onFrame: (f, ps) =>
            {
                var m = ps[0].Abilities.Meters;
                if (f == 66) meterOnLeaving = m.EruptMove;
                meterAtEnd = m.EruptMove;
            });

        int after = SolidCount(terrain, 0, 8, 0, 3);
        output.WriteLine($"meter {meterOnLeaving:F1} → {meterAtEnd:F1}, solid {before} → {after}");
        Assert.True(meterOnLeaving >= BuildMeters.PaintLockoutMin,
            $"Setup: the buried hold should have banked past the lockout; got {meterOnLeaving:F1}.");
        Assert.True(meterAtEnd > meterOnLeaving,
            $"The meter should keep charging out of ground; {meterOnLeaving:F1} → {meterAtEnd:F1}.");
        Assert.Equal(before, after);
    }

    // The other half of that rule: with an EMPTY meter the same hold still paints, so the
    // lockout gates the charged case only and doesn't quietly retire the painter.
    [Fact]
    public void HeldRmbOutOfGround_StillPaints_WhenNothingIsBanked()
    {
        var terrain = FlatGround();
        int before = SolidCount(terrain, 0, 8, 0, 3);

        SimRunner.RunMulti(Build(new InputScript()
                .For(6, new PlayerInput { MouseWorldPosition = InAir })
                .Forever(new PlayerInput { RightClick = true, MouseWorldPosition = InAir }),
            terrain, frames: 60));

        int after = SolidCount(terrain, 0, 8, 0, 3);
        output.WriteLine($"solid {before} → {after}");
        Assert.True(after > before, "An uncharged hold in open air must still paint.");
    }

    // ── Releasing the button clears the meter ───────────────────────────────────
    //
    // The lockout above is right for a LIVE hold and was wrong the moment the button
    // came up. A banked charge used to bleed at EruptDecay 60/s, so a full meter sat
    // above PaintLockoutMin for 3.7 seconds after release — nearly four seconds in which
    // the player could not place a block, most of it holding a charge already too small
    // to charge a block with. Release now empties the meter outright.

    [Fact]
    public void ReleasingTheButton_EmptiesTheMeterAtOnce()
    {
        var terrain = FlatGround();

        float atRelease = 0f, oneFrameLater = 0f;
        bool locksPaintAfter = true;
        SimRunner.RunMulti(Build(new InputScript()
                .For(6,   Rmb(false))
                .For(130, Rmb(true))     // fill it the normal way
                .Forever( Rmb(false)),   // and let go
            terrain, frames: 145),
            onFrame: (f, ps) =>
            {
                var m = ps[0].Abilities.Meters;
                if (f == 135) atRelease     = m.EruptMove;
                if (f == 138) oneFrameLater = m.EruptMove;
                if (f >= 138) locksPaintAfter &= m.ChargeLocksPaint;
            });

        output.WriteLine($"meter {atRelease:F1} -> {oneFrameLater:F1}");
        Assert.True(atRelease >= BuildMeters.BlockChargeMin,
            $"Setup: the hold should have filled the meter; got {atRelease:F1}.");
        Assert.Equal(0f, oneFrameLater);
        Assert.False(locksPaintAfter, "An emptied meter must not still be locking out paint.");
    }

    // The complaint this fixes, end to end: charge a full meter, let go, and the very
    // next stroke has to place blocks. Before the release-clear it painted nothing —
    // ChargeLocksPaint was still true off the banked 240, so the hold was read as another
    // charge — and stayed that way for about 3.7 seconds.
    [Fact]
    public void AfterAFullCharge_TheNextStrokePaintsImmediately()
    {
        var terrain = FlatGround();
        int before = SolidCount(terrain, 0, 8, 0, 3);

        SimRunner.RunMulti(Build(new InputScript()
                .For(6,   Rmb(false))
                .For(130, Rmb(true))                                            // charge, buried
                .For(6,   new PlayerInput { MouseWorldPosition = InAir })       // release
                .Forever(new PlayerInput { RightClick = true, MouseWorldPosition = InAir }),
            terrain, frames: 190));

        int after = SolidCount(terrain, 0, 8, 0, 3);
        output.WriteLine($"solid {before} -> {after}");
        Assert.True(after > before,
            "A stroke started right after releasing a full charge must paint.");
    }

    // The reserve that keeps the double-click payable is deliberately invisible to
    // everything else. If painting could reach it, emptying the meter on release would
    // just have moved the accidental-spend problem the lockout was built to solve.
    [Fact]
    public void ThePaintStrokeAfterARelease_CannotSpendTheReserve()
    {
        var terrain = FlatGround();

        float reserveAtEnd = -1f;
        SimRunner.RunMulti(Build(new InputScript()
                .For(6,   Rmb(false))
                .For(130, Rmb(true))
                .For(6,   new PlayerInput { MouseWorldPosition = InAir })
                .Forever(new PlayerInput { RightClick = true, MouseWorldPosition = InAir }),
            terrain, frames: 200),
            // Sampled while the reserve is still inside its grace window and the stroke
            // is actively painting: release lands on frame 136, the window is 1.0s (60
            // frames) so it runs to ~196, and painting starts on frame 142.
            onFrame: (f, ps) => { if (f == 170) reserveAtEnd = ps[0].Abilities.Meters.BankedCharge; });

        output.WriteLine($"reserve mid-stroke = {reserveAtEnd:F1}");
        Assert.True(reserveAtEnd >= BuildMeters.BlockChargeMin,
            $"Painting must not eat the reserve; {reserveAtEnd:F1} left.");
    }

    // And it does expire — the reserve is a window for one gesture, not a second meter
    // the player can sit on indefinitely.
    [Fact]
    public void TheReserve_ExpiresAfterItsWindow()
    {
        var terrain = FlatGround();

        float reserve = -1f;
        SimRunner.RunMulti(Build(new InputScript()
                .For(6,   Rmb(false))
                .For(130, Rmb(true))
                .Forever( Rmb(false)),
            terrain, frames: 260),
            onFrame: (f, ps) => reserve = ps[0].Abilities.Meters.BankedCharge);

        output.WriteLine($"reserve well past the window = {reserve:F1}");
        Assert.Equal(0f, reserve);
    }

    // Charge is sim state, not decoration: it has to survive a rollback like the rest of
    // the terrain, and it has to clear when the tile it's on is destroyed.
    [Fact]
    public void Charge_SnapshotsAndClearsOnBreak()
    {
        var terrain = FlatGround();
        terrain.Charge.Set(TargetGtx, TargetGty);

        var snap = terrain.CaptureTerrain();
        terrain.Charge.Set(TargetGtx + 1, TargetGty);
        terrain.RestoreTerrain(snap);

        Assert.True(terrain.Charge.IsCharged(TargetGtx, TargetGty));
        Assert.False(terrain.Charge.IsCharged(TargetGtx + 1, TargetGty));

        terrain.BreakCell(TargetGtx, TargetGty);
        Assert.False(terrain.Charge.IsCharged(TargetGtx, TargetGty));
    }
}
