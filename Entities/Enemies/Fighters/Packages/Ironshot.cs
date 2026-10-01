using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Ironshot: a mobile hybrid shooter-closer that rationes exact reads and combines ranged pressure
// with aggressive finishing. Shoots at range to pressure and establish positioning, then lunges or
// melees to close and finish. Good on all terrains; avoids pure kiters' weakness of time-outs.
public sealed class Ironshot : IFighterPackage
{
    public string Name   => "Ironshot";
    public string Author => "haiku-w3";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 5f,
        Strength       = 1.35f,
        Density        = 1.3f,
        ReactionFrames = 3,
        GroundPower    = 200f,
        JumpImpulse    = 365f,
        EnergyReserve  = 4f,
        EnergyRegen    = 1.2f,
        Color          = new Color(180, 200, 100),
        Actions        = { Shot(), Melee(), Lunge() },
        EngageRange    = 26f,
        AlertRange     = 350f,
        Brain          = s => new IronshotBrain(s),
    };

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.MinRange = 50f; a.MaxRange = 280f; a.Speed = 450f;
        return a;
    }

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 1.2f; a.Reach = 24f; a.MaxRange = 34f;
        return a;
    }

    private static ActionSpec Lunge()
    {
        var a = ActionSpec.Default(ActionKind.Lunge);
        a.MinRange = 40f; a.MaxRange = 80f;
        return a;
    }

    // Scratch: I0 = mode (0 approach, 1 dodge ranged, 2 dodge melee), I1 = 1 if dodge is hop,
    // F0 = seconds in current mode, F1 = frame counter for read cadence tracking
    private sealed class IronshotBrain : FighterController
    {
        private readonly float _engage, _shotMin, _shotMax, _shotCost, _meleeMax, _lungeMax;
        public IronshotBrain(FighterSpec s)
        {
            _engage = s.EngageRange;
            var shot = s.Actions[0];
            _shotMin = shot.MinRange; _shotMax = shot.MaxRange; _shotCost = shot.EnergyCost;
            _meleeMax = s.Actions[1].MaxRange;
            _lungeMax = s.Actions[2].MaxRange;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = coarse.Known ? (coarse.Position - self.Position).Length() : 9999f;

            // Rationed exact reads: every 3 frames when close, every 6 when far
            EnemyTarget t; int age;
            int readCadence = cd < 120f ? 3 : 6;
            if (s.Frame % readCadence == 0 && self.Energy - s.TargetCost >= _shotCost + 0.2f)
                t = s.Target(out age);
            else
                t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = coarse; age = 0; }

            var pos  = t.Position + t.Velocity * (age * s.Dt);
            var to   = pos - self.Position;
            float dist = to.Length();
            float dx = to.X >= 0f ? 1f : -1f;
            var input = new EnemyInput { AimWorld = pos };

            m.F0 += s.Dt;

            // Dodge incoming ranged attacks: jump when an opponent shot is about to fire
            bool opponentShot = t.Known && t.TellProgress >= 0.5f && t.TellProgress < 1f
                                && t.Tell == ActionKind.Ranged && dist > 40f && dist < 220f;
            bool opponentRail = t.Known && t.TellProgress >= 0f && t.TellProgress < 0.5f
                                && t.Tell == ActionKind.RailShot && dist > 30f;

            // Dodge melee by backing away
            bool opponentMelee = t.Known && t.TellProgress >= 0.3f && t.TellProgress < 1f
                                 && t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash
                                 && dist < 70f;

            if (m.I0 == 0 && opponentMelee && !self.ActionCommitted)
            {
                m.I0 = 2; m.F0 = 0f;
            }
            if (m.I0 == 0 && (opponentShot || opponentRail) && !self.ActionCommitted && MathF.Abs(self.Velocity.Y) < 40f)
            {
                m.I0 = 1; m.F0 = 0f;
            }

            // Execute dodges
            if (m.I0 == 1)  // Dodge ranged - jump
            {
                if (m.F0 < 0.25f)
                {
                    input.Jump = true;
                    return input;
                }
                m.I0 = 0; m.F0 = 0f;
            }
            if (m.I0 == 2)  // Dodge melee - back away
            {
                if (m.F0 < 0.35f)
                {
                    input.MoveDir.X = -dx;
                    return input;
                }
                m.I0 = 0; m.F0 = 0f;
            }

            // Main navigation and attack logic
            if (dist > 350f) return input;  // Out of alert range

            // Approach opponent if too far
            if (dist > _engage + 20f)
            {
                input.MoveDir.X = dx;
                input.Jump = to.Y < -30f;
            }

            // Jump to avoid ground hazards
            if (to.Y < -40f && MathF.Abs(self.Velocity.Y) < 40f)
                input.Jump = true;

            // Attack selection
            input.WantAttack = true;
            if (dist <= _meleeMax)
            {
                // Close: melee
                input.RequestedAction = 1;
            }
            else if (dist <= _lungeMax && dist >= 40f)
            {
                // Mid range: lunge to close
                input.RequestedAction = 2;
            }
            else if (dist >= _shotMin && dist <= _shotMax && s.Visible && self.Energy >= _shotCost + 0.3f)
            {
                // Range: shoot to pressure
                input.RequestedAction = 0;
            }
            else
            {
                // Out of range or no energy: approach and let sim pick what's available
                input.RequestedAction = null;
            }

            return input;
        }
    }
}
