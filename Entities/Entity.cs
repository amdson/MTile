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

    // Nailed to the spot. Not "heavy", not "weightless" — a rooted body does not move,
    // and the two halves of that have to travel together, which is why this is a
    // property rather than a field anyone can set half of:
    //
    //   * PreStep zeroes Velocity and cancels gravity every frame. It runs immediately
    //     before PhysicsWorld.StepSwept, downstream of knockback, force fields and the
    //     entity's own Update, so it catches every writer without any of them knowing.
    //   * IgnoreTiles takes the depenetration solver out of the loop, which is the one
    //     mover PreStep cannot get in front of — it runs INSIDE StepSwept. Without it a
    //     player who builds a block into a rooted body still shoves it out of the wall.
    //
    // Position is therefore constant from spawn. That is what keeps this off the
    // snapshot: there is no anchor to remember, because a body that is never integrated
    // has nothing to drift from. Mass is a separate question and still worth setting —
    // it is what keeps the hit-feel stamp (LastHitDir/LastHitImpulse) proportionate.
    public bool Rooted
    {
        get => _rooted;
        set
        {
            _rooted = value;
            if (value) Body.IgnoreTiles = true;
        }
    }
    private bool _rooted;
    public Color Color        = Color.White;
    public Faction Faction { get; set; } = Faction.Neutral;
    // Optional visual. When null, Game1 falls back to drawing the body polygon outline.
    public Sprite Sprite;

    // Stable identity for snapshot/restore (roadmap goal 4 §G). Assigned once by
    // Simulation when the entity is spawned, from a deterministic counter, so the
    // same entity carries the same id across a snapshot/restore round-trip — which
    // is what lets the combat dedupe table be snapshotted by id (see CombatSystem).
    public EntityId Id { get; set; }

    // Render-only hit-feel stamp (Plans/HIT_FEEL_PLAN.md) — same "stamp advances,
    // HitFeelSystem edge-detects" contract as CombatState.LastHit* on PlayerCharacter,
    // just keyed on a per-hit counter instead of a frame number since a bare Entity
    // has no frame clock of its own. Set by OnHit below; snapshotted in
    // CaptureState/RestoreState so a rollback restore can't hand HitFeelSystem a
    // stale direction/impulse for a hit that's about to be replayed differently.
    // HitGeneration doubles as the white-flash stamp (Drawing/HitFlash.cs).
    public int     HitGeneration;
    public float   LastHitImpulse;
    public Vector2 LastHitDir;

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

    // Virtual so non-hittable helpers (a block-grab pulling point) can opt out — a
    // hurtbox is also what force fields act on, so "no hurtbox" means "not a target".
    public virtual void PublishHurtboxes(HurtboxWorld world)
        => world.Publish(new Hurtbox(Body.Bounds, Faction, Id));

    public virtual Vector2 OnHit(in Hitbox hit, in Hurtbox _)
    {
        Health -= hit.Damage;
        var res = HitResolver.Resolve(in hit, Mass, Body.Velocity);
        Body.Velocity += res.TargetDeltaV;

        LastHitDir = res.TargetDeltaV.LengthSquared() > 1e-4f
            ? Vector2.Normalize(res.TargetDeltaV)
            : hit.StrikeDir;
        LastHitImpulse = res.Strength;
        HitGeneration++;

        return res.Impulse;
    }

    // Called before PhysicsWorld.StepSwept. Cancels (or amplifies) the global
    // gravity by adding an opposing force scaled by (GravityScale - 1). With
    // GravityScale = 1 this is a no-op; with 0, the body is weightless.
    public void PreStep(Vector2 globalGravity)
    {
        if (_rooted)
        {
            // Discard whatever the frame accumulated and hand StepSwept a body whose net
            // acceleration is exactly zero: it does `Velocity += AppliedForce * dt` then
            // `Velocity += gravity * dt`, so cancelling gravity here leaves the velocity
            // it integrates at 0 and the position unchanged. Assigning rather than adding
            // is the point — a rooted body owes nothing to the forces pushing on it.
            Body.Velocity     = Vector2.Zero;
            Body.AppliedForce = -globalGravity;
            return;
        }
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
    // Virtual for entities that carry a sparse component beyond EntityData (a peel
    // group): they override, call base, and marshal their own store by Id.
    public virtual void CaptureState(World world)
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
        d.HitGeneration  = HitGeneration;
        d.LastHitImpulse = LastHitImpulse;
        d.LastHitDir     = LastHitDir;
        WriteState(ref d);
        world.Get<BodyStateComp>(Id).State = BodyState.Capture(Body);
    }

    public virtual void RestoreState(World world)
    {
        var d = world.Get<EntityData>(Id);
        Health       = d.Health;
        MaxHealth    = d.MaxHealth;
        Mass         = d.Mass;
        GravityScale = d.GravityScale;
        Color        = d.Color;
        Faction      = d.Faction;
        HitGeneration  = d.HitGeneration;
        LastHitImpulse = d.LastHitImpulse;
        LastHitDir     = d.LastHitDir;
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
    // Look up a live entity by id. Actions that own a helper entity (BlockGrabAction's
    // pulling point) keep the EntityId in their ActionVars — never the object, which a
    // rollback restore replaces — and resolve it every frame. Null once the entity has
    // died and been swept (end of the Step it died in). Default null so spawner stubs
    // that never mint entities need no change.
    Entity Resolve(EntityId id) => null;
    // This frame's published hurtboxes. Entities are updated AFTER every hurtbox has
    // been published, so an entity that must react to touching a body — a thrown clod
    // bursting on the player it hits rather than sailing through to land — can query
    // the set directly instead of waiting a frame for CombatSystem to dispatch. Read-only
    // sampling; publishing from inside an entity update would be order-dependent.
    // Default null so spawner stubs that never need it are unaffected.
    HurtboxWorld Hurtboxes => null;
    // A mass ball landed and erupted: a cosmetic event for the render shell (particle
    // splash, audio). Default no-op — only Simulation forwards it, as OnMassLanded.
    void NotifyMassLanded(EntityId id, Vector2 pos, TileType type, int blocks) { }
    // A destroyed charged block detonated: the same cosmetic channel as above, sized by
    // the blast's world-space radius so the render shell doesn't have to know the
    // entity's constants. Default no-op — only Simulation forwards it, as OnChargedBlast.
    void NotifyChargedBlast(EntityId id, Vector2 pos, float radius) { }
}

// Entities that show something beyond their Sprite in the world-space overlay pass
// (enemy telegraphs, the block-grab tether tint) implement ITelegraphSource
// (Presentation/TelegraphList.cs) and append shapes to the frame's TelegraphList.
