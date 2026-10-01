using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Phalanx: a mobile shooter with a fast ball. Holds a standoff, fires a quick high-speed
// shot (too fast to hop on a Harrier-style read), jumps incoming shots by arrival time,
// and rushes shooters/turrets because projectiles are 1D and close range is safe.
public sealed class Phalanx : IFighterPackage
{
    public string Name   => "Phalanx";
    public string Author => "sonnet-w2";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 6f,
        Strength       = 1.3f,
        Density        = 1.5f,
        ReactionFrames = 3,
        GroundPower    = 200f,
        JumpImpulse    = 400f,
        EnergyReserve  = 6f,
        EnergyRegen    = 1.5f,
        Color          = new Color(120, 120, 220),
        Actions        = { Shot(), Jab() },
        EngageRange    = 20f,
        AlertRange     = 500f,
        Brain          = s => new PhalanxBrain(s),
    };

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Speed = 800f; a.Damage = 1f; a.Windup = 0.25f; a.Recovery = 0.3f;
        a.MinRange = 50f; a.MaxRange = 420f;
        return a;
    }

    private static ActionSpec Jab()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 0.8f; a.Reach = 18f; a.MaxRange = 28f;
        return a;
    }

    // Scratch: I0 = mode (0 kite, 1 sidestep), F0 = seconds in mode, I1 = 1 once a shooter was seen.
    private sealed class PhalanxBrain : FighterController
    {
        private readonly float _shotCost, _shotMin, _shotMax, _shotSpeed;
        public PhalanxBrain(FighterSpec s)
        {
            _shotCost = s.Actions[0].EnergyCost;
            _shotMin = s.Actions[0].MinRange; _shotMax = s.Actions[0].MaxRange;
            _shotSpeed = s.Actions[0].Speed;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            EnemyTarget t; int age;
            var coarse = s.TargetCoarse();
            float cd = (coarse.Position - self.Position).Length();
            int cadence = cd < 140f ? 3 : 6;
            if (s.Frame % cadence == 0 && self.Energy - s.TargetCost >= 0.3f) t = s.Target(out age);
            else                                                              t = s.TargetStale(out age);
            if (!t.Known || age > 30) { t = coarse; age = 0; }

            var pos  = t.Position + t.Velocity * (age * s.Dt);
            var to   = pos - self.Position;
            float dist = to.Length();
            float dx = to.X >= 0f ? 1f : -1f;
            var input = new EnemyInput { AimWorld = pos };
            m.F0 += s.Dt;

            if (t.Known && t.TellProgress >= 0f && t.Tell is ActionKind.Ranged or ActionKind.RailShot) m.I1 = 1;
            if (!s.Visible && s.HiddenSeconds > 0.4f && dist > 40f)
            {
                input.MoveDir.X = dx;
                input.Jump = MathF.Abs(self.Velocity.X) < 8f;
                return input;
            }
            if (m.I1 == 1)
            {
                bool rail = t.Tell == ActionKind.RailShot;
                float tArr = (1f - t.TellProgress) * (rail ? 1.35f : 0.6f) + dist / (rail ? 1500f : 450f)
                             - age * s.Dt;
                bool shotComing = t.Known && t.TellProgress >= 0f && t.TellProgress < 1f
                                  && t.Tell is ActionKind.Ranged or ActionKind.RailShot
                                  && tArr is > 0.2f and < 0.5f;
                if (dist > 22f) input.MoveDir.X = dx;
                input.Jump = (shotComing && MathF.Abs(self.Velocity.Y) < 40f) || to.Y < -40f;
                input.WantAttack = true;
                input.RequestedAction = dist <= 28f ? 1 : (dist > 120f && dist <= _shotMax ? 0 : null);
                return input;
            }

            bool incoming = t.Known && t.TellProgress >= 0.2f && t.TellProgress < 1f
                            && (t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash)
                            && dist < 90f;
            if (m.I0 == 0 && incoming) { m.I0 = 1; m.F0 = 0f; }
            if (m.I0 == 1)
            {
                if (m.F0 < 0.35f) { input.MoveDir.X = -dx; input.Jump = dist < 60f; return input; }
                m.I0 = 0; m.F0 = 0f;
            }

            float closing = -(t.Velocity.X * dx);
            float want = 140f + MathF.Max(0f, closing) * 0.6f;
            if (dist < want) input.MoveDir.X = -dx;
            else if (dist > 260f) input.MoveDir.X = dx;
            input.Jump = dist < 45f;
            input.WantAttack = s.Visible || s.HiddenSeconds < 1f;
            input.RequestedAction = dist <= 28f ? 1 : (dist >= _shotMin && dist <= _shotMax ? 0 : null);
            return input;
        }
    }
}
