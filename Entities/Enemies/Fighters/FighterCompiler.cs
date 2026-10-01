using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// One priced line of a cost report: what it is, and what it costs.
public readonly record struct CostPart(string Label, Cost Cost);

// What FighterCompiler.Compile hands back. Blueprint is null whenever Violations is
// non-empty — there is no "over budget but allowed" mode (§3.4), because an arena
// result from an illegal fighter would be meaningless.
public sealed class CompileResult
{
    public EnemyBlueprint         Blueprint  { get; init; }
    public Cost                   Total      { get; init; }
    public Cost                   Attributes { get; init; }
    public IReadOnlyList<CostPart> ActionCosts { get; init; }
    public Cost                   Brain      { get; init; }
    public IReadOnlyList<string>  Violations { get; init; }
    public FighterBudget          Budget     { get; init; }

    public bool IsValid => Violations.Count == 0;

    // Every priced part, attributes first — the lines of a cost report. Sums to Total.
    public IEnumerable<CostPart> Parts
    {
        get
        {
            yield return new CostPart("attributes", Attributes);
            foreach (var p in ActionCosts) yield return p;
            yield return new CostPart("brain", Brain);
        }
    }

    public string Report()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in Parts)
            sb.AppendLine($"  {p.Label,-18} mass {p.Cost.Mass,6:0.000}  energy {p.Cost.Energy,6:0.00}  " +
                          $"slots {p.Cost.Slots,2}  points {p.Cost.Points,2}");
        sb.AppendLine($"  {"TOTAL",-18} mass {Total.Mass,6:0.000}  energy {Total.Energy,6:0.00}  " +
                      $"slots {Total.Slots,2}  points {Total.Points,2}   " +
                      $"(budget {Budget.MaxMass:0.##} / {Budget.MaxSlots} / {Budget.MaxPoints})");
        foreach (var v in Violations) sb.AppendLine("  ✗ " + v);
        return sb.ToString();
    }
}

// FighterSpec → priced, validated EnemyBlueprint (Plans/FIGHTER_DESIGN_PLAN.md §2, §3.4).
// The blueprint stays the sim's only view of an enemy, so spawn, snapshot, rehydrate and
// stages need nothing new. Pure function of (spec, model): compiling twice gives the
// same costs and an equivalent blueprint.
public static class FighterCompiler
{
    // Half-width the ordering rule assumes for whatever the fighter is swinging at
    // (the player's is ~6 px; TemplateEnemy documents the same number).
    public const float TargetHalfWidth = 6f;

    // Floor friction scale for every compiled fighter — the stock enemies' band.
    // EnemyChaseState's powered walk pre-compensates the floor's full Coulomb brake, so
    // friction never gates whether a fighter can start walking; a_min is the only floor.
    public const float FighterFrictionScale = 0.10f;

    public static CompileResult Compile(FighterSpec spec, ICostModel model)
    {
        if (spec == null)  throw new ArgumentNullException(nameof(spec));
        if (model == null) throw new ArgumentNullException(nameof(model));

        var attrs = model.AttributeCost(spec);
        var brain = model.BrainCost(spec);
        var total = attrs + brain;
        var actionCosts = new List<CostPart>(spec.Actions.Count);
        for (int i = 0; i < spec.Actions.Count; i++)
        {
            var a = spec.Actions[i];
            var c = model.ActionCost(a, spec);
            actionCosts.Add(new CostPart($"{i}:{a.Kind}", c));
            total += c;
        }

        var violations = new List<string>();
        var budget = model.Budget;
        if (total.Mass > budget.MaxMass)
            violations.Add($"Over the mass budget: {total.Mass:0.###} > {budget.MaxMass:0.###}.");
        if (total.Slots > budget.MaxSlots)
            violations.Add($"Over the slot budget: {total.Slots} > {budget.MaxSlots}.");
        if (total.Points > budget.MaxPoints)
            violations.Add($"Over the points budget: {total.Points} > {budget.MaxPoints}.");
        if (spec.Brain == null)
            violations.Add("No brain: a fighter ships with its controller.");
        if (spec.PreferredAction >= spec.Actions.Count)
            violations.Add($"PreferredAction {spec.PreferredAction} is out of range " +
                           $"(the kit has {spec.Actions.Count} actions).");
        violations.AddRange(model.Validate(spec, total));
        // The ordering rule is checked against the reach the compiled body will actually
        // have (hitbox geometry scales with the derived radius; trigger bands do not).
        violations.AddRange(ValidateOrdering(spec, DerivedRadius(total.Mass, spec) / FighterCosts.Current.ReferenceRadius));

        var result = new CompileResult
        {
            Total       = total,
            Attributes  = attrs,
            ActionCosts = actionCosts,
            Brain       = brain,
            Violations  = violations,
            Budget      = budget,
            Blueprint   = violations.Count == 0 ? Build(spec, model, total, actionCosts) : null,
        };
        return result;
    }

