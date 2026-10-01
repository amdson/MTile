using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Vanguard: an armoured brawler. Armor and health soak trades; the brain reads the
// opponent's tell only when it is close, backs out of windups it would otherwise eat,
// and punishes the recovery with a swing.
public sealed class Vanguard : IFighterPackage
{
    public string Name   => "Vanguard";
    public string Author => "sonnet-w1";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 5f,
        Strength       = 1.2f,
        Armor          = 1.2f,
        Density        = 1.0f,
        ReactionFrames = 5,
        GroundPower    = 120f,
        JumpImpulse    = 370f,
        EnergyReserve  = 2f,
        EnergyRegen    = 0.5f,
        Color          = new Color(90, 130, 200),
        Actions        = { Melee(), Lunge() },
        EngageRange    = 26f,
        AlertRange     = 320f,
        Brain          = s => new VanguardBrain(s),
    };

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 1.2f; a.Reach = 24f; a.MaxRange = 34f;
        a.Windup = 0.15f; a.Active = 0.15f; a.Recovery = 0.22f;
        return a;
    }

    private static ActionSpec Lunge()
    {
        var a = ActionSpec.Default(ActionKind.Lunge);
        a.MinRange = 45f; a.MaxRange = 80f;
        return a;
    }

    // Scratch: I0 = mode (0 hunt, 1 dodge), F0 = seconds in mode.
    private sealed class VanguardBrain : FighterController
    {
        private readonly float _engage, _alert;
        public VanguardBrain(FighterSpec s) { _engage = s.EngageRange; _alert = s.AlertRange; }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var c = s.TargetCoarse();
            float cd = (c.Position - self.Position).Length();
            EnemyTarget t; int age;
            bool want = cd < 110f || (cd < 260f && s.Frame % 4 == 0);
            if (want && self.Energy - s.TargetCost >= 0.55f) t = s.Target(out age);
            else t = s.TargetStale(out age);
            bool exact = t.Known && age <= 8;
            if (!exact) t = c;

            var to = t.Position - self.Position;
            float dist = to.Length();
            var input = new EnemyInput { AimWorld = t.Position };
            if (dist > _alert) { m.I0 = 0; m.F0 = 0f; return input; }
            m.F0 += s.Dt;

            bool incoming = exact && t.TellProgress >= 0.3f && t.TellProgress < 1f
                            && t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash
                            && dist < 80f && !self.ActionCommitted;
            // Jump a ball that is about to leave (aimed at where we stood at windup start).
            bool shot = exact && t.Tell == ActionKind.Ranged && t.TellProgress >= 0.55f;
            if (m.I0 == 0 && incoming) { m.I0 = 1; m.F0 = 0f; }
            if (m.I0 == 1)
            {
                if (m.F0 < 0.28f) { input.MoveDir.X = to.X >= 0f ? -1f : 1f; return input; }
                m.I0 = 0; m.F0 = 0f;
            }

            if (dist > _engage) input.MoveDir.X = to.X >= 0f ? 1f : -1f;
            input.Jump = to.Y < -20f || shot;
            input.WantAttack = true;
            input.RequestedAction = dist >= 48f && dist <= 80f ? 1 : dist <= 34f ? 0 : null;
            return input;
        }
    }
}
