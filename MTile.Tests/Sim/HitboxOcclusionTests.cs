using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;

namespace MTile.Tests;

// Terrain occlusion for hitboxes (TileReach + CombatSystem). A hitbox published
// with an Origin only reaches cells and hurtboxes it has line of sight to, and
// its tile damage propagates nearest-first from that origin — so a hit strong
// enough to break the front cell reaches the one behind it in the same pass,
// while a weak one only chips the exposed face.
//
// Grid: TileSize = 16. Ascii rows are gty 0.., columns gtx 0..; 'X' = Stone
// (MaxHP 2.0), '.' = air.
public class HitboxOcclusionTests
{
    private const float T = Chunk.TileSize;
    private const float StoneHP = 2.0f * TileDamage.TileMaxHP;

    private sealed class Dummy : IHittable
    {
        public Faction Faction => Faction.Enemy;
        public EntityId Id { get; }
        public BoundingBox Region;
        public int Hits;
        public Dummy(int id, BoundingBox region) { Id = new EntityId(id); Region = region; }
        public void PublishHurtboxes(HurtboxWorld world) => world.Publish(new Hurtbox(Region, Faction, Id));
        public Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox) { Hits++; return hit.KnockbackImpulse; }
    }

    private static Hitbox Box(BoundingBox region, float damage, Vector2? origin, int hitId = 1)
        => new(region, hitId, damage, new Vector2(100f, 0f), Faction.Player1, EntityId.None,
               origin: origin);

    private static BoundingBox Cells(int gtx0, int gty0, int gtx1, int gty1)
        => new(gtx0 * T, gty0 * T, (gtx1 + 1) * T, (gty1 + 1) * T);

    private static Vector2 CellCenter(int gtx, int gty) => new((gtx + 0.5f) * T, (gty + 0.5f) * T);

    private static void Apply(ChunkMap chunks, Hitbox hit, params Dummy[] targets)
    {
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();
        var byId = new Dictionary<EntityId, IHittable>();
        foreach (var d in targets) { d.PublishHurtboxes(hurtboxes); byId[d.Id] = d; }
        hitboxes.Publish(hit);
        combat.Apply(chunks, hitboxes, hurtboxes, id => byId.TryGetValue(id, out var h) ? h : null);
    }

    // ── Entities ───────────────────────────────────────────────────────────────

    // Attacker at gtx 0, a one-cell wall at gtx 1, target at gtx 2. The hitbox
    // spans all three cells; with an origin the wall blocks the hit, without one
    // it lands (legacy behaviour preserved for hitboxes that don't opt in).
    [Fact]
    public void Entity_BehindWall_NotHit_WhenOccluded()
    {
        var chunks = SimTerrain.FromAscii(".X.");
        var target = new Dummy(7, Cells(2, 0, 2, 0));
        Apply(chunks, Box(Cells(0, 0, 2, 0), 0f, origin: CellCenter(0, 0)), target);
        Assert.Equal(0, target.Hits);

        var legacy = new Dummy(8, Cells(2, 0, 2, 0));
        Apply(chunks, Box(Cells(0, 0, 2, 0), 0f, origin: null), legacy);
        Assert.Equal(1, legacy.Hits);
    }

    [Fact]
    public void Entity_NoWall_Hit_WhenOccluded()
    {
        var chunks = SimTerrain.FromAscii("...");
        var target = new Dummy(7, Cells(2, 0, 2, 0));
        Apply(chunks, Box(Cells(0, 0, 2, 0), 0f, origin: CellCenter(0, 0)), target);
        Assert.Equal(1, target.Hits);
    }

    // A target whose center is behind cover but whose lower end pokes out is
    // still hittable: the hurtbox is sampled at its corners, not just its
    // center. Wall at (1,0); origin at (0,0); the target body spans rows 0-2 at
    // gtx 2. Its center (row 1) and nearest point (row 0) are shadowed by the
    // wall, but the sightline over the wall's bottom-left corner reaches its
    // bottom third.
    [Fact]
    public void Entity_PeekingAroundCover_Hit()
    {
        var chunks = SimTerrain.FromAscii(
            ".X.\n" +
            "...\n" +
            "...");
        var target = new Dummy(7, Cells(2, 0, 2, 2));
        Apply(chunks, Box(Cells(0, 0, 2, 2), 0f, origin: CellCenter(0, 0)), target);
        Assert.Equal(1, target.Hits);

        // Same cover, but a target only as tall as the wall is fully hidden.
        var hidden = new Dummy(8, Cells(2, 0, 2, 0));
        Apply(chunks, Box(Cells(0, 0, 2, 0), 0f, origin: CellCenter(0, 0)), hidden);
        Assert.Equal(0, hidden.Hits);
    }

    // The tile pass runs before the entity pass: a hit that breaks the wall this
    // frame reaches the target behind it this same frame.
    [Fact]
    public void Entity_BehindWall_Hit_WhenSameHitBreaksWall()
    {
        var chunks = SimTerrain.FromAscii(".X.");
        var target = new Dummy(7, Cells(2, 0, 2, 0));
        Apply(chunks, Box(Cells(0, 0, 2, 0), StoneHP, origin: CellCenter(0, 0)), target);
        Assert.Equal(TileState.Empty, chunks.GetCellState(1, 0));
        Assert.Equal(1, target.Hits);
    }

    // ── Tiles ──────────────────────────────────────────────────────────────────

    // "X.X" from the left: the second block sits behind an air gap. A weak hit
    // (below Stone HP) chips only the front block — it can't reach across the
    // gap through the first one.
    [Fact]
    public void Tiles_AirGap_WeakHit_OnlyFrontCellDamaged()
    {
        var chunks = SimTerrain.FromAscii("X.X");
        var origin = new Vector2(-0.5f * T, 0.5f * T);   // one cell left of the row
        Apply(chunks, Box(Cells(0, 0, 2, 0), 0.5f, origin));
        Assert.Equal(0.5f, chunks.Damage.Get(0, 0), 3);
        Assert.Equal(0f,   chunks.Damage.Get(2, 0), 3);
    }

    // Same layout, a hit strong enough to break Stone: the front block breaks,
    // which exposes the block across the gap, which breaks too — in ONE pass.
    [Fact]
    public void Tiles_AirGap_StrongHit_PunchesThrough()
    {
        var chunks = SimTerrain.FromAscii("X.X");
        var origin = new Vector2(-0.5f * T, 0.5f * T);
        Apply(chunks, Box(Cells(0, 0, 2, 0), StoneHP, origin));
        Assert.Equal(TileState.Empty, chunks.GetCellState(0, 0));
        Assert.Equal(TileState.Empty, chunks.GetCellState(2, 0));
    }

    // Cumulative multi-frame damage propagates one layer per break: half-HP hits
    // on "XX" need two frames to break the front cell; the back cell starts
    // taking damage on the frame the front breaks, and breaks the frame after.
    [Fact]
    public void Tiles_TwoDeep_HalfHits_BreakFrontThenBack()
    {
        var chunks = SimTerrain.FromAscii("XX");
        var origin = new Vector2(-0.5f * T, 0.5f * T);
        var hit = Box(Cells(0, 0, 1, 0), StoneHP * 0.5f, origin);

        Apply(chunks, hit);
        Assert.Equal(TileState.Solid, chunks.GetCellState(0, 0));
        Assert.Equal(0f, chunks.Damage.Get(1, 0), 3);

        Apply(chunks, hit);
        Assert.Equal(TileState.Empty, chunks.GetCellState(0, 0));
        Assert.Equal(TileState.Solid, chunks.GetCellState(1, 0));
        Assert.Equal(StoneHP * 0.5f, chunks.Damage.Get(1, 0), 3);

        Apply(chunks, hit);
        Assert.Equal(TileState.Empty, chunks.GetCellState(1, 0));
    }

    // Without an origin, every overlapped cell takes damage regardless of cover —
    // the legacy behaviour BeamAction still relies on.
    [Fact]
    public void Tiles_NoOrigin_DamagesHiddenCells()
    {
        var chunks = SimTerrain.FromAscii("XX");
        Apply(chunks, Box(Cells(0, 0, 1, 0), 0.5f, origin: null));
        Assert.Equal(0.5f, chunks.Damage.Get(0, 0), 3);
        Assert.Equal(0.5f, chunks.Damage.Get(1, 0), 3);
    }

    // Grazing angles: from a point above a floor, every cell of the floor's top
    // surface is reachable — the segment to a far cell's top edge runs along the
    // neighbours' faces without entering them.
    [Fact]
    public void Tiles_FloorFromAbove_WholeTopSurfaceReachable()
    {
        var chunks = SimTerrain.FromAscii("XXXXXX");
        var origin = new Vector2(0.5f * T, -0.75f * T);   // above the leftmost cell
        Apply(chunks, Box(Cells(0, 0, 5, 0), 0.5f, origin));
        for (int gtx = 0; gtx < 6; gtx++)
            Assert.Equal(0.5f, chunks.Damage.Get(gtx, 0), 3);
    }

    // ...but the row underneath is not: it's covered by the row on top.
    [Fact]
    public void Tiles_FloorFromAbove_SecondRowShielded()
    {
        var chunks = SimTerrain.FromAscii(
            "XXX\n" +
            "XXX");
        var origin = new Vector2(1.5f * T, -0.75f * T);
        Apply(chunks, Box(Cells(0, 0, 2, 1), 0.5f, origin));
        for (int gtx = 0; gtx < 3; gtx++)
        {
            Assert.Equal(0.5f, chunks.Damage.Get(gtx, 0), 3);
            Assert.Equal(0f,   chunks.Damage.Get(gtx, 1), 3);
        }
    }

    // A one-cell hole in the top row exposes the cell under it (and only it).
    [Fact]
    public void Tiles_HoleInFloor_ExposesCellBeneath()
    {
        var chunks = SimTerrain.FromAscii(
            "X.X\n" +
            "XXX");
        var origin = new Vector2(1.5f * T, -0.75f * T);
        Apply(chunks, Box(Cells(0, 0, 2, 1), 0.5f, origin));
        Assert.Equal(0.5f, chunks.Damage.Get(1, 1), 3);
        Assert.Equal(0f,   chunks.Damage.Get(0, 1), 3);
        Assert.Equal(0f,   chunks.Damage.Get(2, 1), 3);
    }

    // ── TileReach direct area damage ───────────────────────────────────────────

    // A blast in the middle of a cavity damages every wall face it can see and
    // nothing behind them.
    [Fact]
    public void DamageDisc_DamagesExposedRing_NotBehind()
    {
        var chunks = SimTerrain.FromAscii(
            "XXXXX\n" +
            "XXXXX\n" +
            "XX.XX\n" +
            "XXXXX\n" +
            "XXXXX");
        var origin = CellCenter(2, 2);
        int broken = TileReach.DamageDisc(chunks, origin, radius: 1.5f * T, damage: 0.5f);
        Assert.Equal(0, broken);
        // The four cells whose faces line the cavity take the blast.
        Assert.Equal(0.5f, chunks.Damage.Get(1, 2), 3);
        Assert.Equal(0.5f, chunks.Damage.Get(3, 2), 3);
        Assert.Equal(0.5f, chunks.Damage.Get(2, 1), 3);
        Assert.Equal(0.5f, chunks.Damage.Get(2, 3), 3);
        // Diagonals only touch the cavity at a corner — no exposed face, buried.
        Assert.Equal(0f, chunks.Damage.Get(1, 1), 3);
        Assert.Equal(0f, chunks.Damage.Get(3, 3), 3);
        // Two cells out, shadowed by the ring.
        Assert.Equal(0f, chunks.Damage.Get(0, 2), 3);
        Assert.Equal(0f, chunks.Damage.Get(2, 0), 3);
    }

    [Fact]
    public void DamageDisc_StrongBlast_CascadesOutward()
    {
        var chunks = SimTerrain.FromAscii(
            "XXXXX\n" +
            "XX.XX\n" +
            "XXXXX");
        var origin = CellCenter(2, 1);
        int broken = TileReach.DamageDisc(chunks, origin, radius: 2.5f * T, damage: StoneHP);
        Assert.Equal(TileState.Empty, chunks.GetCellState(1, 1));
        Assert.Equal(TileState.Empty, chunks.GetCellState(0, 1));   // reached once (1,1) broke
        Assert.Equal(TileState.Empty, chunks.GetCellState(4, 1));
        Assert.True(broken >= 8);
    }
}
