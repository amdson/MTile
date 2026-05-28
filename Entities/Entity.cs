using Microsoft.Xna.Framework;

namespace MTile;

// Generic, hittable, configurable non-player entity. One class covers balloons,
// balls, and future combat targets — behavior is parametrized via Mass, GravityScale,
// Health, Color rather than via subclasses. Subclasses come back when something
// genuinely diverges (an enemy with AI, a projectile that fires hitboxes).
//
// IHittable contract:
//   * Faction filters self-damage at the CombatSystem level.
//   * PublishHurtboxes broadcasts a single body-bounds hurtbox each frame.
//   * OnHit applies damage and knockback; CombatSystem already deduped per HitId.
public class Entity : IHittable
{
    public PhysicsBody Body;
    public float Health;
    public float MaxHealth;
    // Higher mass = less knockback (target.Velocity += impulse / Mass). Mass ≤ 0
    // is treated as immovable (no knockback applied).
    public float Mass         = 1f;
    // 1 = full gravity, 0 = none (floating), 0.5 = half. PreStep adds a counter-force
    // to the global gravity so we don't have to touch PhysicsWorld.
    public float GravityScale = 1f;
    public Color Color        = Color.White;
    public Faction Faction { get; set; } = Faction.Neutral;
    // Optional visual. When null, Game1 falls back to drawing the body polygon outline.
    public Sprite Sprite;

    // Stable identity for snapshot/restore (roadmap goal 4 §G). Assigned once by
    // Simulation when the entity is spawned, from a deterministic counter, so the
    // same entity carries the same id across a snapshot/restore round-trip — which
    // is what lets the combat dedupe table be snapshotted by id (see CombatSystem).
    public EntityId Id { get; set; }

    public bool IsDead => Health <= 0f;

    // Concrete-type tag for rehydration (see EntityFactory.Rehydrate). The base
    // Entity (balloons/balls) reports Generic; each polymorphic subtype overrides.
    public virtual EntityKind Kind => EntityKind.Generic;

    public Entity(PhysicsBody body, float health)
    {
        Body      = body;
        Health    = health;
        MaxHealth = health;
    }

    public void PublishHurtboxes(HurtboxWorld world)
        => world.Publish(new Hurtbox(Body.Bounds, Faction, Id));

    public virtual void OnHit(in Hitbox hit, in Hurtbox _)
    {
        Health -= hit.Damage;
        if (Mass > 0f) Body.Velocity += hit.KnockbackImpulse / Mass;
    }

    // Called before PhysicsWorld.StepSwept. Cancels (or amplifies) the global
    // gravity by adding an opposing force scaled by (GravityScale - 1). With
    // GravityScale = 1 this is a no-op; with 0, the body is weightless.
    public void PreStep(Vector2 globalGravity)
    {
        if (GravityScale == 1f) return;
        Body.AppliedForce += globalGravity * (GravityScale - 1f);
    }

    // Per-frame AI / scripted-behavior hook. Default is no-op — passive entities
    // (balloons, balls) ignore it. Active entities (enemies) override to drive
    // their physics body and publish offensive hitboxes. Called by Game1 between
    // hurtbox publication and CombatSystem.Apply so hitboxes published here
    // resolve the same frame.
    //
    // `spawner` lets an entity emit new entities mid-update (e.g. a turret firing
    // a bullet). Spawned entities are added to the game's lists after the loop
    // finishes, so they don't trip the in-flight foreach.
    public virtual void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner) { }

    // Sync any sprite state that's NOT a 1:1 mirror of Body.Position — Game1 sets
    // Position uniformly; orientation, animation phase, or tinting are owned here.
    // Default no-op for entities whose sprite is purely positional (balls, balloons).
    public virtual void SyncSprite()
    {
        if (Sprite != null) Sprite.Position = Body.Position;
    }

    // ── Snapshot/restore (Plans/ECS_MIGRATION_PLAN.md, Phases 4-6) ───────────────
    // Serializable state lives in the World's value-component stores (EntityData +
    // BodyStateComp) keyed by this entity's Id. The live object stays the authority
    // during a Step; these methods sync it to/from the components only at snapshot
    // boundaries. The component-set IS the snapshot — no separate per-entity struct
    // array. Symmetric with WriteState/ReadState, which marshal the subtype fields.
    public void CaptureState(World world)
    {
        ref var d = ref world.Get<EntityData>(Id);
        d.Kind         = Kind;
        d.Health       = Health;
        d.MaxHealth    = MaxHealth;
        d.Mass         = Mass;
        d.GravityScale = GravityScale;
        d.Color        = Color;
        d.Faction      = Faction;
        d.Polygon      = Body.Polygon;   // immutable shape
        d.Impact       = Body.Impact;    // immutable config
        WriteState(ref d);
        world.Get<BodyStateComp>(Id).State = BodyState.Capture(Body);
    }

    public void RestoreState(World world)
    {
        var d = world.Get<EntityData>(Id);
        Health       = d.Health;
        MaxHealth    = d.MaxHealth;
        Mass         = d.Mass;
        GravityScale = d.GravityScale;
        Color        = d.Color;
        Faction      = d.Faction;
        ReadState(in d);
        world.Get<BodyStateComp>(Id).State.RestoreInto(Body);
    }

    // Subtype hooks for the per-type fields (AI state, projectile fuses, …). Base
    // entities (balloons/balls) carry none, so the defaults are no-ops.
    protected virtual void WriteState(ref EntityData s) { }
    protected virtual void ReadState(in EntityData s) { }
}

// Callback handed to entity Update so AI can spawn child entities (projectiles,
// summons) without touching Game1's internal lists directly.
public interface IEntitySpawner
{
    void SpawnEntity(Entity e);
    // Shared, deterministic HitId source so AI / projectiles mint ids from the same
    // sequence as player attacks (see HitIdAllocator).
    HitIdAllocator HitIds { get; }
    // World handle for AI states that need tile queries (e.g. surface-anchored
    // movement). Read-only sampling only — don't mutate from inside a state, so
    // sampling order doesn't matter for determinism.
    ChunkMap Chunks { get; }
}
