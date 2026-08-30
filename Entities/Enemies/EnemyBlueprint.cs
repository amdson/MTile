using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Data-driven enemy authoring. EnemyBlueprint bundles every per-class parameter
// BruteEnemy hardcodes (body shape, mass, sprite, FSM state lists) into a value
// you can declare in one place and spawn via EnemyFactory.
//
// Drafting a new enemy is now: pick movement + action states from the existing
// pool, set the knobs, register under a fresh EntityKind. No new subclass.
//
// BruteEnemy is left as a hand-written reference — it documents the "by hand"
// path. The factory is the path for everything new.
//
// Snapshot/restore: each blueprint owns its EntityKind. EntityFactory.Rehydrate
// dispatches to EnemyFactory.Create for any kind whose blueprint is registered.
// Adding a new enemy type is therefore exactly two edits — (1) an EntityKind
// variant, (2) an EnemyFactory.Register call during startup — and nothing else
// needs to know.
public sealed class EnemyBlueprint
{
    // EntityKind this blueprint is registered under. Required because Rehydrate
    // dispatches on it — two blueprints sharing a Kind would clobber each
    // other in the registry.
    public required EntityKind Kind { get; init; }

    // ── Body / physics ──────────────────────────────────────────────────────
    public float Radius        { get; init; } = 12f;
    public int   Sides         { get; init; } = 6;
    public float Health        { get; init; } = 3f;
    public float Mass          { get; init; } = 1.2f;
    public float GravityScale  { get; init; } = 1f;
    public float FrictionScale { get; init; } = 0.12f;

    // ── Rendering ───────────────────────────────────────────────────────────
    public Color Color { get; init; } = new(150, 30, 30);
    // Sprite factory; receives Radius so the implementation can scale. Default
    // = Brute sprite so callers that don't care visually still see something.
    public Func<float, Sprite> Sprite { get; init; } = Sprites.Brute;

    // ── FSM composition ─────────────────────────────────────────────────────
    // Factories rather than lists so every spawned instance gets its own list
    // (EnemyEntity stores the list by reference). The state objects themselves
    // are flyweights with no instance state, so the factory may either `new`
    // them or hand back cached singletons — either is correct.
    //
    // Movement must contain at least one state, and the state at index 0 is
    // the FSM's fallback (EnemyEntity drops back to it when the current
    // state's CheckConditions fails). EnemyIdleState is the conventional pick.
    public required Func<List<EnemyMovementState>> Movement { get; init; }
    public required Func<List<EnemyActionState>>   Actions  { get; init; }

    // Swappable brain. Decides per-frame MoveX / Jump / AimWorld for the
    // entity; movement states consume that output rather than re-reading the
    // world. Defaults to ChasePlayerController so untouched blueprints behave
    // like BruteEnemy. Controllers must be stateless or config-only — see
    // EnemyController for details.
    public EnemyController Controller { get; init; } = EnemyController.Default;
}

// Concrete EnemyEntity that reads its config from a blueprint. Construct via
// EnemyFactory.Create rather than newing this directly — that keeps the spawn
// path symmetric with snapshot Rehydrate (which also goes through the factory).
public sealed class BlueprintEnemy : EnemyEntity
{
    private readonly EnemyBlueprint _bp;

    public override EntityKind Kind => _bp.Kind;

    public BlueprintEnemy(EnemyBlueprint blueprint, Vector2 pos)
        : base(new PhysicsBody(Polygon.CreateRegular(blueprint.Radius, blueprint.Sides), pos),
               blueprint.Health,
               blueprint.Movement(),
               blueprint.Actions(),
               blueprint.Controller)
    {
        _bp                = blueprint;
        Mass               = blueprint.Mass;
        GravityScale       = blueprint.GravityScale;
        Body.FrictionScale = blueprint.FrictionScale;
        Color              = blueprint.Color;
        Sprite             = blueprint.Sprite(blueprint.Radius);
    }
}

// Process-wide registry of enemy blueprints. Registration must run before any
// sim references a blueprint kind — built-ins register from the static ctor
// (eager on first member access), which guarantees ordering w.r.t. the sim
// since the sim can only touch this class through Create/IsRegistered.
//
// Determinism: every host (Desktop / Web / tests) must register the same
// blueprints under the same kinds. Built-ins below satisfy that; if you
// Register from gameplay code, do it deterministically (no input-driven
// registration, no random ordering) or per-host startup will diverge.
public static class EnemyFactory
{
    private static readonly Dictionary<EntityKind, EnemyBlueprint> _registry = new();

    static EnemyFactory() => RegisterBuiltIns();

