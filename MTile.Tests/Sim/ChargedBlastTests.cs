using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The third sink for a charged block, alongside the two in ChargedBlockUseTests (peel
// it into a demolition clod; feed it to an eruption). Destroy it where it stands —
// stab it, slash it, crush it, catch it in another blast — and it detonates: a short
// fuse, then a crater and anyone next to it thrown clear. See Entities/ChargedBlast.cs.
//
// Every test here breaks the cell through ChunkMap.BreakCell directly rather than
// driving a real attack. That is deliberate: BreakCell is the choke point every
// destruction path funnels through, so this pins the RULE — "a charged cell that goes
// Empty goes off" — instead of one attack's carve pattern layered on top of it.
public class ChargedBlastTests(ITestOutputHelper output)
{
    private static Vector2 CellCenter(int gtx, int gty) => new(
        gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
        gty * Chunk.TileSize + Chunk.TileSize * 0.5f);

    private static int SolidCount(ChunkMap c, int gtx0, int gtx1, int gty0, int gty1)
    {
        int n = 0;
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (c.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }

    // A solid slab under open air, with the player parked far to the left doing
    // nothing — so the only thing that changes terrain in these runs is the blast.
    private static ChunkMap Slab()
    {
        var rows = new List<string>();
        for (int y = 0; y < 3; y++) rows.Add(new string('O', 32));
        for (int y = 3; y < 10; y++) rows.Add(new string('X', 32));
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private static Simulation Settled(ChunkMap terrain, Vector2 playerAt, int frames = 5)
    {
        var sim = new Simulation(terrain, playerAt);
        for (int f = 0; f < frames; f++) sim.Step(new PlayerInput());
        return sim;
    }

    // Break one cell of the slab and let the sim run well past the fuse. Returns the
    // change in solid tiles around the site.
    private int BreakDelta(bool charged, int gtx = 19, int gty = 5)
    {
        var terrain = Slab();
        var sim = Settled(terrain, CellCenter(2, 2));

        if (charged) terrain.Charge.Set(gtx, gty);
        int before = SolidCount(terrain, gtx - 5, gtx + 5, gty - 5, gty + 5);
        terrain.BreakCell(gtx, gty);

        for (int f = 0; f < 60; f++) sim.Step(new PlayerInput());

        int after = SolidCount(terrain, gtx - 5, gtx + 5, gty - 5, gty + 5);
        output.WriteLine($"charged={charged}: solid {before} -> {after}");
        return after - before;
    }

    // The headline, with its own control: destroying a charged block takes its
    // neighbours with it, where destroying a plain one removes exactly the cell you
    // broke and nothing else.
    [Fact]
    public void DestroyingAChargedBlock_TakesItsNeighboursWithIt()
    {
        int plain   = BreakDelta(charged: false);
        int charged = BreakDelta(charged: true);

        output.WriteLine($"plain delta={plain}, charged delta={charged}");
        Assert.Equal(-1, plain);
        Assert.True(charged < -1, $"a charged block should crater, not just vanish (delta {charged})");
    }

    // The blast is a real hitbox, not just a terrain edit: a body standing next to the
    // block takes the hit. Faction.Neutral is what makes it reach a player at all —
    // CombatSystem's only ownership rule is "hitbox faction != hurtbox faction", so a
    // Neutral blast hits everyone, including whoever charged the block.
    [Fact]
    public void TheBlast_HitsABodyStandingNextToIt()
    {
        var terrain = Slab();
        var sim = Settled(terrain, CellCenter(18, 2), frames: 10);   // standing on the slab

        float before = sim.Player.Combat.DamagePercent;
        terrain.Charge.Set(19, 3);
        terrain.BreakCell(19, 3);
        for (int f = 0; f < 30; f++) sim.Step(new PlayerInput());

        float after = sim.Player.Combat.DamagePercent;
        output.WriteLine($"damage {before} -> {after}");
        Assert.True(after > before, "a body next to a detonating charged block should be hit");

        // And it lands ONCE, at a heavy-but-not-absurd weight. The band is wide, but it
        // is the guard that matters: the crater channel runs on tile HP (tens) while
        // this one runs on percent contribution (~15× the number), so a blast that
        // accidentally shares a constant between them — or double-dips because the
        // shared HitId stopped deduping the core against the ring — shows up here as a
        // hit several times heavier than any melee move in the game.
        float dealt = after - before;
        Assert.InRange(dealt, 10f, 35f);
    }

    // A body well outside the blast radius is untouched — the control for the test
    // above, and the thing that would catch a blast that quietly reached the whole map.
    [Fact]
    public void TheBlast_LeavesADistantBodyAlone()
    {
        var terrain = Slab();
        var sim = Settled(terrain, CellCenter(2, 2), frames: 10);

        float before = sim.Player.Combat.DamagePercent;
        terrain.Charge.Set(19, 3);
        terrain.BreakCell(19, 3);
        for (int f = 0; f < 30; f++) sim.Step(new PlayerInput());

        Assert.Equal(before, sim.Player.Combat.DamagePercent);
    }

    // The fuse is the whole reason this is an entity rather than hitboxes published at
    // the break site: detonating on the breaking frame would make a chain of these one
    // indivisible flash, with no time for anyone to get out of it.
    [Fact]
    public void TheBlast_WaitsAFuseBeforeGoingOff()
    {
        var terrain = Slab();
        var sim = Settled(terrain, CellCenter(2, 2));

        terrain.Charge.Set(19, 5);
        terrain.BreakCell(19, 5);
        int atBreak = SolidCount(terrain, 14, 24, 0, 10);

        sim.Step(new PlayerInput());                 // the blast is armed, not fired
        int oneFrameLater = SolidCount(terrain, 14, 24, 0, 10);

        for (int f = 0; f < 30; f++) sim.Step(new PlayerInput());
        int settled = SolidCount(terrain, 14, 24, 0, 10);

        output.WriteLine($"break={atBreak} +1f={oneFrameLater} settled={settled}");
        Assert.Equal(atBreak, oneFrameLater);
        Assert.True(settled < atBreak, "the blast should land once the fuse runs out");
    }

    // A blast that breaks another charged cell arms that one in turn, so a charged wall
    // goes off as a cascade that travels. The termination argument is BreakCell clearing
    // the flag — no cell can arm twice — and this run would eat the whole slab if that
    // ever stopped holding.
    [Fact]
    public void ChargedBlocks_ChainOffEachOther()
    {
        var terrain = Slab();
        var sim = Settled(terrain, CellCenter(2, 2));

        // Two charges within a blast radius of each other along the same row.
        terrain.Charge.Set(17, 5);
        terrain.Charge.Set(19, 5);
        int before = SolidCount(terrain, 8, 28, 0, 10);
        terrain.BreakCell(17, 5);

        for (int f = 0; f < 90; f++) sim.Step(new PlayerInput());

        int after = SolidCount(terrain, 8, 28, 0, 10);
        bool secondStillCharged = terrain.Charge.IsCharged(19, 5);
        output.WriteLine($"solid {before} -> {after}, second still charged={secondStillCharged}");
        Assert.False(secondStillCharged, "the first blast should have set off the second");
        Assert.Equal(0, terrain.Charge.Count);
        // Strictly more damage than one blast alone — the chain actually compounded.
        Assert.True(before - after > -BreakDelta(charged: true),
                    $"a chain should out-crater a single blast (removed {before - after})");
    }

    // The other half of the rule: DISCHARGING a block is not destroying it. An eruption
    // recruits nearby charges through Charge.Clear precisely so the wall the player
    // spent four seconds charging survives being built with — arming a blast on that
    // path would have a building verb eat its own material.
    [Fact]
    public void DischargingABlock_DoesNotArmABlast()
    {
        var terrain = Slab();
        var sim = Settled(terrain, CellCenter(2, 2));

        terrain.Charge.Set(19, 5);
        int before = SolidCount(terrain, 14, 24, 0, 10);
        terrain.Charge.Clear(19, 5);

        for (int f = 0; f < 60; f++) sim.Step(new PlayerInput());

        Assert.Equal(before, SolidCount(terrain, 14, 24, 0, 10));
        Assert.Equal(TileState.Solid, terrain.GetCellState(19, 5));
    }

    // The queue ChunkMap hands Simulation is drained inside the same Step that fills it,
    // which is what keeps it off the snapshot. If it ever survived a frame boundary it
    // would be un-rolled-back sim state, and a rollback would replay the blast twice.
    [Fact]
    public void TheBreakQueue_NeverSurvivesAFrameBoundary()
    {
        var terrain = Slab();
        var sim = Settled(terrain, CellCenter(2, 2));

        terrain.Charge.Set(19, 5);
        terrain.BreakCell(19, 5);
        Assert.Single(terrain.ChargedBreaks);        // armed, waiting for the drain

        sim.Step(new PlayerInput());
        Assert.Empty(terrain.ChargedBreaks);
    }
}
