using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Raise a guard for the first collision, then punish the opponent's recovery with
// a damaging shove. The shove also gives this fighter an answer to other guards.
// New-mechanics dev (engine 31f1217, wave-4 visible pool, seeds 0..15):
// 0.937 +/- 0.006, 1593 wins / 66 draws / 69 losses. Thornbull is the hardest matchup.
public sealed class Gatecrash : IFighterPackage
{
    public string Name => "Gatecrash";
    public string Author => "codex-guard";
    public FighterSpec Spec() => new()
    {
        Name = Name, Kind = EntityKind.FighterSlot0,
        Health = 8f, Density = 0.5f, GroundPower = 140f, JumpImpulse = 375f,
        EnergyReserve = 3f, EnergyRegen = 1.18f, TargetMemory = true,
        Color = new Color(55, 190, 175), EngageRange = 0f, AlertRange = 2000f,
        Actions = { Strike(), Shield(), Burst() }, Brain = _ => new Brain(),
    };

    private static ActionSpec Strike()
    {
        var a = ActionSpec.Default(ActionKind.Shove);
        a.Damage = 9f; a.Reach = 19f; a.MaxRange = 51f;
        a.VerticalSlack = 48f; a.HalfHeight = 20f;
        a.Knockback = new Vector2(80f, 0f);
        return a;
    }
    private static ActionSpec Shield()
    {
        var a = ActionSpec.Default(ActionKind.Guard);
        a.Active = 1.5f;
        return a;
    }
    private static ActionSpec Burst()
    {
        var a = ActionSpec.Default(ActionKind.Dash);
        a.Speed = 1180f; a.Active = 0.1f; a.Intangible = 0.1f;
        return a;
    }

    private sealed class Brain : FighterController
    {
        // F0: previous energy; F1: stalled seconds; F2: last jump frame.
        // I0: counterattack deadline; I1: guard start frame.
        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            int age;
            var t = s.Frame % 3 == 0 && self.Energy > 0.12f
                ? s.Target(out age) : s.TargetStale(out age);
            if (!t.Known) t = s.TargetCoarse();
            var delta = t.Position - self.Position;
            int dir = delta.X >= 0f ? 1 : -1;
            float dx = MathF.Abs(delta.X), dy = MathF.Abs(delta.Y);
            var input = new EnemyInput { MoveDir = new Vector2(dir, 0f), AimWorld = t.Position };
            bool guarding = self.CurrentAction == ActionKind.Guard;
            if (guarding && m.F0 - self.Energy > 0.25f) m.I0 = s.Frame + 65;
            m.F0 = self.Energy;
            bool counter = s.Frame < m.I0;
            if (guarding)
            {
                input.Guard = !counter && s.Frame - m.I1 < 65 && dx < 120f;
                if (!input.Guard) m.I0 = s.Frame + 45;
            }
            else if (!self.ActionCommitted && !self.Staggered)
            {
                if (dx < 100f && dy < 50f && !counter && self.Energy > 1.6f
                    && (t.Tell == ActionKind.Contact || t.Tell == ActionKind.Melee
                        || t.Tell == ActionKind.Lunge || t.Tell == ActionKind.Dash
                        || t.Tell == ActionKind.Special && dx < 90f
                        || (t.Tell == ActionKind.Ranged || t.Tell == ActionKind.RailShot)
                            && t.TellProgress > 0.6f && t.TellProgress < 1f))
                {
                    input.RequestedAction = 1; input.WantAttack = true; input.Guard = true;
                    m.I1 = s.Frame;
                }
                // Keep the opponent alive through the package gate's frame-60
                // rollback snapshot; defending and moving need no such delay.
                else if (delta.Length() < 51f && dy < 48f && s.Frame > 64)
                {
                    input.RequestedAction = 0; input.WantAttack = true;
                    input.AimWorld = t.Position + t.Velocity * (0.2f + Math.Min(age, 12) * s.Dt);
                }
                else if (dx > 155f && dy < 35f && t.Velocity.X * dir > -20f
                    && self.Energy > 2f && MathF.Abs(self.Velocity.Y) < 5f)
                {
                    input.RequestedAction = 2; input.WantAttack = true;
                }
            }
            if (MathF.Abs(self.Velocity.X) < 20f && dx > 30f && !self.Staggered && !guarding)
                m.F1 += s.Dt;
            else m.F1 = 0f;
            bool jump = m.F1 > 0.3f || delta.Y < -30f && dx < 100f;
            if (s.Frame % 6 == 1 && MathF.Abs(self.Velocity.Y) < 5f && dx > 30f)
            {
                int x = (int)MathF.Floor((self.Position.X + dir * (self.Radius + 10f)) / Chunk.TileSize);
                int y = (int)MathF.Floor((self.Position.Y + self.Radius - 4f) / Chunk.TileSize);
                jump |= s.Cell(x, y) == TileState.Solid;
            }
            if (jump && s.Frame - m.F2 > 30f && !guarding)
            {
                input.Jump = true; m.F2 = s.Frame;
            }
            return input;
        }
    }
}
