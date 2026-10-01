using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The reference package (Plans/FIGHTER_PACKAGE_GUIDE.md): a melee brawler with a brain
// that reads the opponent's tell and steps out of strikes it sees coming. Copy this file
// to start your own; keep everything in one file; never touch the shared ones.
public sealed class ExampleBrawler : IFighterPackage
{
    public string Name   => "ExampleBrawler";
    public string Author => "reference";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,     // the arena re-points this
        Health         = 4f,
        Strength       = 1.1f,
        Density        = 1.4f,
        ReactionFrames = 4,
        GroundPower    = 160f,
        JumpImpulse    = 330f,
        EnergyReserve  = 2f,
        EnergyRegen    = 0.6f,
        Color          = new Color(220, 190, 60),
        Actions        = { Melee(), Lunge() },
        EngageRange    = 26f,
        AlertRange     = 300f,
        Brain          = s => new BrawlerBrain(s),
    };

    private static ActionSpec Melee()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 1.1f; a.Reach = 24f; a.MaxRange = 34f;
        return a;
    }

    private static ActionSpec Lunge()
    {
        var a = ActionSpec.Default(ActionKind.Lunge);
        // MaxRange must sit under the lunge's effective reach (speed × active + hitbox
        // half-width + target half-width, scaled with the body): ≈ 84 px at this body,
        // so 80 — FighterCompiler refuses a band the dash cannot cover.
        a.MinRange = 40f; a.MaxRange = 80f;
        return a;
    }

    // Scratch: I0 = mode (0 hunt, 1 dodge), F0 = seconds in mode.
    private sealed class BrawlerBrain : FighterController
    {
        private readonly float _engage, _alert, _lungeCost, _lungeMax;
        public BrawlerBrain(FighterSpec s)
        {
            _engage = s.EngageRange; _alert = s.AlertRange;
            _lungeCost = s.Actions[1].EnergyCost; _lungeMax = s.Actions[1].MaxRange;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            // Buy the exact view (with the tell) whenever a lunge is still affordable
            // afterwards; otherwise fall back to the last bought view or the free one.
            EnemyTarget t; int age;
            if (self.Energy - s.TargetCost >= _lungeCost) t = s.Target(out age);
            else                                          t = s.TargetStale(out age);
            if (!t.Known || age > 12) t = s.TargetCoarse();

            var to     = t.Position - self.Position;
            float dist = to.Length();
            var input  = new EnemyInput { AimWorld = t.Position };
            if (dist > _alert) { m.I0 = 0; m.F0 = 0f; return input; }
            m.F0 += s.Dt;

            // A visible windup inside 70 px: step back for a third of a second.
            bool incoming = t.Known && t.TellProgress is >= 0.35f and < 1f
                            && t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash
                            && dist < 70f && !self.ActionCommitted;
            if (m.I0 == 0 && incoming) { m.I0 = 1; m.F0 = 0f; }
            if (m.I0 == 1)
            {
                if (m.F0 < 0.3f) { input.MoveDir.X = to.X >= 0f ? -1f : 1f; input.Jump = true; return input; }
                m.I0 = 0; m.F0 = 0f;
            }

            if (dist > _engage) input.MoveDir.X = to.X >= 0f ? 1f : -1f;
            input.Jump       = to.Y < -20f;
            input.WantAttack = true;
            // Lunge from mid range, swing up close; null lets the sim pick what passes.
            input.RequestedAction = dist >= 40f && dist <= _lungeMax ? 1 : dist <= 34f ? 0 : null;
            return input;
        }
    }
}
