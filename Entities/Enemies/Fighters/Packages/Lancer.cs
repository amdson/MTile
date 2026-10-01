using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Lancer: a Cling crawler (60 px/s, anchored to any nearby surface) with a fast 500 px/s
// shot, a short-windup lash and a jab. It advances along the straight line to the target,
// shooting from range (its balls cancel the opponent's), and lashes or jabs once inside
// reach. Big health and a modest meter carry it through trades; the brain rations reads.
public sealed class Lancer : IFighterPackage
{
    public string Name   => "Lancer";
    public string Author => "sonnet-w2";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 8f,
        Strength       = 1.2f,
        Density        = 1.3f,
        ReactionFrames = 5,
        Cling          = true,
        EnergyReserve  = 6f,
        EnergyRegen    = 1.5f,
        Color          = new Color(120, 200, 230),
        Actions        = { LashAct(), Melee(), Shot() },
        EngageRange    = 20f,
        AlertRange     = 600f,
        Brain          = s => new LancerBrain(s),
    };

    private static ActionSpec LashAct()
    {
        var a = ActionSpec.Default(ActionKind.Lash);
        a.Windup = 0.12f; a.Recovery = 0.3f;
        a.MinRange = 18f; a.MaxRange = 50f; a.Reach = 52f;
        return a;
    }

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Windup = 0.25f; a.Recovery = 0.3f; a.MinRange = 60f; a.MaxRange = 400f; a.Speed = 500f;
        return a;
    }

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Windup = 0.10f; a.Recovery = 0.3f; a.Damage = 1.1f; a.Reach = 18f; a.MaxRange = 28f;
        return a;
    }

    private sealed class LancerBrain : FighterController
    {
        private readonly float _lashMax, _meleeMax, _shotCost, _shotMin, _shotMax;
        public LancerBrain(FighterSpec s) { _lashMax = s.Actions[0].MaxRange; _meleeMax = s.Actions[1].MaxRange;
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
            input.RequestedAction = d <= _meleeMax ? 1 : (d <= _lashMax ? 0 : (d >= _shotMin && d <= _shotMax && s.Visible && self.Energy >= _shotCost + 0.5f ? 2 : null));
            return input;
        }
    }
}
