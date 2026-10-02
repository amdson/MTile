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
        float mass = _k.KHp   * s.Health
                   + _k.KStr  * MathF.Max(0f, s.Strength - 1f)
                   + _k.KArm  * s.Armor
                   + _k.KE    * s.EnergyReserve
                   + _k.KR    * s.EnergyRegen
                   + _k.KGp   * s.GroundPower
                   + _k.KJ    * s.JumpImpulse
                   + _k.KT    * s.Thrust
                   + (s.Cling ? _k.KCling : 0f)
                   + _k.KDensity * MathF.Max(0f, s.Density - 1f)
                   + _k.KReact   * MathF.Max(0, _k.ReactionFramesDefault - s.ReactionFrames);

        // Movement modes take slots; walking is the baseline and does not.
        int slots = (s.JumpImpulse > 0f ? 1 : 0) + (s.Thrust > 0f ? 1 : 0) + (s.Cling ? 1 : 0);
        int points = s.Rooted ? -_k.RootedDiscount : 0;
        // Flight's energy is per SECOND airborne (§3.3 "Fly" row).
        float energy = s.Thrust * _k.CHover;
        return new Cost(mass, energy, slots, points);
    }

    public Cost ActionCost(in ActionSpec a, FighterSpec s)
    {
        var c = BaseActionCost(in a, s);
        // Knockback above the kind's stock push is control, and is paid for at the shove's
        // rate (a shove already pays for all of its push). Below stock is free.
        if (a.Kind is not (ActionKind.Shove or ActionKind.Special))
        {
            float extra = a.Knockback.Length() - ActionSpec.Default(a.Kind).Knockback.Length();
            if (extra > 0f) c = c with { Mass = c.Mass + _k.KShove * extra };
        }
        return c;
    }

    private Cost BaseActionCost(in ActionSpec a, FighterSpec s)
    {
        float kA = _k.KA, refReach = _k.ReachRef;
        return a.Kind switch
        {
            ActionKind.Melee      => new Cost(kA * a.Damage * a.Reach, 0f, 1, 0),
            // Touch damage. The box is the body (compiler: Radius + ContactMargin), not a
            // knob, so it is priced on the stock box, never on the spec's HalfWidth.
            ActionKind.Contact    => new Cost(kA * a.Damage * ActionSpec.Default(ActionKind.Contact).HalfWidth, 0f, 1, 0),
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
            // Terrain actions (§3.3): weightless, paid per tile in the same units as the
            // player's build meter; conjuring a block in mid-air carries a premium and a point.
            ActionKind.PlaceBlock      => new Cost(0f, MaterialStrengths.BuildCostFor(a.Material) * _k.PlaceDiscount, 1, 0),
            ActionKind.SpawnBlockInAir => new Cost(0f, MaterialStrengths.BuildCostFor(a.Material) * _k.AirPremium,
                                                   1, _k.SpawnInAirPoints),
            // Dash: weightless; paid per use for distance and for untouchable time.
            ActionKind.Dash => new Cost(0f, _k.KDashDistance * a.Speed * a.Active + _k.KIntangible * a.Intangible, 1, 0),
            // Mine: a swing's mass for half its blast reach; per use for damage × reach,
            // cheaper for a slow arm or a short fuse.
            ActionKind.Mine => new Cost(0.5f * kA * a.Damage * a.BlastRadius,
                                        _k.KMine * a.Damage * a.BlastRadius
                                        * Math.Clamp(_k.MineArmRef / MathF.Max(a.ArmDelay, 1e-3f), 0.5f, 2f)
                                        * (0.5f + 0.5f * a.Fuse / _k.MineFuseRef), 1, 0),
            // Guard: weightless; raising it costs GuardRaiseCost (holding and blocking drain
            // the meter at run time); a point, like the other defensive specials.
            ActionKind.Guard => new Cost(0f, _k.GuardRaiseCost, 1, _k.GuardPoints),
            // Shove: a swing's mass for its reach and damage, plus mass for the push.
            ActionKind.Shove => new Cost(kA * a.Damage * a.Reach + _k.KShove * a.Knockback.Length(),
                                         _k.ShoveEnergy, 1, 0),
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
        if (s.Sides < 3)
            yield return "Body needs at least 3 Sides.";
        if (s.Density < _k.DensityMin || s.Density > _k.DensityMax)
            yield return $"Density {s.Density:0.##} is outside [{_k.DensityMin:0.##}, {_k.DensityMax:0.##}].";
        if (s.ReactionFrames < 0 || s.ReactionFrames >= TargetHistory.Capacity)
            yield return $"ReactionFrames {s.ReactionFrames} is outside [0, {TargetHistory.Capacity - 1}].";

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
                case ActionKind.Dash:
                    if (a.Speed <= 0f || a.Active <= 0f)
                        yield return $"Action {i} (Dash) needs a positive Speed and Active.";
                    if (a.Speed * a.Active > _k.DashMaxDistance)
                        yield return $"Action {i} (Dash) covers {a.Speed * a.Active:0} px; the cap is {_k.DashMaxDistance:0}.";
                    if (a.Intangible < 0f || a.Intangible > a.Active)
                        yield return $"Action {i} (Dash) Intangible {a.Intangible:0.###} s must be within [0, Active {a.Active:0.###}].";
                    break;
                case ActionKind.Guard:
                    if (a.Active <= 0f || a.Active > _k.GuardMaxHold)
                        yield return $"Action {i} (Guard) Active {a.Active:0.##} s (the longest hold) must be within (0, {_k.GuardMaxHold:0.##}].";
                    break;
                case ActionKind.Shove:
                    if (a.Knockback.Length() > _k.ShoveMaxImpulse)
                        yield return $"Action {i} (Shove) knockback {a.Knockback.Length():0} exceeds the cap {_k.ShoveMaxImpulse:0}.";
                    if (a.Damage < 0f || a.Damage > _k.ShoveMaxDamage)
                        yield return $"Action {i} (Shove) Damage {a.Damage:0.##} must be within [0, {_k.ShoveMaxDamage:0.##}]: a shove is a push (use Melee for damage).";
                    break;
                case ActionKind.Mine:
                    if (a.Damage <= 0f)
                        yield return $"Action {i} (Mine) needs positive Damage.";
                    if (a.BlastRadius <= 0f || a.BlastRadius > _k.MineMaxRadius)
                        yield return $"Action {i} (Mine) BlastRadius {a.BlastRadius:0} must be within (0, {_k.MineMaxRadius:0}].";
                    if (a.ArmDelay < _k.MineMinArm)
                        yield return $"Action {i} (Mine) ArmDelay {a.ArmDelay:0.##} s is below {_k.MineMinArm:0.##} s.";
                    if (a.Fuse <= a.ArmDelay || a.Fuse > _k.MineMaxFuse)
                        yield return $"Action {i} (Mine) Fuse {a.Fuse:0.##} s must exceed ArmDelay and be at most {_k.MineMaxFuse:0.##} s.";
                    if (a.MaxRange < 0f || a.MaxRange > _k.MineMaxRange)
                        yield return $"Action {i} (Mine) MaxRange {a.MaxRange:0} must be within [0, {_k.MineMaxRange:0}].";
                    if (a.MaxLive < 1 || a.MaxLive > _k.MineMaxLive)
                        yield return $"Action {i} (Mine) MaxLive {a.MaxLive} must be within [1, {_k.MineMaxLive}].";
                    break;
            }
            float use = ActionCost(a, s).Energy;
            if (use > 0f && use > s.EnergyReserve)
                yield return $"Action {i} ({a.Kind}) is unaffordable: {use:0.##} energy per use " +
                             $"exceeds EnergyReserve {s.EnergyReserve:0.##}.";
        }
    }
}
