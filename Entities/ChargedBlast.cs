using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The payout for destroying a charged block. A cell the player spent a full avalanche
// meter on doesn't just vanish when something breaks it — it detonates, cratering the
// terrain around it and throwing bodies clear.
//
// This is the third sink for a charged block, alongside the two from the demolition
// clod / supercharged eruption pass. The three now read as one rule rather than three
// special cases: a charged block always cashes out, and WHAT it buys depends on how it
// leaves the world. Peeled into a clod it becomes a bigger burst on impact; recruited
// by an eruption it becomes build mass (BlockEruptionHelpers.RecruitChargedBlocks
// discharges without breaking, which is why that path deliberately never lands here);
// simply destroyed — stabbed, slashed, crushed, caught in another blast — it goes off
// where it stood. Charging a wall is now a decision with a downside, which is what
// makes it interesting: the block is a weapon either player can set off.
//
// Why an entity rather than publishing the hitboxes straight from the break site: the
// fuse. Detonating on the breaking frame would make a chain of charged blocks resolve
// inside a handful of milliseconds — one indivisible flash, and no time for anyone to
// get out of it. A short fuse turns the same chain into a visible cascade that travels,
// and gives the player who lit it a beat to be somewhere else. Being an entity is also
// what makes the fuse rollback-safe for free: it rides the ordinary entity snapshot
// instead of needing a pending-blast table of its own.
public class ChargedBlast : Entity
{
    // Beat between "the block broke" and "the block goes off" — long enough to read as
    // a separate event, short enough that it still feels caused by the hit that broke
    // it. Six frames.
    private const float FuseSeconds     = 0.10f;
    // Blast reach. Small on purpose: this is a demolition charge, not the clod's
    // area-denial burst (StickyGrenade sits at 3.5 tiles, a charged clod further
    // still). Two and a half tiles takes out the block's immediate neighbours and
    // little else, so charging a wall shapes a hole rather than clearing a room.
    private const float RadiusTiles     = 2.5f;
    private const float Radius          = RadiusTiles * Chunk.TileSize;
    private const int   Segments        = 12;

    // ── Crater channel ──────────────────────────────────────────────────────────
    // Tile damage at the epicentre, falling off linearly to CraterRimDamage at the rim.
    // BOTH sit above the toughest material shipped today — Stone, MaxHP 12 in
    // material_strengths.json (the comment there still says 2.0; it is stale) — so the
    // blast clears its whole radius whatever it is dug into. That is the deliberate
    // choice: a demolition charge whose hole changes shape with the rock is a charge
    // the player cannot aim, and the cost is already paid in the meter, not in the
    // material.
    //
    // The falloff is therefore not doing a material-spread job right now, and pinning
    // it against Stone would be knife-edge tuning either way (at radius 2.5 the
    // outermost cells sit at 2.24 tiles, so a rim value near 12 flips the crater
    // between 9 cells and 21 on a rounding error). What it buys is graceful
    // degradation: if a material tougher than the rim value ever lands, the crater
    // shrinks toward the centre instead of the blast silently doing nothing, and cells
    // that survive keep their accumulated damage for the next hit.
    private const float CraterCoreDamage = 30f;
    private const float CraterRimDamage  = 14f;

    // ── Body channel ────────────────────────────────────────────────────────────
    // A percent contribution, scaled by CombatState.PercentPerDamage (15) — so this is
    // ~22%, about three light slashes, landed once. Heavy, as a whole avalanche meter
    // should be, but well short of the ~45% that the raw tile-damage numbers above
    // would have delivered if the two channels shared a constant. They read as one
    // explosion and are on completely different scales; that is why they are separate.
    private const float BlastDamage     = 1.5f;
    private const float BlastKnockback  = 640f;
    // The core is a separate box because the ring can't cover its own centre: the
    // segments sit ON the ring, so without this the epicentre — the cell that actually
    // held the charge — is the one place the blast doesn't reach.
    private const float CoreHalfSize    = 0.9f * Chunk.TileSize;
    // Straight up (y-down). A body standing on the charged block is the common case,
    // and the core's job there is to pop them off it; the ring handles anyone stood to
    // one side, with a direction that actually points away from the blast.
    private static readonly Vector2 CoreKnockDir = new(0f, -1f);

    private readonly int _hitId;
    private bool _detonated;

    public override EntityKind Kind => EntityKind.ChargedBlast;

    // World-space reach, for the render shell's telegraph/particle sizing and for tests.
    public static float BlastRadius => Radius;
    public static float Fuse        => FuseSeconds;

