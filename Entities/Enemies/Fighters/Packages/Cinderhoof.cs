using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Cinderhoof: a contact rammer built for the rammer-versus-rammer fight that decides a
// field where most of the top fighters hurt by touch. Its hide (a Contact action, no
// windup, no tell before it lands) hits for 12.1 — one touch kills anything up to twelve
// health, including the high-health crawlers and tanks — on a body with 8.1 health, just
// over the common 7–8 one-touch numbers, so a straight trade with a lighter-hitting
// rammer leaves it standing and the other side dead.
//
// The brain makes the touch a trade only when it has to:
//   * it fires the hide when the boxes are about to meet, never early (a hide spent on air
//     is 0.85 s of recovery the opponent can walk into), and from the first frame — a
//     rammer that holds its hide through the opening second is touched before it can;
//   * it times every opponent contact cycle it sees (the tell reads Contact from the first
//     live frame to the last recovery frame, and recovery is fixed at 0.85 s), learns the
//     opponent's live window from the first full cycle, backs off a live hide — dashing
//     clear, untouchable for the dash's first frames, when it is about to reach — and
//     dashes back in to touch while the opponent is still in recovery;
//   * against shooters it keeps its feet on the ground through the predicted release
//     (fixed windups) and walks through the balls on 8 health;
//   * it hops steps and placed blocks (probing the cell ahead), backs off when walled in,
//     and brakes before an arena edge, since a knockback slide off the world is a draw.
public sealed class Cinderhoof : IFighterPackage
{
    public string Name   => "Cinderhoof";
    public string Author => "opus-w4";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 8.1f,
        Strength       = 1f,
        Density        = 0.6f,
        ReactionFrames = 6,
        GroundPower    = 295f,
        JumpImpulse    = 376f,
        EnergyReserve  = 2f,
        EnergyRegen    = 0.6f,
        Color          = new Color(170, 60, 30),
        Actions        = { Hide(), Dash() },
        EngageRange    = 0f,
        AlertRange     = 2000f,
        Brain          = s => new HoofBrain(s),
    };

    private const float HideActive = 0.5f;

    private static ActionSpec Hide()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage    = 12.1f;
        a.HalfWidth = 16f;
        a.Active    = HideActive;
        a.MaxRange  = 400f;   // the brain decides when; the band never refuses it
        a.Knockback = new Vector2(60f, -60f);
        return a;
    }

    private static ActionSpec Dash()
    {
        var a = ActionSpec.Default(ActionKind.Dash);
        a.Speed      = 790f;
        a.Active     = 0.15f;
        a.Intangible = 0.05f;
        return a;
    }

    // Scratch:
    //   F0 = seconds stalled
    //   F1 = seconds since the last jump
    //   F2 = predicted frame the opponent's shot leaves the muzzle (0 = none pending)
    //   F3 = the opponent's learned contact live window, s (0 = not learned yet)
    //   I0 = frame the opponent's current contact cycle started (0 = none running)
    //   I1 = frame a walled-off retreat ends
    private sealed class HoofBrain : FighterController
    {
        private const int HideIdx = 0, DashIdx = 1;
        private const float ContactRecovery = 0.85f;   // fixed per kind by the compiler
        private const float DefaultLive = 0.6f;        // guess before one cycle is timed
        private readonly float _dashCost;

        public HoofBrain(FighterSpec s)
        {
            // The spec handed to the brain is the authored one; price the dash from the
            // cost table's own formula (distance + untouchable time).
            var d = s.Actions[DashIdx];
            _dashCost = 0.004f * d.Speed * d.Active + 3.6f * d.Intangible;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            EnemyTarget t; int age;
            bool fresh = false;
            float far = (coarse.Position - self.Position).Length();
            int period = far < 120f ? 2 : far < 300f ? 3 : 6;
            if (self.Energy >= s.TargetCost + 0.1f && s.Frame % period == 0) { t = s.Target(out age); fresh = true; }
            else t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = coarse; age = 0; fresh = false; }
            float theirHalf = t.Bounds.Right > t.Bounds.Left ? (t.Bounds.Right - t.Bounds.Left) * 0.5f : 12f;

            var pos = t.Position + t.Velocity * (age * s.Dt);
            var to = pos - self.Position;
            float dist = to.Length();
            var input = new EnemyInput { AimWorld = pos };
            int dir = to.X >= 0f ? 1 : -1;
            input.MoveDir.X = dir;
            float touch = self.Radius + 3f + theirHalf;   // centre distance at which a hide box meets a body

            // ── The opponent's contact cycle ─────────────────────────────
            if (fresh && s.Visible)
            {
                int seenAt = s.Frame - age;
                bool cyc = t.Tell == ActionKind.Contact && t.TellProgress >= 0f;
                if (cyc && m.I0 == 0) m.I0 = Math.Max(1, seenAt);
                else if (!cyc && m.I0 != 0)
                {
                    float live = (seenAt - m.I0) / 60f - ContactRecovery;
                    if (live > 0.03f && live < 6f) m.F3 = m.F3 <= 0f ? live : MathF.Max(m.F3 * 0.5f + live * 0.5f, live);
                    m.I0 = 0;
                }
            }
            float theirLiveEst = m.F3 > 0f ? m.F3 : DefaultLive;
            float el = m.I0 != 0 ? (s.Frame - m.I0) / 60f : -1f;
            if (m.I0 != 0 && el > theirLiveEst + ContactRecovery + 0.5f) { m.I0 = 0; el = -1f; }
            bool theirLive = m.I0 != 0 && el < theirLiveEst + 0.08f;
            bool theirRecover = m.I0 != 0 && !theirLive && el < theirLiveEst + ContactRecovery - 0.1f;

            // ── Shots: predict the release from the fixed windup ──────────
            if (fresh && s.Visible && (t.Tell == ActionKind.Ranged || t.Tell == ActionKind.RailShot)
                && t.TellProgress >= 0f && t.TellProgress < 1f)
            {
                float windup = t.Tell == ActionKind.Ranged ? 36f : 81f;
                m.F2 = s.Frame - age + (1f - t.TellProgress) * windup;
            }
            float toFire = m.F2 > 0f ? m.F2 - s.Frame : -99f;
            if (toFire < -12f) m.F2 = 0f;
            bool shotSoon = toFire > -12f && toFire < 10f;

            m.F1 += s.Dt;
            if (MathF.Abs(self.Velocity.X) < 25f && MathF.Abs(to.X) > 30f && !self.Staggered) m.F0 += s.Dt; else m.F0 = 0f;
            // Walled off for a long while: back away for a beat.
            if (m.F0 > 2.5f) { m.I1 = s.Frame + 50; m.F0 = 0f; }
            bool retreat = s.Frame < m.I1;

            bool myAction = self.CurrentAction != ActionKind.Special && self.ActionProgress >= 0f;
            bool grounded = MathF.Abs(self.Velocity.Y) < 5f;
            bool canDash = grounded && !myAction && self.Energy >= _dashCost + 0.05f;
            float close = dist > 1f ? MathF.Max(0f, Vector2.Dot(self.Velocity - t.Velocity, to / dist)) : 0f;
            bool level = MathF.Abs(to.Y) < touch + 10f;
            bool dash = false;

            // ── Edge guard: the arena ends in a fall, and a slide off it is a draw ──
            int edge = 0;   // +1/-1: a drop lies that way within reach of the current slide
            if (s.Frame % 6 == 2 && grounded && MathF.Abs(self.Velocity.X) > 20f)
            {
                float ts = Chunk.TileSize;
                int vdir = self.Velocity.X >= 0f ? 1 : -1;
                float look = self.Radius + 8f + MathF.Abs(self.Velocity.X) * 0.35f;
                int gx = (int)MathF.Floor((self.Position.X + vdir * look) / ts);
                int feet = (int)MathF.Floor((self.Position.Y + self.Radius + 2f) / ts);
                if (s.Cell(gx, feet + 1) != TileState.Solid && s.Cell(gx, feet + 3) != TileState.Solid) edge = vdir;
            }

            // ── A live hide close by: back off, dashing clear when it is about to reach ──
            bool evade = theirLive && dist < touch + 45f && level;
            if (evade)
            {
                input.MoveDir.X = -dir;
                dash = canDash && dist < touch + 22f;
            }
            // ── A hide in recovery: dash back in while the window lasts ──
            else if (theirRecover && !retreat && canDash && level && !(toFire > 5f && toFire < 30f)
                     && el + 0.55f < theirLiveEst + ContactRecovery
                     && dist > touch + 30f && dist < touch + 150f)
            {
                input.MoveDir.X = dir;
                dash = true;
            }
            else if (retreat) input.MoveDir.X = -dir;

            if (edge != 0 && input.MoveDir.X * edge > 0f) { input.MoveDir.X = -edge; if (!evade) dash = false; }
            if (edge != 0 && MathF.Abs(self.Velocity.X) > 60f) input.MoveDir.X = -edge;

            if (dash)
            {
                input.WantAttack = true;
                input.RequestedAction = DashIdx;
                return input;
            }

            bool jump = m.F0 > 0.35f;
            // A step or a placed block just ahead: hop it before the walk stalls on it.
            if (!evade && s.Frame % 4 == 1 && grounded && MathF.Abs(to.X) > 20f && MathF.Abs(self.Velocity.X) < 90f)
            {
                float ts = Chunk.TileSize;
                int gx = (int)MathF.Floor((self.Position.X + dir * (self.Radius + 5f + MathF.Abs(self.Velocity.X) * 0.08f)) / ts);
                int gy = (int)MathF.Floor((self.Position.Y + self.Radius - 4f) / ts);
                if (s.Cell(gx, gy) == TileState.Solid) jump = true;
                else if (s.Cell(gx, (int)MathF.Floor(self.Position.Y / ts)) == TileState.Solid) jump = true;
            }
            if (to.Y < -20f && MathF.Abs(to.X) < 80f) jump = true;
            // Jumping into a shot's flight only lifts the body into its path.
            if (shotSoon || retreat || evade) jump = false;
            if (jump && m.F1 > 0.35f) { input.Jump = true; m.F1 = 0f; }

            // Fire the hide only when the boxes are about to meet.
            bool meet = dist <= touch + 3f + close * 0.1f && MathF.Abs(to.Y) < touch + 6f;
            input.WantAttack = meet && !evade;
            input.RequestedAction = HideIdx;
            return input;
        }
    }
}
