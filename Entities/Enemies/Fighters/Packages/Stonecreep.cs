using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Stonecreep: a dense (small, so it stays within the cling anchor distance) Cling crawler
// with a 500 px/s shot, a lash and a jab, and as much health as the mass budget leaves.
// It plants and shoots from range, crawls toward a target it cannot see or reach, and
// lashes or jabs inside reach. Against a shooter it phases its windup so its ball is in
// flight while the opponent's is not (balls of opposing teams cancel).
public sealed class Stonecreep : IFighterPackage
{
    public string Name   => "Stonecreep";
    public string Author => "sonnet-w3";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 9.5f,
        Strength       = 1.2f,
        Density        = 1.7f,
        ReactionFrames = 5,
        Cling          = true,
        EnergyReserve  = 6f,
        EnergyRegen    = 1.5f,
        Color          = new Color(120, 200, 230),
        Actions        = { LashAct(), Melee(), Shot() },
        EngageRange    = 20f,
        AlertRange     = 600f,
        Brain          = s => new StonecreepBrain(s),
    };

    private static ActionSpec LashAct()
    {
        var a = ActionSpec.Default(ActionKind.Lash);
        a.Windup = 0.12f; a.Recovery = 0.3f;
        a.MinRange = 18f; a.MaxRange = 50f; a.Reach = 52f;
        return a;
    }

    private const float ShotSpeed = 500f;
    private const float Gate = 0.35f;

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Windup = 0.25f; a.Recovery = 0.3f; a.MinRange = 60f; a.MaxRange = 400f; a.Speed = ShotSpeed;
        return a;
    }

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Windup = 0.10f; a.Recovery = 0.3f; a.Damage = 1.1f; a.Reach = 18f; a.MaxRange = 28f;
        return a;
    }

    private sealed class StonecreepBrain : FighterController
    {
        private readonly float _lashMax, _meleeMax, _shotCost, _shotMin, _shotMax;
        public StonecreepBrain(FighterSpec s) { _lashMax = s.Actions[0].MaxRange; _meleeMax = s.Actions[1].MaxRange;
            _shotCost = s.Actions[2].EnergyCost; _shotMin = s.Actions[2].MinRange; _shotMax = s.Actions[2].MaxRange; }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            EnemyTarget t; int age;
            if (self.Energy - s.TargetCost >= 0.1f && s.Frame % 3 == 0) t = s.Target(out age);
            else t = s.TargetStale(out age);
            if (!t.Known || age > 12) { t = coarse; age = 0; }
            var pos = t.Position + t.Velocity * (age * s.Dt);
            var to = pos - self.Position;
            float d = to.Length();
            var input = new EnemyInput { AimWorld = pos };
            input.MoveDir = d > 1f ? to / d : Vector2.Zero;
            input.WantAttack = true;
            // Phase against a shooter: my ball launches 0.6 s after I start, so begin only
            // once their windup is past Gate; then my ball flies while theirs is not and
            // the two do not cancel in mid-air.
            bool hold = t.Known && age <= 12 && t.Tell == ActionKind.Ranged && t.TellProgress >= 0f && t.TellProgress < Gate && !self.ActionCommitted;
            input.RequestedAction = d <= _meleeMax ? 1 : (d <= _lashMax ? 0 : (d >= _shotMin && d <= _shotMax && s.Visible && !hold && self.Energy >= _shotCost + 0.5f ? 2 : null));
            return input;
        }
    }
}