    // Ticks up to the fuse. Stored rather than derived from a spawn frame so it
    // snapshots as a plain value like every other entity fuse.
    private float _age;

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId    = _hitId;
        s.Age      = _age;
        s.Exploded = _detonated;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _age       = s.Age;
        _detonated = s.Exploded;
    }

    public ChargedBlast(Vector2 pos, int hitId)
        : base(new PhysicsBody(Polygon.CreateRegular(3f, 6), pos), health: 0.1f)
    {
        _hitId = hitId;
        // It sits exactly where the block was and does nothing physical: no gravity to
        // drop it out of a mid-air crater, no tile collision to shove it out of the
        // cell it is supposed to detonate in.
        Body.IgnoreTiles = true;
        Body.Velocity    = Vector2.Zero;
        GravityScale     = 0f;
        Mass             = 0f;
        // Neutral is load-bearing, not a default: CombatSystem's only ownership rule is
        // "hitbox faction != hurtbox faction", so Neutral is the one faction that
        // reaches both players. A charged block belongs to the terrain by the time it
        // goes off — it hurts whoever is standing next to it, including the player who
        // charged it.
        Faction          = Faction.Neutral;
        Color            = new Color(255, 240, 200);
        Sprite           = Sprites.Ball(3f);
    }

    // Not a target. A blast is a moment, not an object: giving it a hurtbox would let
    // it soak a hit, take knockback out of its own cell, and get shoved around by force
    // fields on the way to going off.
    public override void PublishHurtboxes(HurtboxWorld world) { }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        _age += dt;
        if (_age < FuseSeconds || _detonated) return;
        Detonate(hitboxes, spawner);
        // One frame of hitbox, then gone. The registry is cleared every Step, so living
        // longer would just mean re-publishing — and the shared HitId means a victim
        // takes the blast once no matter how many boxes reach them.
        Health = 0f;
    }

    // Two channels, deliberately separate — the ForceBurst pattern. They want opposite
    // things from the same explosion, and trying to serve both from one set of hitboxes
    // is what a first cut of this got wrong.
    //
    // TERRAIN is carved directly, in a circle, ignoring line of sight. A charged block
    // is normally BURIED, so the hitbox path can't do this job: hitboxes published with
    // an origin are occluded by terrain, and a blast that starts inside a wall is
    // occluded from itself — the ring lands two tiles out, with solid rock in between,
    // and carves nothing at all. Sight is also the wrong model for a demolition charge:
    // it is in contact with the rock, and blowing a hole in what surrounds it is the
    // entire point. A hitbox AABB would also give a square crater.
    //
    // BODIES go through the ordinary hitbox path WITH occlusion, because for them
    // sight is exactly right — nobody should be blasted through a wall. Carving first
    // is what makes the pair coherent: the crater is already open when CombatSystem
    // resolves the hitboxes later this frame, so the blast reaches through the hole it
    // just made, and only through that hole.
    private void Detonate(HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        _detonated = true;
        var center = Body.Position;

        // IEntitySpawner.Chunks is the sanctioned read-only handle; null only for the
        // bare spawner stubs some tests pass, which have no world to crater.
        Crater(spawner?.Chunks, center);

        if (hitboxes != null)
        {
            // Core: the epicentre, for a body standing on the block that just went off.
            hitboxes.Publish(new Hitbox(
                new BoundingBox(center.X - CoreHalfSize, center.Y - CoreHalfSize,
                                center.X + CoreHalfSize, center.Y + CoreHalfSize),
                _hitId, BlastDamage, CoreKnockDir * BlastKnockback,
                Faction, Id, Color.LightGoldenrodYellow,
                targets: HitTargets.EntitiesOnly, origin: center));

            // Ring: one box per segment, each shoving outward along its own radius, so
            // bodies on opposite sides are thrown apart instead of sharing one vector.
            // Half-size is DERIVED from the spacing (2πR/N) rather than tuned, because
            // a hand-picked constant is exactly how a blast ends up with gaps between
            // its segments for a body to stand in.
            float segHalf = MathF.Max(0.5f * Chunk.TileSize, MathF.PI * Radius / Segments);
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * MathHelper.TwoPi / Segments;
                var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var at  = center + dir * Radius;
                hitboxes.Publish(new Hitbox(
                    new BoundingBox(at.X - segHalf, at.Y - segHalf,
                                    at.X + segHalf, at.Y + segHalf),
                    _hitId, BlastDamage, dir * BlastKnockback,
                    Faction, Id, Color.LightGoldenrodYellow,
                    targets: HitTargets.EntitiesOnly, origin: center));
            }
        }
        // Presentation only (particles / audio), keyed by entity id so the render shell
        // dedupes it across rollback replays.
        spawner?.NotifyChargedBlast(Id, Body.Position, Radius);
    }

    // Damage every cell whose centre falls inside the blast circle, hardest at the
    // epicentre. DamageCell rather than BreakCell so the crater goes through the
    // ordinary material path — accumulated damage compared against the cell's own MaxHP
    // — instead of deleting terrain outright. With the numbers above that clears the
    // full disc today; going through DamageCell is what keeps that a consequence of the
    // tuning rather than a hardcoded hole, and what leaves partial damage behind on
    // anything that does survive.
    //
    // Iteration is row-major over a fixed cell range, which is what makes it
    // deterministic — a rollback replay damages the same cells in the same order, and
    // a cell that breaks can cascade support to its neighbours identically.
    private static void Crater(ChunkMap chunks, Vector2 center)
    {
        if (chunks == null) return;
        int gtx0 = (int)MathF.Floor((center.X - Radius) / Chunk.TileSize);
        int gtx1 = (int)MathF.Floor((center.X + Radius) / Chunk.TileSize);
        int gty0 = (int)MathF.Floor((center.Y - Radius) / Chunk.TileSize);
        int gty1 = (int)MathF.Floor((center.Y + Radius) / Chunk.TileSize);
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
        {
            var cell = new Vector2(gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
                                   gty * Chunk.TileSize + Chunk.TileSize * 0.5f);
            float dist = (cell - center).Length();
            if (dist > Radius) continue;          // circle, not the bounding square
            float t = dist / Radius;
            chunks.DamageCell(gtx, gty, MathHelper.Lerp(CraterCoreDamage, CraterRimDamage, t));
        }
    }
}