    // Compile + EnemyFactory.Register. Registers ONLY a valid blueprint; the result says
    // why when it did not.
    public static CompileResult Register(FighterSpec spec, ICostModel model)
    {
        var r = Compile(spec, model);
        if (r.Blueprint != null) EnemyFactory.Register(r.Blueprint);
        return r;
    }

    // ── The ordering rule (§4) ───────────────────────────────────────────────
    // effective reach > trigger MaxRange > the brain's EngageRange, for every
    // melee-band action. Break it and the fighter pins at its trigger boundary
    // swinging at air (TemplateEnemy measured it: frozen at 54.7 px, 0 hits in 420
    // frames). Contact is excluded — its trigger range is a deliberately generous
    // broad-phase gate, the body IS the hitbox.
    public static IEnumerable<string> ValidateOrdering(FighterSpec s, float geometryScale = 1f)
    {
        for (int i = 0; i < s.Actions.Count; i++)
        {
            var a = ScaleGeometry(s.Actions[i], geometryScale);
            if (EffectiveReach(a) is not float reach) continue;
            if (reach <= a.MaxRange)
                yield return $"Action {i} ({a.Kind}) ordering: effective reach {reach:0.#} must exceed " +
                             $"trigger MaxRange {a.MaxRange:0.#}, or the fighter swings at air.";
            if (a.MaxRange <= s.EngageRange)
                yield return $"Action {i} ({a.Kind}) ordering: trigger MaxRange {a.MaxRange:0.#} must exceed " +
                             $"the brain's EngageRange {s.EngageRange:0.#}, or the fighter never closes to it.";
        }
    }

    // Centre-to-centre distance at which a melee-band action still connects, or null for
    // kinds the ordering rule does not cover. Mirrors each action's hitbox geometry.
    public static float? EffectiveReach(in ActionSpec a) => a.Kind switch
    {
        // Box spans 8..(8 + Reach) in front of the body (EnemyMeleeAction.Update).
        ActionKind.Melee => 8f + a.Reach + TargetHalfWidth,
        // Body-anchored box carried Speed · Active px by the dash.
        ActionKind.Lunge => a.Speed * a.Active + a.HalfWidth + TargetHalfWidth,
        // Rotated box from the body centre out to Reach (EnemyLashAction.Update).
        ActionKind.Lash  => a.Reach + TargetHalfWidth,
        _                => null,
    };

    // ── Blueprint assembly ───────────────────────────────────────────────────