    public static void Register(EnemyBlueprint blueprint)
        => _registry[blueprint.Kind] = blueprint;

    public static bool IsRegistered(EntityKind kind) => _registry.ContainsKey(kind);

    public static EnemyEntity Create(EntityKind kind, Vector2 pos)
    {
        if (!_registry.TryGetValue(kind, out var bp))
            throw new InvalidOperationException(
                $"No EnemyBlueprint registered for {kind}. Call EnemyFactory.Register first " +
                "or add the registration to EnemyFactory.RegisterBuiltIns.");
        return new BlueprintEnemy(bp, pos);
    }

    // Built-in registrations. Add new ones here (or call Register from your own
    // startup code). The EntityKind values referenced here must exist — see
    // Entities/EntityKind.cs.
    private static void RegisterBuiltIns()
    {
        // Skirmisher — light, fast, mid-range harasser. Demonstrates picking a
        // non-overlapping action subset (lunge + ranged, no melee) and a
        // smaller body. Spawn with: EnemyFactory.Create(EntityKind.Skirmisher, pos).
        Register(new EnemyBlueprint
        {
            Kind          = EntityKind.Skirmisher,
            Radius        = 10f,
            Health        = 2f,
            Mass          = 0.9f,
            FrictionScale = 0.10f,
            Color         = new Color(80, 140, 200),
            Sprite        = Sprites.Stalker,
            Movement = () => new()
            {
                new EnemyIdleState(),          // 0 — fallback
                new EnemyChaseState(),
                new EnemyAttackHoldState(),
                new EnemyStaggerState(),
            },
            Actions = () => new()
            {
                new EnemyLungeAction(),
                new EnemyRangedAction(),
            },
        });

        // ── Gauntlet trio ───────────────────────────────────────────────────
        // Three enemies that between them cover the three axes a traversal
        // encounter has: hold a line (Bastion), close from above (Pouncer), and
        // deny the walls and ceiling (Latcher). Each is expressed purely as a
        // blueprint — body knobs, a brain, and a pick from the movement/action
        // pools — with no subclass. See Levels/gauntlet.json for the stage they
        // were tuned against.

        // Bastion — rooted emplacement. Never moves; charges a 1.35s rail shot
        // that fires a bolt fast enough to be effectively undodgeable once
        // released, and which eats the cover the player is hiding behind.
        //
        // Mass 40 is the "rooted" mechanism: Entity.OnHit divides the knockback
        // impulse by mass, so at 40 even a heavy slash barely nudges it. No
        // stagger state is registered either, so hitting a Bastion never
        // interrupts a charge — the counterplay is to leave the line or to
        // close inside MinRange, not to trade hits at distance.
        Register(new EnemyBlueprint
        {
            Kind          = EntityKind.Bastion,
            Radius        = 14f,
            Sides         = 8,
            Health        = 7f,
            Mass          = 40f,
            FrictionScale = 0.9f,
            Color         = new Color(90, 95, 115),
            Sprite        = Sprites.Bastion,
            Controller    = new StationaryAimController { AlertRange = 540f },
            Movement = () => new()
            {
                // Idle only. A brain that never emits MoveDir makes every
                // locomotion state dead weight; leaving them out documents the
                // intent better than registering states that can't fire.
                new EnemyIdleState(),          // 0 — fallback (and the whole kit)
            },
            Actions = () => new()
            {
                new EnemyRailShotAction(),
            },
        });

        // Pouncer — surface-to-surface hopper. EnemyHopState solves its own
        // ballistic arc toward the brain's aim point, so it climbs terrain in
        // discrete bounds; EnemyPounceSlamAction turns the descent into a
        // hitbox whose damage and knockback scale with fall speed.
        //
        // Deliberately NO EnemyAttackHoldState: at priority 40 it would preempt
        // the hop and brake the body, destroying the very momentum the slam
        // measures. GravityScale 1.25 sharpens the arcs so the drop reads as a
        // commitment rather than a float.
        Register(new EnemyBlueprint
        {
            Kind          = EntityKind.Pouncer,
            Radius        = 11f,
            Sides         = 3,
            Health        = 4f,
            Mass          = 1.1f,
            GravityScale  = 1.25f,
            FrictionScale = 0.14f,
            Color         = new Color(200, 130, 40),
            Sprite        = Sprites.Pouncer,
            // EngageRange 0 ⇒ always emit an aim vector, which EnemyHopState
            // consumes as its landing target. A chase brain would be wrong here:
            // the hop state wants a point in 2D, not a left/right sign.
            Controller    = new MoveTowardPlayerController { EngageRange = 0f },
            Movement = () => new()
            {
                new EnemyIdleState(),          // 0 — fallback (recovery between bounds)
                new EnemyHopState(),
                new EnemyStaggerState(),
            },
            Actions = () => new()
            {
                new EnemyPounceSlamAction(),
            },
        });

        // Latcher — wall/ceiling crawler. EnemyClingMoveState zeroes gravity and
        // walks the body along whichever direction keeps it anchored to solid
        // tiles, so it tracks the player around overhangs and up shafts;
        // EnemyLashAction strikes along a frozen 2D axis, which is what makes an
        // attack from an inverted position land where the telegraph pointed.
        //
        // Also no EnemyAttackHoldState — same reason as the Pouncer but a
        // different failure: AttackHold would preempt the cling, and cling's
        // Exit restores gravity, so the Latcher would drop off the ceiling the
        // instant it started a swing. Cling handles the planting itself.
        // EnemyStaggerState IS registered, and does peel it off the wall on a
        // hit — that's the intended reward for connecting.
        Register(new EnemyBlueprint
        {
            Kind          = EntityKind.Latcher,
            Radius        = 10f,
            Health        = 5f,
            Mass          = 1.6f,
            FrictionScale = 0.10f,
            Color         = new Color(60, 150, 130),
            Sprite        = Sprites.Latcher,
            Controller    = new MoveTowardPlayerController { EngageRange = 40f },
            Movement = () => new()
            {
                new EnemyIdleState(),          // 0 — fallback (falls if it loses the surface)
                new EnemyClingMoveState(),
                new EnemyStaggerState(),
            },
            Actions = () => new()
            {
                new EnemyLashAction(),
            },
        });

        // Bird — flying hazard. Patrols left and right and hurts on contact; it
        // has no awareness of the player at all beyond the touch itself, which is
        // the point: it is terrain that moves, and the player routes around it.
        //
        // GravityScale 0 is what makes it a flier rather than something falling
        // slowly. EnemyFlyState can hold altitude against gravity on its own
        // acceleration budget, but only with margin at Mass ~1 — zeroing gravity
        // instead spends that whole budget on the patrol, so the flight line stays
        // dead level and cheap. Mass 0.8 keeps it swattable: a player slash still
        // flings it, which is the counterplay.
        //
        // No EnemyStaggerState, deliberately. Stagger sets Committed, which drops
        // EnemyFlyState (its preconditions are !IsActionCommitted) — a staggered
        // bird would stop flying, and with GravityScale 0 it would hang motionless
        // in the air instead of reacting. Knockback alone reads the hit.
        Register(new EnemyBlueprint
        {
            Kind          = EntityKind.Bird,
            Radius        = 9f,
            Sides         = 5,
            Health        = 2f,
            Mass          = 0.8f,
            GravityScale  = 0f,
            FrictionScale = 0.05f,
            Color         = new Color(70, 80, 110),
            Sprite        = Sprites.Bird,
            Controller    = new PatrolController { LegSeconds = 2.4f },
            Movement = () => new()
            {
                new EnemyIdleState(),          // 0 — fallback
                new EnemyFlyState(),
            },
            Actions = () => new()
            {
                new EnemyContactAction(),
            },
        });

        // Shrike — the bird that hunts. Same silhouette as the Bird above and the
        // opposite intent: it patrols until the player is close, hovers for a beat,
        // then dives and detonates on whatever it reaches. Everything about it
        // lives in Entities/Enemies/Types/ShrikeEnemy.cs.
        Register(ShrikeEnemy.Blueprint);

        // Zeus — the statue on the hill. Everything about it lives in
        // Entities/Enemies/Types/ZeusEnemy.cs; this is the line that makes it
        // spawnable and snapshot-restorable.
        Register(ZeusEnemy.Blueprint);

        // Template — the documented copy-and-edit starting point. Body, brain,
        // and both state lists live in Entities/Enemies/Types/TemplateEnemy.cs; this is the
        // one line that makes it spawnable and snapshot-restorable.
        Register(TemplateEnemy.Blueprint);

        // Older inline template — each new blueprint wants its own EntityKind
        // in EntityKind.cs.
        //
        // Register(new EnemyBlueprint
        // {
        //     Kind   = EntityKind.Bombardier,
        //     Health = 4f,
        //     Color  = Color.DarkOliveGreen,
        //     Movement = () => new()
        //     {
        //         new EnemyIdleState(),
        //         new EnemyChaseState(),
        //         new EnemyAttackHoldState(),
        //         new EnemyJumpState(),
        //         new EnemyStaggerState(),
        //     },
        //     Actions = () => new()
        //     {
        //         new EnemyShockwaveAction(),
        //         new EnemyRangedAction(),
        //     },
        // });
    }
}
