using System;
using System.Collections.Generic;

namespace MTile;

// The four currencies of Plans/FIGHTER_DESIGN_PLAN.md §3.1. Every feature reports a
// vector across all four; the compiler sums them and checks each against the budget.
//   Mass   — continuous, derived; becomes EnemyBlueprint.Mass.
//   Energy — meter units. Per-use for actions, per-second for flight: informational in
//            the total (no compile-time cap — reserve and regen cost mass instead).
//   Slots  — how many actions and movement modes a fighter carries.
//   Points — the catch-all for things with no physical price.
public readonly record struct Cost(float Mass, float Energy, int Slots, int Points)
{
    public static readonly Cost Zero = default;

    public static Cost operator +(Cost a, Cost b) => new(a.Mass + b.Mass, a.Energy + b.Energy,
                                                         a.Slots + b.Slots, a.Points + b.Points);
}

public sealed record FighterBudget(float MaxMass, int MaxSlots, int MaxPoints);

public interface ICostModel
{
    Cost AttributeCost(FighterSpec s);            // mass the attributes add, plus points/slots
    Cost ActionCost(in ActionSpec a, FighterSpec s);
    Cost BrainCost(FighterSpec s);                // e.g. target memory's raycast → points
    FighterBudget Budget { get; }                 // caps per currency
    IEnumerable<string> Validate(FighterSpec s, Cost total);   // constraint violations
}

// The first cost model: mass is the spine (§3.2), actions are priced from their knobs
// (§3.3). Every coefficient comes from a FighterCostConfig — FighterCosts.Current by
// default — so the whole economy retunes from configs/fighter_costs.json.
public sealed class PhysicsCostModel : ICostModel
{
    private readonly FighterCostConfig _k;

    public PhysicsCostModel(FighterCostConfig config = null)
    {
        _k     = config ?? FighterCosts.Current;
        Budget = _k.Budget;
    }

    public PhysicsCostModel(FighterBudget budget, FighterCostConfig config = null)
    {
        _k     = config ?? FighterCosts.Current;
        Budget = budget;
    }

    public FighterBudget Budget { get; }

    public Cost AttributeCost(FighterSpec s)
    {
        float mass = _k.KBody * s.Radius * s.Radius
                   + _k.KHp   * s.Health
                   + _k.KStr  * MathF.Max(0f, s.Strength - 1f)
                   + _k.KArm  * s.Armor
                   + _k.KE    * s.EnergyReserve
                   + _k.KR    * s.EnergyRegen
                   + _k.KGp   * s.GroundPower
                   + _k.KJ    * s.JumpImpulse
                   + _k.KT    * s.Thrust
                   + (s.Cling ? _k.KCling : 0f);

        // Movement modes take slots; walking is the baseline and does not.
        int slots = (s.JumpImpulse > 0f ? 1 : 0) + (s.Thrust > 0f ? 1 : 0) + (s.Cling ? 1 : 0);
        int points = s.Rooted ? -_k.RootedDiscount : 0;
        // Flight's energy is per SECOND airborne (§3.3 "Fly" row).
        float energy = s.Thrust * _k.CHover;
        return new Cost(mass, energy, slots, points);
    }

    public Cost ActionCost(in ActionSpec a, FighterSpec s)
    {
        float kA = _k.KA, refReach = _k.ReachRef;
        return a.Kind switch
        {
            ActionKind.Melee      => new Cost(kA * a.Damage * a.Reach, 0f, 1, 0),
            // Touch damage: priced by the size of the damage box.
            ActionKind.Contact    => new Cost(kA * a.Damage * a.HalfWidth, 0f, 1, 0),
            // A lunge's reach is the distance its hitbox sweeps.
            ActionKind.Lunge      => new Cost(kA * a.Damage * (a.Speed * a.Active + a.HalfWidth),
                                              _k.KDash * a.Speed, 1, 0),
            ActionKind.Slam       => new Cost(kA * a.Damage * refReach, 0f, 1, _k.SlamPoints),
            ActionKind.PounceSlam => new Cost(kA * MathF.Max(a.Damage, a.DamageMax) * refReach, 0f, 1, _k.SlamPoints),
            ActionKind.Ranged     => new Cost(kA * a.Damage * refReach,
                                              _k.KShot * a.Speed * a.Damage, 1, 0),
            ActionKind.RailShot   => new Cost(kA * a.Damage * refReach,
                                              _k.KShot * a.Speed * a.Damage + _k.KTile * a.Penetration,
                                              1, _k.RailPoints),
            ActionKind.Lash       => new Cost(kA * a.Damage * a.Reach, 0f, 1, _k.LashPoints),
            // Special actions carry private knobs the model cannot read. Validate
            // reports them; price them as one slot so the report still adds up.
            _                     => new Cost(0f, 0f, 1, 0),
        };
    }

