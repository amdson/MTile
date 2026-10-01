using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Harrier: a fast kiter. Keeps outside melee reach using the opponent's velocity, fires
// energy balls while it walks, jumps over a closing opponent, and sidesteps strikes it
// sees coming.
public sealed class Harrier : IFighterPackage
{
    public string Name   => "Harrier";
    public string Author => "sonnet-w1";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 4f,
        Density        = 1.5f,
        ReactionFrames = 4,
        GroundPower    = 200f,
        JumpImpulse    = 400f,
        EnergyReserve  = 4f,
        EnergyRegen    = 1.2f,
        Color          = new Color(90, 200, 160),
        Actions        = { Shot(), Jab() },
        EngageRange    = 20f,
        AlertRange     = 400f,
        Brain          = s => new HarrierBrain(s),
    };

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Windup = 0.40f; a.Recovery = 0.35f;
        a.MinRange = 50f; a.MaxRange = 320f;
        return a;
    }

    private static ActionSpec Jab()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 0.8f; a.Reach = 18f; a.MaxRange = 28f;
        return a;
    }

    // Scratch: I0 = mode (0 kite, 1 sidestep), F0 = seconds in mode.
    private sealed class HarrierBrain : FighterController
    {
        private readonly float _shotCost, _shotMin, _shotMax;
        public HarrierBrain(FighterSpec s)
        {
            _shotCost = s.Actions[0].EnergyCost;
            _shotMin = s.Actions[0].MinRange; _shotMax = s.Actions[0].MaxRange;
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

            // Lead: where the target will be after its reading delay.
            var pos  = t.Position + t.Velocity * (age * s.Dt);
            var to   = pos - self.Position;
            float dist = to.Length();
            float dx = to.X >= 0f ? 1f : -1f;
            var input = new EnemyInput { AimWorld = pos };
            m.F0 += s.Dt;

            if (t.Known && t.TellProgress >= 0f && t.Tell is ActionKind.Ranged or ActionKind.RailShot) m.I1 = 1;
            // Out of sight (a ledge between us): go and find it, hopping any wall.
            if (!s.Visible && s.HiddenSeconds > 0.4f && dist > 40f)
            {
                input.MoveDir.X = dx;
                input.Jump = MathF.Abs(self.Velocity.X) < 8f;
                input.WantAttack = false;
                return input;
            }
            if (m.I1 == 1)
            {
                // Shooter opponent: rush it and hop its shots (aimed at windup start).
                bool rail = t.Tell == ActionKind.RailShot;
                float tArr = (1f - t.TellProgress) * (rail ? 1.35f : 0.6f) + dist / (rail ? 1500f : 450f)
                             - age * s.Dt;
                bool shotComing = t.Known && t.TellProgress >= 0f && t.TellProgress < 1f
                                  && t.Tell is ActionKind.Ranged or ActionKind.RailShot
                                  && tArr is > 0.2f and < 0.5f;
                if (dist > 22f) input.MoveDir.X = dx;
                input.Jump = (shotComing && MathF.Abs(self.Velocity.Y) < 40f) || to.Y < -40f;
                input.WantAttack = true;
                input.RequestedAction = dist <= 28f ? 1 : (dist > 170f && dist <= _shotMax ? 0 : null);
                return input;
            }

            bool incoming = t.Known && t.TellProgress >= 0.2f && t.TellProgress < 1f
                            && (t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash)
                            && dist < 90f;
            if (m.I0 == 0 && incoming) { m.I0 = 1; m.F0 = 0f; }
            if (m.I0 == 1)
            {
                if (m.F0 < 0.35f) { input.MoveDir.X = -dx; input.Jump = dist < 60f; input.WantAttack = false; return input; }
                m.I0 = 0; m.F0 = 0f;
            }

            // Closing speed toward us decides how much room we want.
            float closing = -(t.Velocity.X * dx);
            float want = 140f + MathF.Max(0f, closing) * 0.6f;
            if (dist < want) input.MoveDir.X = -dx;
            else if (dist > 230f) input.MoveDir.X = dx;
            // hop over a body that has closed in
            input.Jump = dist < 45f;
            input.WantAttack = s.Visible || s.HiddenSeconds < 1f;
            input.RequestedAction = dist <= 28f ? 1 : (dist >= _shotMin && dist <= _shotMax ? 0 : null);
            return input;
        }
    }
}