    private static EnemyBlueprint Build(FighterSpec spec, ICostModel model, Cost total,
                                        List<CostPart> actionCosts)
    {
        // Snapshot everything the factories close over NOW, so a spec mutated after
        // compile cannot change what an already-registered blueprint spawns.
        var actions = new ActionSpec[spec.Actions.Count];
        for (int i = 0; i < actions.Length; i++)
        {
            var a = spec.Actions[i];
            // The model's per-use price IS what the meter charges at runtime: the
            // compiler overwrites whatever EnergyCost the spec carried, so the number
            // the cost report shows and the number the sim spends can never disagree.
            a.EnergyCost = actionCosts[i].Cost.Energy;
            actions[i] = a;
        }

        bool rooted  = spec.Rooted;
        bool walks   = spec.GroundPower > 0f;
        bool jumps   = spec.JumpImpulse > 0f;
        bool flies   = spec.Thrust > 0f;
        bool clings  = spec.Cling;
        // EnemyAttackHoldState at priority 40 preempts Fly/Cling and un-latches or
        // grounds them (see RegisterBuiltIns: Pouncer, Latcher, Bird). A rooted fighter
        // never moves, so it gets Idle alone, like the Bastion.
        bool hold    = !rooted && !flies && !clings;
        bool stagger = !rooted;

        var controller = spec.Brain(spec);
        float thrust   = spec.Thrust;

        return new EnemyBlueprint
        {
            Kind         = spec.Kind,
            Radius       = DerivedRadius(total.Mass, spec),
            Sides        = spec.Sides,
            Health       = spec.Health,
            Mass         = total.Mass,
            FrictionScale = rooted ? 0.9f : FighterFrictionScale,
            // Every fighter raycasts (§16, mandatory line of sight); bought memory
            // decides what a hidden target looks like, not whether sight is checked.
            TargetMemory    = true,
            RemembersTarget = spec.TargetMemory,
            ReactionFrames  = spec.ReactionFrames,
            Rooted       = rooted,
            Team         = spec.Team,
            Strength     = spec.Strength,
            Armor        = spec.Armor,
            EnergyMax    = spec.EnergyReserve,
            EnergyRegen  = spec.EnergyRegen,
            GroundPower  = spec.GroundPower,
            JumpImpulse  = spec.JumpImpulse,
            Thrust       = thrust,
            FlightDrain  = model.AttributeCost(spec).Energy,
            Color        = spec.Color,
            Sprite       = spec.Sprite ?? PickSprite(spec, DerivedRadius(total.Mass, spec)),
            Controller   = controller,
            Movement = () =>
            {
                var list = new List<EnemyMovementState> { new EnemyIdleState() };   // 0 — fallback
                if (walks)   list.Add(new EnemyChaseState());
                if (jumps)   list.Add(new EnemyJumpState());
                if (flies)   list.Add(new EnemyFlyState());
                if (clings)  list.Add(new EnemyClingMoveState());
                if (hold)    list.Add(new EnemyAttackHoldState());
                if (stagger) list.Add(new EnemyStaggerState());
                return list;
            },
            Actions = () =>
            {
                var list = new List<EnemyActionState>(actions.Length);
                float geom = DerivedRadius(total.Mass, spec) / FighterCosts.Current.ReferenceRadius;
                foreach (var a in actions) list.Add(CreateAction(ScaleGeometry(a, geom)));
                return list;
            },
        };
    }

    // Body radius from the compiled mass (§16): R = RadiusPerSqrtMass · √(Mass / Density).
    public static float DerivedRadius(float mass, FighterSpec spec)
    {
        var k = FighterCosts.Current;
        float d = MathHelper.Clamp(spec.Density, k.DensityMin, k.DensityMax);
        return k.RadiusPerSqrtMass * MathF.Sqrt(MathF.Max(mass, 0.05f) / d);
    }

    // Pool-action HITBOX geometry (reach and extents) is authored for ReferenceRadius and
    // scales with the compiled body: a bigger fighter swings a bigger arc. Trigger bands
    // (Min/MaxRange, VerticalSlack) are the designer's numbers and stay as written — the
    // brain's ranges are paired with them. The COST uses the nominal (unscaled) spec, so
    // pricing never depends on the mass it is helping to compute.
    public static ActionSpec ScaleGeometry(ActionSpec a, float scale)
    {
        if (MathF.Abs(scale - 1f) < 1e-4f) return a;
        a.Reach      *= scale;
        a.HalfWidth  *= scale;
        a.HalfHeight *= scale;
        return a;
    }

    // ActionSpec → pool action flyweight. Kinds without a pool behaviour throw: the
    // cost model already refuses them, so reaching here with one is a compiler bug.
    public static EnemyActionState CreateAction(in ActionSpec a) => a.Kind switch
    {
        ActionKind.Melee      => new EnemyMeleeAction(a),
        ActionKind.Contact    => new EnemyContactAction(a),
        ActionKind.Lunge      => new EnemyLungeAction(a),
        ActionKind.Slam       => new EnemySlamAction(a),
        ActionKind.Ranged     => new EnemyRangedAction(a),
        ActionKind.RailShot   => new EnemyRailShotAction(a),
        ActionKind.PounceSlam => new EnemyPounceSlamAction(a),
        ActionKind.Lash       => new EnemyLashAction(a),
        ActionKind.PlaceBlock      => new EnemyPlaceBlockAction(a),
        ActionKind.SpawnBlockInAir => new EnemySpawnBlockInAirAction(a),
        _ => throw new NotSupportedException(
                 $"ActionKind.{a.Kind} has no pool action a fighter can buy."),
    };

    // A stock sprite chosen by what the body is, so a compiled fighter reads at a glance.
    private static Func<float, Sprite> PickSprite(FighterSpec s, float radius)
    {
        if (s.Thrust > 0f)  return Sprites.Bird;
        if (s.Rooted)       return Sprites.Bastion;
        if (s.Cling)        return Sprites.Latcher;
        if (s.Sides == 3)   return Sprites.Pouncer;
        if (radius <= 10f) return Sprites.Stalker;
        return Sprites.Brute;
    }
}
