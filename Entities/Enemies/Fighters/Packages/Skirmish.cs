using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Skirmish: a small, dense lunge assassin. Fast reaction frames; the brain reads the
// opponent's tell, backs out of windups it sees coming, and lunges in at the edge of
// the opponent's reach so the dash lands during their commitment.
public sealed class Skirmish : IFighterPackage
{
    public string Name   => "Skirmish";
    public string Author => "sonnet-w1";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 5f,
        Strength       = 1.3f,
        Armor          = 0.3f,
        Density        = 1.2f,
        ReactionFrames = 4,
        GroundPower    = 220f,
        JumpImpulse    = 380f,
        EnergyReserve  = 1f,
        EnergyRegen    = 0.3f,
        Color          = new Color(230, 90, 90),
        Actions        = { Melee() },
        EngageRange    = 24f,
        AlertRange     = 320f,
        Brain          = s => new SkirmishBrain(s),
    };

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Windup = 0.10f; a.Recovery = 0.35f;
        a.Damage = 1.2f; a.Reach = 22f; a.MaxRange = 34f;
        return a;
    }

    // Scratch: I0 = mode (0 hunt, 1 back off), F0 = seconds in mode.
    private sealed class SkirmishBrain : FighterController
    {
        private readonly float _engage, _alert, _lungeCost, _lungeMin, _lungeMax;
        public SkirmishBrain(FighterSpec s)
        {
            _engage = s.EngageRange; _alert = s.AlertRange;
            _lungeCost = 0f; _lungeMin = 0f; _lungeMax = s.Actions[0].MaxRange;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = coarse.Known ? (coarse.Position - self.Position).Length() : 9999f;
            EnemyTarget t; int age;
            if (cd < 170f && self.Energy - s.TargetCost >= _lungeCost) t = s.Target(out age);
            else t = s.TargetStale(out age);
            if (!t.Known || age > 12) t = coarse;

            var to     = t.Position - self.Position;
            float dist = to.Length();
            float dir  = to.X >= 0f ? 1f : -1f;
            var input  = new EnemyInput { AimWorld = t.Position };
            if (dist > _alert) { m.I0 = 0; m.F0 = 0f; return input; }
            m.F0 += s.Dt;

            bool windup = t.Known && t.TellProgress is >= 0.2f and < 1f
                          && t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash;
            if (m.I0 == 0 && windup && dist < 90f && !self.ActionCommitted) { m.I0 = 1; m.F0 = 0f; }
            if (m.I0 == 1)
            {
                if (m.F0 < 0.28f) { input.MoveDir.X = -dir; return input; }
                m.I0 = 0; m.F0 = 0f;
            }

            if (dist > _engage) input.MoveDir.X = dir;
            input.Jump       = to.Y < -24f || (dist > _engage + 10f && MathF.Abs(self.Velocity.X) < 15f);
            input.WantAttack = true;
            input.RequestedAction = dist <= _lungeMax ? 0 : null;
            return input;
        }
    }
}
