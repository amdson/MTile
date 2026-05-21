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
    public int Id;
    public int HittableId => Id;

    public bool IsDead => Health <= 0f;

    // Concrete-type tag for rehydration (see EntitySnapshot.Rehydrate). The base
    // Entity (balloons/balls) reports Generic; each polymorphic subtype overrides.
    public virtual EntityKind Kind => EntityKind.Generic;

    public Entity(PhysicsBody body, float health)
    {
        Body      = body;
        Health    = health;
        MaxHealth = health;
    }

    public void PublishHurtboxes(HurtboxWorld world)
        => world.Publish(new Hurtbox(Body.Bounds, Faction, this));

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

    // ── Snapshot/restore (roadmap goal 4 §G) ────────────────────────────────────
    // Capture the entity's full mutable state into a flat EntitySnapshot. The base
    // fills the shared fields (id, body, stats, immutable shape/impact refs); each
    // subtype adds its own via WriteState. Symmetric with RestoreInto/ReadState.
    public EntitySnapshot Capture()
    {
        var s = new EntitySnapshot
        {
            Kind         = Kind,
            Id           = Id,
            Body         = BodyState.Capture(Body),
            Health       = Health,
            MaxHealth    = MaxHealth,
            Mass         = Mass,
            GravityScale = GravityScale,
            Color        = Color,
            Faction      = Faction,
            Polygon      = Body.Polygon,   // immutable shape
            Impact       = Body.Impact,    // immutable config
        };
        WriteState(ref s);
        return s;
    }

    public void RestoreInto(in EntitySnapshot s)
    {
        Id           = s.Id;
        s.Body.RestoreInto(Body);
        Health       = s.Health;
        MaxHealth    = s.MaxHealth;
        Mass         = s.Mass;
        GravityScale = s.GravityScale;
        Color        = s.Color;
        Faction      = s.Faction;
        ReadState(in s);
    }

    // Subtype hooks for the per-type fields (AI state, projectile fuses, …). Base
    // entities (balloons/balls) carry none, so the defaults are no-ops.
    protected virtual void WriteState(ref EntitySnapshot s) { }
    protected virtual void ReadState(in EntitySnapshot s) { }
}

// Callback handed to entity Update so AI can spawn child entities (projectiles,
// summons) without touching Game1's internal lists directly.
public interface IEntitySpawner
{
    void SpawnEntity(Entity e);
    // Shared, deterministic HitId source so AI / projectiles mint ids from the same
    // sequence as player attacks (see HitIdAllocator).
    HitIdAllocator HitIds { get; }
}
