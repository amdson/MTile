using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The six hand-authored archetypes of Plans/FIGHTER_DESIGN_PLAN.md §8.2 — each one
// exists to prove one tradeoff in the cost model, and together they are the first
// fighter content (registered as blueprints, spawned on the "fighters" stage) and
// phase 5's balance roster.
//
// Each factory returns a FRESH spec, so a test or the forge can mutate one freely.
// Masses quoted are under the default FighterCostConfig; see FighterCosts.cs for the
// derivation and FighterCostTests for the gate that each compiles under budget.
public static class FighterRoster
{
    // Brick — health, armor, strength, one heavy swing. Proves mass makes you slow: at
    // ≈2.44 it sits at the budget's mass cap and its legs barely clear a_min (45 px/s²),
    // and it cannot afford a jump that would lift it (JumpImpulse / Mass < v_min).
    public static FighterSpec Brick() => new()
    {
        Name        = "Brick",
        Kind        = EntityKind.Brick,
        Radius      = 13f,
        Sides       = 8,
        Health      = 8f,
        Armor       = 1.5f,
        Strength    = 1.25f,
        GroundPower = 110f,
        Color       = new Color(120, 90, 80),
        Actions     =
        {
            Melee(damage: 1.2f, reach: 24f, maxRange: 34f),
        },
        EngageRange     = 26f,
        AlertRange      = 240f,
        PreferredAction = 0,
        Brain           = s => new FighterCloserBrain(s),
    };

    // Sprinter — ground power, a jump, a lunge and a jab, on a two-HP body. Proves speed
    // costs health: ≈1.87 mass of which the legs are 0.82, three times the Brick's walk
    // acceleration, and it breaks off to reposition once it is half dead.
    public static FighterSpec Sprinter() => new()
    {
        Name          = "Sprinter",
        Kind          = EntityKind.Sprinter,
        Radius        = 9f,
        Sides         = 4,
        Health        = 2f,
        GroundPower   = 260f,
        JumpImpulse   = 400f,
        EnergyReserve = 3f,
        EnergyRegen   = 0.6f,
        Color         = new Color(230, 200, 60),
        Actions       =
        {
            Lunge(damage: 0.9f, speed: 300f),                   // 0 — 36..90 px, 0.6 energy
            Melee(damage: 0.8f, reach: 18f, maxRange: 28f),     // 1 — the jab up close
        },
        EngageRange        = 22f,
        AlertRange         = 260f,
        RetreatBelowHealth = 0.5f,
        PreferredAction    = 0,
        Brain              = s => new FighterCloserBrain(s),
    };

    // Gunner — an energy reserve, a stock energy ball, and target memory. Proves energy is
    // a real limit: five shots in the bank at 1.0 each and 0.6/s back, so a long fight is
    // paced by the meter rather than by the windup.
    public static FighterSpec Gunner() => new()
    {
        Name          = "Gunner",
        Kind          = EntityKind.Gunner,
        Radius        = 10f,
        Sides         = 6,
        Health        = 3f,
        GroundPower   = 120f,
        EnergyReserve = 5f,
        EnergyRegen   = 0.6f,
        TargetMemory  = true,
        Color         = new Color(80, 140, 200),
        Actions       =
        {
            ActionSpec.Default(ActionKind.Ranged),              // 0 — 90..360 px, 1.0 energy
        },
        EngageRange     = 220f,
        StandoffRange   = 110f,
        AlertRange      = 320f,
        RetreatBelowHealth = 0.5f,
        PreferredAction = 0,
        Brain           = s => new FighterKiterBrain(s),
    };

    // Flyer — thrust and a dive slam on a bird's body. Proves Thrust/Mass > g binds:
    // ≈1.27 mass against 900 thrust is 709 px/s², 18% over gravity — buy four more HP and
    // it drops under 600 and the compiler refuses the flight (FighterCostTests pins it).
    public static FighterSpec Flyer() => new()
    {
        Name          = "Flyer",
        Kind          = EntityKind.Flyer,
        Radius        = 9f,
        Sides         = 5,
        Health        = 2f,
        Thrust        = 900f,
        EnergyReserve = 3f,
        EnergyRegen   = 1.0f,      // ≥ the 0.9/s hover drain, so it can stay up
        Color         = new Color(110, 90, 170),
        Actions       =
        {
            Slam(fallSpeedMin: 50f),                            // 0 — the dive's own 80 px/s clears it
        },
        EngageRange     = 24f,     // horizontal window that starts a dive
        HoverHeight     = 70f,
        AlertRange      = 280f,
        PreferredAction = 0,
        Brain           = s => new FighterHoverDiveBrain(s),
    };

