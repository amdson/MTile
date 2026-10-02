using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Survive a heavy first touch, then hold a lethal body contact across a dash crossing.
// Health buys the reaction margin; the dash supplies mobility the heavy chassis lacks.
public sealed class Gravemarch : IFighterPackage
{
    public string Name => "Gravemarch";
    public string Author => "codex-w4";
    public FighterSpec Spec() => new()
    {
        Name = Name, Kind = EntityKind.FighterSlot0,
        Health = 12.2f, Strength = 1f, Density = 0.5f,
        GroundPower = 165f, JumpImpulse = 375f,
        EnergyReserve = 1.1f, EnergyRegen = 0.7f,
        ReactionFrames = 6, TargetMemory = true,
        EngageRange = 0f, AlertRange = 2000f,
        Color = new Color(70, 160, 175),
        Actions = { Hide(), Dash() }, Brain = _ => new MarchBrain(),
    };

    private static ActionSpec Hide()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage = 9.5f; a.Active = 0.6f; a.MaxRange = 90f;
        a.Knockback = Vector2.Zero;
        return a;
    }

    private static ActionSpec Dash()
    {
        var a = ActionSpec.Default(ActionKind.Dash);
        a.Speed = 1180f; a.Active = 0.10f; a.Intangible = 0.10f;
        return a;
    }

    private sealed class MarchBrain : FighterController
    {
        // F0 stall timer, F1 jump timer, F2 shot release, F3 target half-width.
        // I0 last dash request, I1 end of obstacle retreat.
        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            int age;
            var t = s.Frame % 5 == 0 && self.Energy > 0.1f
                ? s.Target(out age) : s.TargetStale(out age);
            if (!t.Known || age > 18) { t = coarse; age = 0; }
            if (!t.Known) return default;
            if (t.Bounds.Right > t.Bounds.Left) m.F3 = (t.Bounds.Right - t.Bounds.Left) * 0.5f;
            float width = m.F3 > 0f ? m.F3 : 15f;
            var pos = t.Position + t.Velocity * MathF.Min(age * s.Dt, 0.2f);
            var to = pos - self.Position;
            float ax = MathF.Abs(to.X), ay = MathF.Abs(to.Y);
            int dir = to.X >= 0f ? 1 : -1;
            float reach = self.Radius + 3f + width;
            bool ground = MathF.Abs(self.Velocity.Y) < 5f;
            bool free = !self.ActionCommitted && self.CurrentAction != ActionKind.Contact;
            var input = new EnemyInput { AimWorld = pos, MoveDir = new Vector2(dir, 0f), RequestedAction = 0 };

            if (s.Visible && t.TellProgress >= 0f && t.TellProgress < 1f &&
                t.Tell is ActionKind.Ranged or ActionKind.RailShot)
                m.F2 = s.Frame - age + (1f - t.TellProgress) * (t.Tell == ActionKind.RailShot ? 81f : 36f);
            float untilShot = m.F2 > 0f ? (m.F2 - s.Frame) / 60f : -99f;
            if (untilShot < -0.3f) m.F2 = 0f;

            m.F1 += s.Dt;
            bool stalled = !self.Staggered && MathF.Abs(self.Velocity.X) < 12f && ax > reach * 0.8f;
            m.F0 = stalled ? m.F0 + s.Dt : 0f;
            if (m.F0 > 2f) { m.I1 = s.Frame + 40; m.F0 = 0f; }
            bool retreat = s.Frame < m.I1;
            if (retreat) input.MoveDir.X = -dir;

            bool jump = m.F0 > 0.25f || (to.Y < -18f && ax < 90f);
            if (ground && s.Frame % 6 == 1 && ax > 25f && self.Energy > 0.12f)
            {
                int gx = (int)MathF.Floor((self.Position.X + dir * (self.Radius + 8f)) / Chunk.TileSize);
                int gy = (int)MathF.Floor((self.Position.Y + self.Radius - 4f) / Chunk.TileSize);
                jump |= s.Cell(gx, gy) == TileState.Solid;
            }
            bool shotNear = untilShot > -0.2f && untilShot < 0.15f;
            if (ground && jump && !shotNear && !retreat && m.F1 > 0.45f)
            { input.Jump = true; m.F1 = 0f; }

            float closing = MathF.Max(0f, (self.Velocity.X - t.Velocity.X) * dir);
            input.WantAttack = ax <= reach + 3f + closing * 0.14f && ay < self.Radius + 3f + width;
            bool catchUp = ax > reach + 8f && ax < 240f && ay < 28f;
            bool dodgeShot = untilShot > -0.05f && untilShot < 0.07f && ax > reach + 20f;
            if (free && ground && !input.Jump && !retreat && self.Energy >= 0.835f &&
                s.Frame - m.I0 > 35 && (catchUp || dodgeShot))
            {
                input.RequestedAction = 1; input.WantAttack = true; m.I0 = s.Frame;
            }
            return input;
        }
    }
}
