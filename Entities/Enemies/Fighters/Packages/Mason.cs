using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Mason: a wall-builder kiter. Shoots from range, and when a melee opponent closes it
// lays a dirt wall between them (line of sight is mandatory, so the wall blinds them),
// backing off between placements. Big reserve and regen pay for shots and bricks.
public sealed class Mason : IFighterPackage
{
    public string Name   => "Mason";
    public string Author => "sonnet-w1";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 3f,
        Density        = 1.5f,
        ReactionFrames = 6,
        GroundPower    = 120f,
        JumpImpulse    = 350f,
        EnergyReserve  = 10f,
        EnergyRegen    = 1.0f,
        Color          = new Color(190, 140, 80),
        Actions        =
        {
            Shot(),                                         // 0
            ActionSpec.Default(ActionKind.PlaceBlock),      // 1
            ActionSpec.Default(ActionKind.SpawnBlockInAir), // 2
        },
        EngageRange     = 180f,
        StandoffRange   = 100f,
        AlertRange      = 320f,
        Brain           = s => new MasonBrain(s),
    };

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Damage = 1.0f; a.Speed = 500f; a.Windup = 0.35f; a.Recovery = 0.25f;
        return a;
    }

    // Scratch: I0 = 1 while backing off, F0 = seconds in that leg, I1 = placement phase.
    private sealed class MasonBrain : FighterController
    {
        private readonly float _engage, _stand, _alert, _shotCost, _wallCost, _shotMin, _shotMax;
        public MasonBrain(FighterSpec s)
        {
            _engage = s.EngageRange; _stand = s.StandoffRange; _alert = s.AlertRange;
            _shotCost = s.Actions[0].EnergyCost; _wallCost = s.Actions[1].EnergyCost;
            _shotMin = s.Actions[0].MinRange; _shotMax = s.Actions[0].MaxRange;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            EnemyTarget t;
            int age;
            if (self.Energy - s.TargetCost >= _shotCost + _wallCost && (s.Frame % 3) == 0) t = s.Target(out age);
            else t = s.TargetStale(out age);
            if (!t.Known || age > 14) t = s.TargetCoarse();

            var to     = t.Position - self.Position;
            float dist = to.Length();
            var input  = new EnemyInput { AimWorld = t.Position };
            if (dist > _alert) { m.I0 = 0; m.F0 = 0f; return input; }

            float dx = to.X >= 0f ? 1f : -1f;
            if (m.I0 == 1)
            {
                m.F0 += s.Dt;
                if (m.F0 >= 0.4f && dist >= _stand) { m.I0 = 0; m.F0 = 0f; }
            }
            else if (dist < _stand) { m.I0 = 1; m.F0 = 0f; }

            if (m.I0 == 1) input.MoveDir.X = -dx;
            else if (dist > _engage) input.MoveDir.X = dx;
            input.Jump = (m.I0 == 0 && to.Y < -20f);

            // A projectile tell: it was aimed when the windup began, so leave the line.
            bool proj = t.Known && age <= 8 && t.Tell is ActionKind.RailShot or ActionKind.Ranged
                        && t.TellProgress >= 0.55f && t.TellProgress <= 1f;
            if (proj) input.Jump = true;

            input.WantAttack = s.Visible;
            int? req = null;
            if (dist < 110f && dist >= 40f && self.Energy >= _wallCost + _shotCost) req = 1;
            else if (dist >= _shotMin && dist <= _shotMax) req = 0;
            input.RequestedAction = req;
            return input;
        }
    }
}
