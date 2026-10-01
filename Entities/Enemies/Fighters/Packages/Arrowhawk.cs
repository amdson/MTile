using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Arrowhawk: a fast, mobile ranged+melee hybrid. Balanced between chip damage via
// energy-efficient shots and close-quarters finishing with a lunge and jab. The brain
// reads tells and ratios energy: exact reads only when a shot is still affordable,
// otherwise relies on coarse positioning. Dodges incoming attacks and switches between
// zoning (ranged) and finishing (lunge/melee) based on range.
public sealed class Arrowhawk : IFighterPackage
{
    public string Name   => "Arrowhawk";
    public string Author => "haiku-w3";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 4.5f,
        Strength       = 1.1f,
        Armor          = 0.2f,
        Density        = 1.0f,
        ReactionFrames = 6,
        GroundPower    = 200f,
        JumpImpulse    = 375f,
        EnergyReserve  = 3f,
        EnergyRegen    = 1.0f,
        Color          = new Color(180, 160, 80),
        Actions        = { Shot(), Lunge(), Melee() },
        EngageRange    = 28f,
        AlertRange     = 400f,
        Brain          = s => new ArrowhawkBrain(s),
    };

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Windup = 0.60f; a.Recovery = 0.50f; a.Speed = 250f; a.Damage = 1.0f;
        a.MinRange = 50f; a.MaxRange = 300f;
        return a;
    }

    private static ActionSpec Lunge()
    {
        var a = ActionSpec.Default(ActionKind.Lunge);
        a.MinRange = 45f; a.MaxRange = 85f;
        return a;
    }

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 1.2f; a.Reach = 24f; a.MaxRange = 34f;
        return a;
    }

    // Brain scratch: I0 = mode (0 hunt, 1 dodge), F0 = time in mode
    private sealed class ArrowhawkBrain : FighterController
    {
        private readonly float _shotCost, _shotMin, _shotMax, _lungeMax, _meleeMax, _engage, _alert;

        public ArrowhawkBrain(FighterSpec s)
        {
            var shot = s.Actions[0];
            _shotCost = shot.EnergyCost; _shotMin = shot.MinRange; _shotMax = shot.MaxRange;
            _lungeMax = s.Actions[1].MaxRange; _meleeMax = s.Actions[2].MaxRange;
            _engage = s.EngageRange; _alert = s.AlertRange;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            EnemyTarget t; int age;

            // Buy exact read only if a shot stays affordable; otherwise fall back to stale or coarse
            if (self.Energy - s.TargetCost >= _shotCost + 0.5f && self.Energy >= _shotCost)
                t = s.Target(out age);
            else
                t = s.TargetStale(out age);
            if (!t.Known || age > 16) t = s.TargetCoarse();

            var to = t.Position - self.Position;
            float dist = to.Length();
            var input = new EnemyInput { AimWorld = t.Position };

            // Exit alert if out of range
            if (dist > _alert) { m.I0 = 0; m.F0 = 0f; return input; }
            m.F0 += s.Dt;

            // Dodge logic: if we see a windup at reasonable range, back away
            bool incoming = t.Known && t.TellProgress >= 0.35f && t.TellProgress < 1f
                            && t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.RailShot
                            && dist < 90f && !self.ActionCommitted;

            if (m.I0 == 0 && incoming) { m.I0 = 1; m.F0 = 0f; }
            if (m.I0 == 1)
            {
                if (m.F0 < 0.25f) { input.MoveDir.X = to.X >= 0f ? -1f : 1f; return input; }
                m.I0 = 0; m.F0 = 0f;
            }

            // Movement toward target if out of engage range
            if (dist > _engage) input.MoveDir.X = to.X >= 0f ? 1f : -1f;

            // Vertical movement to match target height (helps with shots and dodges)
            input.Jump = to.Y < -15f;

            input.WantAttack = true;

            // Action selection: ranged at distance, lunge mid, melee close
            if (dist >= 55f && dist <= _shotMax && s.Visible && self.Energy >= _shotCost + 0.3f)
                input.RequestedAction = 0; // shot
            else if (dist >= 48f && dist <= _lungeMax)
                input.RequestedAction = 1; // lunge
            else if (dist <= _meleeMax)
                input.RequestedAction = 2; // melee
            else
                input.RequestedAction = null; // let sim choose

            return input;
        }
    }
}
