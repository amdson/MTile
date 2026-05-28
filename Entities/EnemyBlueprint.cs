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
// Snapshot/restore: each blueprint owns its EntityKind. EntitySnapshot.Rehydrate
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
    // Entities/EntitySnapshot.cs.
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

        // Template — uncomment and adapt for your own types. Each new blueprint
        // wants its own EntityKind in EntitySnapshot.cs.
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
