using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Sentinel: a rooted rail turret. The Rooted refund pays for the rail's points and target
// memory; the mass goes into reserve and regen. The rail's windup is shortened so the
// aim lock (taken at windup start) is stale for less time, and the brain leads the
// target's velocity across that windup. A short melee covers anything that closes in.
public sealed class Sentinel : IFighterPackage
{
    public string Name   => "Sentinel";
    public string Author => "sonnet-w1";

    private const float Windup = 0.3f;

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Rooted         = true,
        TargetMemory   = true,
        Health         = 7f,
        Strength       = 1.3f,
        Density        = 1.0f,
        ReactionFrames = 5,
        EnergyReserve  = 14f,
        EnergyRegen    = 2.0f,
        Color          = new Color(200, 80, 60),
        Actions        = { Rail(), Melee() },
        EngageRange    = 26f,
        AlertRange     = 560f,
        Brain          = s => new SentinelBrain(s),
    };

    private static ActionSpec Rail()
    {
        var a = ActionSpec.Default(ActionKind.RailShot);
        a.Windup = Windup; a.Recovery = 0.3f;
        a.MinRange = 0f; a.MaxRange = 560f;
        return a;
    }

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 1.0f; a.Reach = 24f; a.MaxRange = 34f;
        return a;
    }

    private sealed class SentinelBrain : FighterController
    {
        private readonly float _railCost, _railMin, _railMax, _meleeMax, _speed;
        public SentinelBrain(FighterSpec s)
        {
            var r = s.Actions[0];
            _railCost = r.EnergyCost; _railMin = r.MinRange; _railMax = r.MaxRange; _speed = r.Speed;
            _meleeMax = s.Actions[1].MaxRange;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            EnemyTarget t; int age;
            // Exact reads only when the rail stays affordable; otherwise the last bought view.
            if (self.Energy - s.TargetCost >= _railCost) t = s.Target(out age);
            else                                          t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = s.TargetCoarse(); age = 20; }

            var to   = t.Position - self.Position;
            float d  = to.Length();
            var input = new EnemyInput { AimWorld = t.Position };
            if (d > 600f) return input;

            // Lead: velocity carried across read age + windup + bolt flight. Vertical lead only
            // when airborne-looking (large |vy|); ground targets jitter vy to zero anyway.
            if (age < 20 && !self.ActionCommitted)
            {
                float lead = age * s.Dt + Windup + d / _speed;
                var v = t.Velocity;
                input.AimWorld = t.Position + new Vector2(v.X * lead, v.Y * lead * 0.5f);
            }

            input.WantAttack = true;
            if (d <= _meleeMax) input.RequestedAction = 1;
            else if (s.Visible && d >= _railMin && d <= _railMax && self.Energy >= _railCost)
                input.RequestedAction = 0;
            else input.WantAttack = false;
            return input;
        }
    }
}