    public Cost BrainCost(FighterSpec s)
        => new(0f, 0f, 0, s.TargetMemory ? _k.TargetMemoryPoints : 0);

    public IEnumerable<string> Validate(FighterSpec s, Cost total)
    {
        float mass = MathF.Max(total.Mass, 1e-4f);
        const float g = 600f;   // Simulation.WorldGravityY; a literal so the message can quote it

        if (s.Strength < _k.MinStrength)
            yield return $"Strength {s.Strength:0.##} is below the floor {_k.MinStrength:0.##}.";
        if (s.Health <= 0f)
            yield return "Health must be positive.";
        if (s.Radius <= 0f || s.Sides < 3)
            yield return "Body needs a positive Radius and at least 3 Sides.";

        if (s.Rooted)
        {
            if (s.GroundPower > 0f) yield return "Rooted excludes GroundPower (a rooted fighter cannot walk).";
            if (s.JumpImpulse > 0f) yield return "Rooted excludes JumpImpulse (a rooted fighter cannot jump).";
            if (s.Thrust      > 0f) yield return "Rooted excludes Thrust (a rooted fighter cannot fly).";
            if (s.Cling)            yield return "Rooted excludes Cling (a rooted fighter cannot climb).";
        }
        else
        {
            // GroundPower 0 is allowed — it is a fighter that does not walk (a flyer).
            // What is refused is paying for legs too weak to carry the body.
            if (s.GroundPower > 0f && s.GroundPower / mass < _k.AMin)
                yield return $"Too heavy to walk: GroundPower / Mass = {s.GroundPower / mass:0.#} px/s² " +
                             $"is below a_min {_k.AMin:0.#} (Mass {mass:0.###}).";
            if (s.JumpImpulse > 0f && s.JumpImpulse / mass < _k.VMin)
                yield return $"Too heavy to jump: JumpImpulse / Mass = {s.JumpImpulse / mass:0.#} px/s " +
                             $"is below v_min {_k.VMin:0.#} (Mass {mass:0.###}).";
            if (s.Thrust > 0f && s.Thrust / mass <= g)
                yield return $"Too heavy to fly: Thrust / Mass = {s.Thrust / mass:0.#} px/s² " +
                             $"does not exceed gravity {g:0} (Mass {mass:0.###}).";
        }
        if (s.Cling && s.Thrust > 0f)
            yield return "Cling and Thrust are exclusive locomotion modes (EnemyFlyState vs EnemyClingMoveState).";
        // EnemyFlyState only runs while the meter is non-empty when flight drains it.
        if (s.Thrust * _k.CHover > 0f && s.EnergyReserve <= 0f)
            yield return "Flight drains energy: Thrust needs an EnergyReserve.";

        bool canLeaveGround = (s.JumpImpulse > 0f || s.Thrust > 0f) && !s.Rooted;
        for (int i = 0; i < s.Actions.Count; i++)
        {
            var a = s.Actions[i];
            switch (a.Kind)
            {
                case ActionKind.Special:
                    yield return $"Action {i} is Special: specials carry private knobs and cannot be bought.";
                    break;
                case ActionKind.Slam:
                case ActionKind.PounceSlam:
                    if (!canLeaveGround)
                        yield return $"Action {i} ({a.Kind}) needs jump or flight to fall onto anything.";
                    break;
                case ActionKind.Lash:
                    if (!s.Cling)
                        yield return $"Action {i} (Lash) needs Cling.";
                    break;
            }
            float use = ActionCost(a, s).Energy;
            if (use > 0f && use > s.EnergyReserve)
                yield return $"Action {i} ({a.Kind}) is unaffordable: {use:0.##} energy per use " +
                             $"exceeds EnergyReserve {s.EnergyReserve:0.##}.";
        }
    }
}
