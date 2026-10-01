using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Wraith: the free-windup melee pushed to its ceiling. Near-zero windup and a short
// recovery, fresh reads, fast legs and a strong jump. The brain closes on the opponent
// and jumps on shooter tells so a bolt aimed at the windup-start position passes under.
public sealed class Wraith : IFighterPackage
{
    public string Name   => "Wraith";
    public string Author => "sonnet-w2";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 4f,
        Strength       = 1.0f,
        Density        = 1.4f,
        ReactionFrames = 1,
        GroundPower    = 310f,
        JumpImpulse    = 520f,
        EnergyReserve  = 3f,
        EnergyRegen    = 0.8f,
        Color          = new Color(150, 110, 220),
        Actions        = { Melee() },
        EngageRange    = 24f,
        AlertRange     = 600f,
        Brain          = s => new WraithBrain(s),
    };

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Windup = 0.02f; a.Active = 0.12f; a.Recovery = 0.18f;
        a.Damage = 4.2f; a.Reach = 22f; a.MaxRange = 32f;
        return a;
    }

    private sealed class WraithBrain : FighterController
    {
        private readonly float _melee;
        public WraithBrain(FighterSpec s) { _melee = s.Actions[0].MaxRange; }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = coarse.Known ? (coarse.Position - self.Position).Length() : 9999f;
            EnemyTarget t; int age;
            int cadence = cd < 330f ? 1 : 6;
            if (s.Frame % cadence == 0 && self.Energy - s.TargetCost >= 0f) t = s.Target(out age);
            else t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = coarse; age = 0; }

            var pos = t.Position + t.Velocity * (age * s.Dt);
            var to = pos - self.Position;
            float dist = to.Length();
            float dx = to.X >= 0f ? 1f : -1f;
            var input = new EnemyInput { AimWorld = pos };
            if (dist > 700f) return input;

            bool lost = !s.Visible && s.HiddenSeconds > 0.3f;
            bool shot = t.Known && t.TellProgress >= 0f && t.TellProgress < 1f
                        && t.Tell is ActionKind.Ranged or ActionKind.RailShot;
            bool rail = t.Tell == ActionKind.RailShot;
            // A ball is aimed where we stand when the windup ENDS: launch just before that.
            float remain = (1f - t.TellProgress) * (rail ? 0.3f : 0.4f) - age * s.Dt;
            bool grounded = MathF.Abs(self.Velocity.Y) < 40f;

            // F0: seconds spent stalled (wanting to move, not moving).
            bool stalled = grounded && MathF.Abs(self.Velocity.X) < 8f && dist > 50f;
            m.F0 = stalled ? m.F0 + s.Dt : 0f;
            input.MoveDir.X = dist > 20f ? dx : 0f;
            input.Jump = (shot && remain < 0.035f && grounded)
                         || (to.Y < -40f && grounded)
                         || m.F0 > 0.3f;
            input.WantAttack = true;
            input.RequestedAction = dist <= _melee ? 0 : null;
            return input;
        }
    }
}
