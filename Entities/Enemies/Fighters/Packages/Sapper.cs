using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Sapper: a dense, fast rusher with a quick heavy melee and a brick-layer's trowel.
// It hops incoming bolts, and closes on shooters.
public sealed class Sapper : IFighterPackage
{
    public string Name   => "Sapper";
    public string Author => "sonnet-w2";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 4f,
        Strength       = 1.6f,
        Density        = 2.0f,
        ReactionFrames = 5,
        GroundPower    = 340f,
        JumpImpulse    = 500f,
        EnergyReserve  = 2.5f,
        EnergyRegen    = 0.6f,
        Color          = new Color(150, 120, 70),
        Actions        = { Melee(), ActionSpec.Default(ActionKind.PlaceBlock) },
        EngageRange    = 44f,
        AlertRange     = 600f,
        Brain          = s => new SapperBrain(s),
    };

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Windup = 0.06f; a.Recovery = 0.30f;
        a.Damage = 1.0f; a.Reach = 42f; a.MaxRange = 50f;
        return a;
    }

    // Scratch: I0 = mode, F0 = seconds in mode.
    private sealed class SapperBrain : FighterController
    {
        private readonly float _meleeMax, _wallCost;
        public SapperBrain(FighterSpec s)
        {
            _meleeMax = s.Actions[0].MaxRange;
            _wallCost = s.Actions[1].EnergyCost;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = (coarse.Position - self.Position).Length();
            EnemyTarget t; int age;
            int cadence = cd < 140f ? 3 : 6;
            var stale = s.TargetStale(out int sage);
            if (stale.Known && sage < 14 && stale.TellProgress >= 0.3f && stale.Tell is ActionKind.Ranged)
                cadence = 1;
            if (s.Frame % cadence == 0 && self.Energy - s.TargetCost >= 0.2f) t = s.Target(out age);
            else t = s.TargetStale(out age);
            if (!t.Known || age > 30) { t = coarse; age = 0; }

            var pos  = t.Position + t.Velocity * (age * s.Dt);
            var to   = pos - self.Position;
            float dist = to.Length();
            float dx = to.X >= 0f ? 1f : -1f;
            var input = new EnemyInput { AimWorld = pos };

            input.MoveDir.X = dist > 40f ? dx : 0f;
            bool grounded = MathF.Abs(self.Velocity.Y) < 30f;

            bool shot = t.Known && s.Visible && t.TellProgress >= 0f
                        && t.Tell is ActionKind.Ranged or ActionKind.RailShot;
            if (shot)
            {
                bool rail = t.Tell == ActionKind.RailShot;
                float tArr = (1f - t.TellProgress) * (rail ? 0.3f : 0.45f) + dist / (rail ? 1500f : 450f)
                             - age * s.Dt;
                if (rail)
                {
                    if (tArr is > 0.16f and < 0.60f && grounded) input.Jump = true;
                }
                else
                {
                    // The ball is aimed at where we stand when it LEAVES, so leave the ground
                    // as it does: rise fast through the flight time.
                    float remaining = (1f - t.TellProgress) * 0.4f - age * s.Dt;
                    if (dist > 150f && remaining < 0.06f && grounded) input.Jump = true;
                }
            }
            else if (!s.Visible && MathF.Abs(self.Velocity.X) < 8f) input.Jump = true;
            else if (to.Y < -30f && grounded && MathF.Abs(self.Velocity.X) < 12f && s.Frame % 4 == 0) input.Jump = true;

            input.WantAttack = true;
            input.RequestedAction = dist <= _meleeMax ? 0 : null;
            if (dist > _meleeMax) input.WantAttack = false;
            return input;
        }
    }
}