    // Builder — an energy bank, legs, a jump and a light shot. Proves terrain actions are
    // worth their energy — once it has them.
    // TODO(fighter phase 3b): add ActionKind.PlaceBlock and ActionKind.SpawnBlockInAir to
    // this kit when those kinds land (another parcel is implementing them). The slot
    // budget already has room: ranged + jump + the two block actions = 4 = MaxSlots.
    public static FighterSpec Builder() => new()
    {
        Name          = "Builder",
        Kind          = EntityKind.Builder,
        Radius        = 10f,
        Sides         = 6,
        Health        = 3f,
        GroundPower   = 120f,
        JumpImpulse   = 350f,
        EnergyReserve = 10f,
        EnergyRegen   = 1.0f,
        Color         = new Color(170, 120, 60),
        Actions       =
        {
            Ranged(damage: 0.6f, speed: 400f),                  // 0 — 0.48 energy a shot
        },
        EngageRange     = 180f,
        StandoffRange   = 100f,
        AlertRange      = 300f,
        PreferredAction = 0,
        Brain           = s => new FighterKiterBrain(s),
    };

    // Turret — rooted (a two-point refund), a rail shot, a big reserve, target memory.
    // Proves rooted is a real trade: the discount is what pays for the rail's two points,
    // and the price is that it can never leave the spot it was placed on.
    public static FighterSpec Turret() => new()
    {
        Name          = "Turret",
        Kind          = EntityKind.FighterTurret,
        Rooted        = true,
        Radius        = 14f,
        Sides         = 8,
        Health        = 6f,
        EnergyReserve = 20f,
        EnergyRegen   = 2f,
        TargetMemory  = true,
        Color         = new Color(90, 95, 115),
        Actions       =
        {
            ActionSpec.Default(ActionKind.RailShot),            // 0 — 6.0 energy a bolt
        },
        EngageRange     = 0f,
        StandoffRange   = 0f,
        AlertRange      = 540f,
        PreferredAction = 0,
        Brain           = s => new FighterKiterBrain(s),
    };

    // Canonical order — the "fighters" stage spawns them left to right in this order.
    public static IReadOnlyList<Func<FighterSpec>> All { get; } = new Func<FighterSpec>[]
    {
        Brick, Sprinter, Gunner, Flyer, Builder, Turret,
    };

    // Compile every archetype with the default physics model and register it. Called
    // from EnemyFactory.RegisterBuiltIns and again after FighterCosts.Load. An archetype
    // that fails to compile is a startup error, not a silently missing enemy.
    public static void RegisterAll()
    {
        var model = new PhysicsCostModel();
        foreach (var make in All)
        {
            var spec = make();
            var r    = FighterCompiler.Register(spec, model);
            if (!r.IsValid)
                throw new InvalidOperationException(
                    $"Fighter '{spec.Name}' does not compile under the default budget:\n{r.Report()}");
        }
    }

    // ── Spec helpers: a stock ActionSpec with the knobs an archetype varies ──────

    private static ActionSpec Melee(float damage, float reach, float maxRange)
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = damage; a.Reach = reach; a.MaxRange = maxRange;
        return a;
    }

    private static ActionSpec Lunge(float damage, float speed)
    {
        var a = ActionSpec.Default(ActionKind.Lunge);
        a.Damage = damage; a.Speed = speed;
        return a;
    }

    private static ActionSpec Slam(float fallSpeedMin)
    {
        var a = ActionSpec.Default(ActionKind.Slam);
        a.FallSpeedMin = fallSpeedMin;
        return a;
    }

    private static ActionSpec Ranged(float damage, float speed)
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Damage = damage; a.Speed = speed;
        return a;
    }
}
