using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Wardstep: an exhibition bot by the coordinator, built to show Guard in the dash-rammer
// meta after wave 4. Not a competition entry: its author saw every report.
//
// A rammer's touch has no tell and, with a dash, no warning distance either — but it has
// a long cooldown. So Wardstep never tries to out-run it:
//   * anything that could reach it soon (a charger, or anything within a dash of it) meets
//     a raised guard facing it; the touch is blocked for energy;
//   * the touch's cooldown is timed from the tell (Contact, seen first on frame C): the
//     guard comes down once the touch can no longer be live and a heavy swing lands inside
//     the cooldown — the hit staggers it, and the guard goes back up;
//   * a shooter's ball is blocked the same way; Wardstep walks in behind the guard and
//     swings at turrets; a long untouchable dash crosses the last stretch.
public sealed class Wardstep : IFighterPackage
{
    public string Name   => "Wardstep";
    public string Author => "coordinator-x";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 5f,
        Strength       = 1.5f,
        Density        = 1.0f,
        ReactionFrames = 3,
        GroundPower    = 240f,
        JumpImpulse    = 400f,
        EnergyReserve  = 5f,
        EnergyRegen    = 2.2f,
        Color          = new Color(120, 170, 220),
        Actions        = { Guard(), Swing(), Dash() },
        EngageRange    = 18f,
        AlertRange     = 2000f,
        Brain          = s => new WardBrain(s),
    };

    private static ActionSpec Guard()
    {
        var a = ActionSpec.Default(ActionKind.Guard);
        a.Active = 3f;                       // the longest hold: a slow approach must not outlast it
        return a;
    }

    private static ActionSpec Swing()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        // A heavy push (paid for): a staggered rammer must land out of reach of a touch
        // for the whole of this swing's recovery.
        a.Damage = 2.6f; a.Reach = 26f; a.MaxRange = 36f; a.Knockback = new Vector2(550f, -180f);
        return a;
    }

    private static ActionSpec Dash()
    {
        var a = ActionSpec.Default(ActionKind.Dash);
        a.Speed = 260f; a.Active = 0.45f; a.Intangible = 0.45f;
        return a;
    }

    private const int GuardIdx = 0, SwingIdx = 1, DashIdx = 2;

    // Scratch: F0 = seconds since the last hop, F1 = frame of our last block (0 = none),
    // F2 = seconds stalled, F3 = frame its ball leaves (0 = none), I0 = frames to the next
    // exact read, I1 = bit 0: the target has shown a touch or a charge; bits 1+: our energy
    // last frame, in thousandths (a sudden drop while guarding is a block).
    private sealed class WardBrain : FighterController
    {
        private readonly float _swingMax, _dashCost;
        public WardBrain(FighterSpec s) { _swingMax = s.Actions[SwingIdx].MaxRange; _dashCost = s.Actions[DashIdx].EnergyCost; }

        private const float DashThreat   = 190f;   // a rammer this close can be on us in one dash
        private const float ChargeSpeed  = 60f;
        private const float BallSpeed    = 480f;
        private const float TouchCool    = 0.85f;  // Contact's fixed recovery
        private static readonly float SwingWindup = ActionSpec.Default(ActionKind.Melee).Windup;
        private static readonly float GuardLower  = ActionSpec.Default(ActionKind.Guard).Recovery;

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = (coarse.Position - self.Position).Length();
            EnemyTarget t; int age;
            int period = cd < 220f ? 2 : 5;
            if (m.I0 <= 0 && self.Energy >= s.TargetCost + 0.1f) { t = s.Target(out age); m.I0 = period; }
            else { t = s.TargetStale(out age); m.I0--; }
            bool exact = t.Known && age <= 10;
            if (!exact) { t = coarse; age = 6; }

            var pos = t.Position + (exact ? t.Velocity * (age * s.Dt) : Vector2.Zero);
            var to = pos - self.Position;
            float d = to.Length();
            float sx = to.X >= 0f ? 1f : -1f;
            float closing = exact ? -t.Velocity.X * sx : 0f;
            bool guarding = self.CurrentAction == ActionKind.Guard;
            bool idle = !self.ActionCommitted && !guarding;
            bool grounded = MathF.Abs(self.Velocity.Y) < 1f;
            float frame = s.Frame;
            m.F0 += s.Dt;

            // A block: the meter dropped by more than the hold's drain while the guard was up.
            // That touch cannot hit again (hits dedupe per attack), and its cooldown has
            // started: lowering + a swing's windup lands inside it.
            float lastEnergy = (m.I1 >> 1) / 1000f;
            if (guarding && lastEnergy - self.Energy > 0.25f) m.F1 = frame;
            m.I1 = (m.I1 & 1) | ((int)(self.Energy * 1000f) << 1);
            float sinceBlock = m.F1 > 0f ? (frame - m.F1) / 60f : 99f;
            bool touchSpent = sinceBlock < TouchCool;
            bool punishWindow = sinceBlock < TouchCool - GuardLower - SwingWindup - 0.02f || (touchSpent && !guarding);

            // Its ball: one look at the windup pins the release.
            if (exact && age <= 8 && s.Visible && (t.Tell == ActionKind.Ranged || t.Tell == ActionKind.RailShot)
                && t.TellProgress >= 0f && t.TellProgress < 1f)
                m.F3 = frame - age + (1f - t.TellProgress) * ActionSpec.Default(t.Tell).Windup * 60f;
            float ballIn = m.F3 > 0f ? m.F3 + d / BallSpeed * 60f - frame : 999f;
            if (ballIn < -6f) { m.F3 = 0f; ballIn = 999f; }

            bool charger = exact && closing > ChargeSpeed;
            if (charger || t.Tell == ActionKind.Contact) m.I1 |= 1;
            bool rammer = (m.I1 & 1) != 0;
            // Guard against a rammer (anything within a dash of us), never against a body
            // that only swings or shoots: those get the swing, and their balls the guard.
            bool threat = rammer && (charger || d < DashThreat) && !touchSpent;
            bool ballSoon = ballIn < 900f && ballIn <= 12f && ballIn > -4f;

            var input = new EnemyInput { AimWorld = pos };
            int? request = null;
            bool hold = false;

            // ── 1. The touch is spent and it is in reach: swing into its cooldown. ──
            if (punishWindow && d <= _swingMax + 10f)
            {
                if (idle) request = SwingIdx;          // (a raised guard is let go first)
                // The body we just blocked is on the guarded side, even when it stands on us.
                float side = MathF.Abs(to.X) < 3f ? (self.Facing >= 0 ? 1f : -1f) : sx;
                input.AimWorld = self.Position + new Vector2(side * 40f, 0f);
                input.MoveDir.X = side;
            }
            // ── 2. Something could touch us, or a ball is about to land: guard up. ──
            else if (threat || ballSoon)
            {
                hold = true;
                if (!guarding) request = GuardIdx;
                // Behind a guard: close on a shooter or a turret, hold ground against a rammer.
                input.MoveDir.X = charger ? 0f : sx;
            }
            // ── 3. In reach of something not threatening: swing. ──
            else if (idle && d <= _swingMax)
                request = SwingIdx;
            // ── 4. A turret far off: a long untouchable dash covers the gap. ──
            else if (idle && !rammer && d > 140f && d < 260f && grounded && self.Energy >= _dashCost + 1.5f
                     && ballIn > 900f && MathF.Abs(t.Velocity.X) < 30f && frame > 90)
            {
                request = DashIdx;
                input.MoveDir.X = sx;
            }
            else
                input.MoveDir.X = d > _swingMax - 6f ? sx : 0f;

            // Steps: only after a real stall.
            if (grounded && MathF.Abs(self.Velocity.X) < 3f && input.MoveDir.X != 0f && idle) m.F2 += s.Dt; else m.F2 = 0f;
            if (m.F2 > 0.25f && !hold && MathF.Abs(to.X) > 30f && m.F0 > 0.4f) { input.Jump = true; m.F0 = 0f; m.F2 = 0f; }
            if (to.Y < -20f && MathF.Abs(to.X) < 50f && !hold && m.F0 > 0.4f) { input.Jump = true; m.F0 = 0f; }
            if (to.Y > 7f * Chunk.TileSize) { input.MoveDir.X = 0f; input.Jump = false; }

            input.Guard = hold;
            if (request is int r) { input.WantAttack = true; input.RequestedAction = r; }
            return input;
        }
    }
}
