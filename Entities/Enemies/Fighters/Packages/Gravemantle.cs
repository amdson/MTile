using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Broad contact coverage intercepts rushers before their small contact boxes reach us.
// Two mass-budgeted contact actions alternate using the published priority-preemption loophole.
// Each keeps the compiler-fixed timings; selecting the other interrupts recovery.
public sealed class Gravemantle : IFighterPackage
{
    public string Name => "Gravemantle";
    public string Author => "codex-w3";
    public FighterSpec Spec() => new()
    {
        Name = Name, Kind = EntityKind.FighterSlot0,
        Health = 7f, Density = 0.5f, Strength = 1f,
        GroundPower = 160f, JumpImpulse = 500f,
        EnergyReserve = 1f, EnergyRegen = 0.5f,
        TargetMemory = true, ReactionFrames = 6,
        EngageRange = 20f, AlertRange = 2000f,
        Color = new Color(65, 180, 160),
        Actions = { Mantle(), Mantle() }, Brain = _ => new MantleBrain(),
    };

    private static ActionSpec Mantle()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage = 0.35f;
        a.HalfWidth = 280f;
        a.MaxRange = 900f;
        a.ActivePriority = 12;
        a.PassivePriority = 13;
        a.Active = 0.2f;
        // Keep targets inside the damage field instead of launching them out of reach.
        a.Knockback = Vector2.Zero;
        return a;
    }

    private sealed class MantleBrain : FighterController
    {
        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            int age;
            var t = s.Frame % 4 == 0 && self.Energy >= s.TargetCost
                ? s.Target(out age) : s.TargetStale(out age);
            if (!t.Known || age > 16) { t = s.TargetCoarse(); age = 0; }
            if (!t.Known) return default;
            var target = t.Position + t.Velocity * MathF.Min(age * s.Dt, 0.2f);
            var delta = target - self.Position;
            float reach = 280f * self.Radius / 12f;
            bool grounded = MathF.Abs(self.Velocity.Y) < 12f;
            bool opening = s.Frame < 65 && self.Health >= self.MaxHealth;
            bool approach = MathF.Abs(delta.X) > reach * 0.7f;
            bool retreat = opening || MathF.Abs(delta.X) < 100f;
            m.F0 = approach && grounded && MathF.Abs(self.Velocity.X) < 8f ? m.F0 + s.Dt : 0f;
            return new EnemyInput
            {
                AimWorld = target,
                MoveDir = new Vector2(retreat ? -MathF.Sign(delta.X) : approach ? MathF.Sign(delta.X) : 0f, 0f),
                Jump = grounded && (m.F0 > 0.25f || delta.Y < -reach * 0.7f),
                // Opening spacing also avoids the published frame-60 restore defect.
                WantAttack = !opening, RequestedAction = s.Frame % 2,
            };
        }
    }
}
