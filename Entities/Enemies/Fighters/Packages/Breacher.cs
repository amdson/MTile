using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Breacher: a fast, durable closer built to kill rooted shooters. It always advances,
// reads the shooter's tell, and jumps so a bolt aimed at windup start passes under it.
// At point blank a 0.1 s melee finishes the job.
public sealed class Breacher : IFighterPackage
{
    public string Name   => "Breacher";
    public string Author => "sonnet-w2";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 6f,
        Strength       = 2.0f,
        Density        = 1.3f,
        ReactionFrames = 4,
        GroundPower    = 230f,
        JumpImpulse    = 520f,
        EnergyReserve  = 2f,
        EnergyRegen    = 0.8f,
        Color          = new Color(240, 160, 40),
        Actions        = { Melee() },
        EngageRange    = 24f,
        AlertRange     = 600f,
        Brain          = s => new BreacherBrain(s),
    };

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Windup = 0.10f; a.Recovery = 0.30f;
        a.Damage = 1.0f; a.Reach = 22f; a.MaxRange = 34f;
        return a;
    }

    // Scratch: I0 = 1 once a shooter tell has been seen, F0 = seconds since last jump.
    private sealed class BreacherBrain : FighterController
    {
        private readonly float _engage, _meleeMax;
        public BreacherBrain(FighterSpec s) { _engage = s.EngageRange; _meleeMax = s.Actions[0].MaxRange; }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = coarse.Known ? (coarse.Position - self.Position).Length() : 9999f;
            EnemyTarget t; int age;
            int cadence = cd < 200f ? 2 : 4;
            if (s.Frame % cadence == 0 && self.Energy - s.TargetCost >= 0.1f) t = s.Target(out age);
            else t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = coarse; age = 0; }

            var pos  = t.Position + t.Velocity * (age * s.Dt);
            var to   = pos - self.Position;
            float dist = to.Length();
            float dx = to.X >= 0f ? 1f : -1f;
            var input = new EnemyInput { AimWorld = pos };
            m.F0 += s.Dt;

            bool shooter = t.Known && t.TellProgress >= 0f && t.Tell is ActionKind.Ranged or ActionKind.RailShot;
            if (shooter) m.I0 = 1;

            // Rail: the bolt is instant at windup end and aimed at where we stood at windup
            // start, so leave the ground right after the lock. Ranged: the ball is aimed at
            // fire time and flies for a while, so lift off just before it fires.
            bool wantJump = false;
            if (shooter && dist > 40f && (m.I1 == 0 || m.F0 > 1.0f) && MathF.Abs(self.Velocity.Y) < 40f)
            {
                float lo = t.Tell == ActionKind.RailShot ? 0f : 0.6f;
                float hi = t.Tell == ActionKind.RailShot ? 0.5f : 1f;
                wantJump = t.TellProgress >= lo && t.TellProgress < hi;
            }
            if (self.Velocity.Y < -150f) { m.F0 = 0f; m.I1 = 1; }

            if (!s.Visible && s.HiddenSeconds > 0.3f && dist > 40f)
            {
                input.MoveDir.X = dx;
                input.Jump = MathF.Abs(self.Velocity.X) < 8f;
                return input;
            }

            if (dist > _engage) input.MoveDir.X = dx;
            input.Jump = wantJump || to.Y < -30f || (s.Frame > 30 && dist > _engage + 10f && MathF.Abs(self.Velocity.X) < 15f && self.Velocity.Y > -5f);
            input.WantAttack = true;
            input.RequestedAction = dist <= _meleeMax ? 0 : null;
            return input;
        }
    }
}
