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

    // Warlord — mobile duelist boss: cleave combo, ram, leap-slam, and a foam cage
    // it leaps into. Subclass; see Entities/Enemies/Types/WarlordEnemy.cs.
    Warlord,

    // Prism — player-sized boss that moves only by dashing and plants ray emitters.
    // Subclass; see Entities/Enemies/Types/PrismEnemy.cs.
    Prism,
    RayEmitter,     // a planted cone-ray node (Entities/RayEmitter.cs)

    // Quillback — small fast armored hedgehog boss: shell over a tiny HP bar, close-range
    // flurry/spin/roll, digs and builds, a punishable quill storm at range.
    // Subclass; see Entities/Enemies/Types/QuillbackEnemy.cs.
    Quillback,

    // Spearman — minion-tier walker: a long telegraphed stab along a tracked-then-locked
    // line, a spear that regrows after every thrust (open until it does), and a snap
    // parry that stabs projectiles out of the air. Subclass; see
    // Entities/Enemies/Types/SpearmanEnemy.cs.
    Spearman,

    // Pulser — slow, tough surface-crawler (gravity off while touching terrain) with
    // one move: a long-charged, terrain-occluded pulse. Subclass; see
    // Entities/Enemies/Types/PulserEnemy.cs.
    Pulser,

    // Spawner — destructible nest that keeps up to N of one enemy kind alive near it.
    // See Entities/EnemySpawner.cs.
    Spawner,

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
    // 8..47: the fighter gauntlet gives each of its fighters its own kind, so a restore
    // rehydrates the right one from Simulation.Blueprints.
    FighterSlot8,  FighterSlot9,  FighterSlot10, FighterSlot11, FighterSlot12, FighterSlot13,
    FighterSlot14, FighterSlot15, FighterSlot16, FighterSlot17, FighterSlot18, FighterSlot19,
    FighterSlot20, FighterSlot21, FighterSlot22, FighterSlot23, FighterSlot24, FighterSlot25,
    FighterSlot26, FighterSlot27, FighterSlot28, FighterSlot29, FighterSlot30, FighterSlot31,
    FighterSlot32, FighterSlot33, FighterSlot34, FighterSlot35, FighterSlot36, FighterSlot37,
    FighterSlot38, FighterSlot39, FighterSlot40, FighterSlot41, FighterSlot42, FighterSlot43,
    FighterSlot44, FighterSlot45, FighterSlot46, FighterSlot47,

    // A fighter's mine (ActionKind.Mine, Entities/Projectiles/FighterMine.cs).
    FighterMine,
}

public static class EntityKinds
{
    // Every kind a FighterSpec can compile to. Rehydrate routes these to EnemyFactory
    // unconditionally so an unregistered one fails loudly.
    public static bool IsFighter(EntityKind k) => k >= EntityKind.Brick && k <= EntityKind.FighterSlot47;

    public const int FighterSlotCount = 48;

    public static EntityKind FighterSlot(int i)
    {
        if (i < 0 || i >= FighterSlotCount) throw new ArgumentOutOfRangeException(nameof(i), "Fighter slots are 0..47.");
        return EntityKind.FighterSlot0 + i;
    }
}
