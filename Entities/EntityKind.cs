using System;

namespace MTile;

// Tag identifying an entity's concrete type for snapshot rehydration. The sim is
// polymorphic over Entity, so a restore that must *recreate* a despawned entity
// (one alive at the snapshot frame but gone now) needs to know which class to
// construct. Stored on each entity's EntityData component; consumed by
// EntityFactory.Rehydrate. (Restoring into a still-live entity uses the virtual
// RestoreState and doesn't consult this.)
public enum EntityKind
{
    Generic,        // balloons / balls (the base Entity class, parametrized by ctor)
    Stalker,
    Turret,
    Bullet,
    EnergyBall,
    StickyGrenade,
    LobbedArea,
    MassBall,       // eruption payload — coasting ball of build mass (see MassBall.cs)
    PullPoint,      // block-grab pulling point: owns the peel group + carried orb (see PullPointEntity.cs)
    Brute,          // MVP EnemyEntity subtype (see Plans/ENEMY_CAPABILITY_FRAMEWORK.md)
    PracticeBall,   // juggling target — breaks on tile contact, respawns at its spawn point
    ChargedBlast,   // a destroyed charged block going off — short fuse, then a crater (see ChargedBlast.cs)

    // Factory-built enemy variants. Each blueprint registered with
    // EnemyFactory owns its own EntityKind so Rehydrate can dispatch to it.
    // Add new variants here as you draft new enemy types — names are the
    // single source of truth for snapshot identity across hosts and replays.
    Skirmisher,
    Bombardier,

    // Gauntlet trio (Levels/gauntlet.json, Stages "gauntlet"). Each is a
    // registered EnemyBlueprint — see EnemyFactory.RegisterBuiltIns.
    Bastion,        // rooted emplacement; charges a terrain-shredding rail shot
    Pouncer,        // hops surface to surface; damage scales with its fall speed
    Latcher,        // crawls walls/ceilings; telegraphed medium-range lash

    RailBolt,       // Bastion ordnance — fast, tile-breaking (see RailBoltProjectile)

    // Zeus — rooted statue boss on the "hill" stage. Three laser attacks
    // (heavy bolt / strike storm / raking sweep); see ZeusEnemy.cs.
    Zeus,

    // Bird — flying contact-damage hazard that patrols left and right. Pure
    // blueprint (no subclass); see EnemyFactory.RegisterBuiltIns.
    Bird,

    // Shrike — the bird's hunting cousin. Patrols like a Bird until the player
    // comes near, then hovers, dives, and detonates on whatever it reaches.
    // Pure blueprint; see Entities/Enemies/Types/ShrikeEnemy.cs.
    Shrike,

    // Copy-and-edit starting point for a new enemy — Entities/Enemies/Types/TemplateEnemy.cs.
    // Spawn it on the "sandbox" stage.
    Template,

    // Aspid — Primal Aspid homage: slow flier that holds range above the player
    // and fires fans of three slow fireballs. See Entities/Enemies/Types/AspidEnemy.cs.
    Aspid,
    AspidFireball,  // Aspid ordnance (AspidFireballProjectile)

    // Sparring — plain melee enemy for the "weights" stage: waits in place until the
    // player comes near (ProximityChaseController). The stage overrides Mass per slot.
    Sparring,

    // Warden — armored player-sized walker, open only while it swings its one heavy
    // smash. Subclass (custom body + armor); see Entities/Enemies/Types/WardenEnemy.cs.
    Warden,

    // Wizard — fragile caster: waves of slow terrain-passing orbs + raised dirt
    // pillars. See Entities/Enemies/Types/WizardEnemy.cs.
    Wizard,
    WizardOrb,      // Wizard ordnance (WizardOrbProjectile)

    // ── Fighters (Plans/FIGHTER_DESIGN_PLAN.md) ─────────────────────────────
    // Compiled from FighterSpecs by FighterCompiler and registered with EnemyFactory.
    // KEEP THIS BLOCK CONTIGUOUS AND LAST: EntityKinds.IsFighter is a range check.
    //
    // The six stock archetypes (Entities/Enemies/Fighters/FighterRoster.cs), registered
    // at startup. The turret archetype is FighterTurret because `Turret` is the older
    // TurretEnemy's kind.
    Brick,
    Sprinter,
    Gunner,
    Flyer,
    Builder,
    FighterTurret,

    // Scratch slots for the arena / forge / tests: re-registrable at will, because
    // EnemyFactory.Register overwrites and both spawn and rehydrate go through the
    // registry. Nothing is registered here at startup — rehydrating an unregistered
    // slot throws rather than quietly making a generic entity.
    FighterSlot0,
    FighterSlot1,
    FighterSlot2,
    FighterSlot3,
    FighterSlot4,
    FighterSlot5,
    FighterSlot6,
    FighterSlot7,
}

public static class EntityKinds
{
    // Every kind a FighterSpec can compile to. Rehydrate routes these to EnemyFactory
    // unconditionally so an unregistered one fails loudly.
    public static bool IsFighter(EntityKind k) => k >= EntityKind.Brick && k <= EntityKind.FighterSlot7;

    public const int FighterSlotCount = 8;

    public static EntityKind FighterSlot(int i)
    {
        if (i < 0 || i >= FighterSlotCount) throw new ArgumentOutOfRangeException(nameof(i), "Fighter slots are 0..7.");
        return EntityKind.FighterSlot0 + i;
    }
}
