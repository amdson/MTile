using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Bramblehorn: a heavy rammer built to win the one-touch exchange. The hide (Contact)
// hits for 10, which kills anything carrying less than ten health in one touch, and the
// body carries more health than any one-touch rammer's hide, so when two rammers meet
// it is the other one that falls. Dense HP also means a shooter needs five or more
// clean shots before the hide arrives. A Dash with a long untouchable window does two
// jobs: from mid range it cuts through a shot at its release frame (computed from the
// tell and the fixed windup), landing the body inside the shooter's recovery, and up
// close it slips through an opposing rammer's live hide. Steps and blocks are hopped by
// reading the cell ahead; a long stall backs off and comes round again.
public sealed class Bramblehorn : IFighterPackage
{
    public string Name   => "Bramblehorn";
    public string Author => "opus-w4";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 7.5f,
        Strength       = 1f,
        Density        = 0.6f,
        ReactionFrames = 6,
        GroundPower    = 360f,
        JumpImpulse    = 380f,
        EnergyReserve  = 2.2f,
        EnergyRegen    = 0.8f,
        Color          = new Color(110, 140, 60),
        Actions        = { Hide(), Dash() },
        EngageRange    = 0f,
        AlertRange     = 2000f,
        Brain          = s => new HornBrain(s),
    };

    private static ActionSpec Hide()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage    = 10f;
        a.Active    = 0.6f;
        a.MaxRange  = 400f;
        a.Knockback = new Vector2(40f, -40f);
        return a;
    }

    private static ActionSpec Dash()
    {
        var a = ActionSpec.Default(ActionKind.Dash);
        a.Speed      = 790f;
        a.Active     = 0.15f;
        a.Intangible = 0.15f;
        return a;
    }

    // Scratch: F0 = seconds stalled, F1 = seconds since the last jump, F2 = predicted
    // frame the opponent's shot leaves the muzzle (0 = none), F3 = frame the opponent's
    // current hide went live (0 = none running). I0 = a walled-off retreat runs until
    // frame I0 (> 0), or a committed charge until frame -I0 (< 0). I1 = flags (bit 0: the
    // opponent has shown a hide, bit 1: it has shown any other tell, bit 2: it was in a
    // hide at the last exact read) | the frame of the last dash << 3.
    private sealed class HornBrain : FighterController
    {
        private const int RamSeen = 1, OtherSeen = 2, InHide = 4;
        // How long an opposing hide is assumed to stay live after it starts.
        private const float EvadeFor = 0.55f;
        private readonly float _active, _dashCost, _dashLen;
        public HornBrain(FighterSpec s)
        {
            _active   = s.Actions[0].Active;
            _dashCost = s.Actions[1].EnergyCost;
            _dashLen  = s.Actions[1].Speed * s.Actions[1].Active;
        }

        // A dash is a ground burst at several hundred px/s: a step up in its path turns it
        // into a launch. Three foot-level cells along the path must be open.
        // A blocked path backs the next look off by six frames (cell reads are paid).
        private static bool Flat(FighterSenses s, in SelfState self, int dir, float len, ref int lastDash)
        {
            float ts = Chunk.TileSize;
            int gy = (int)MathF.Floor((self.Position.Y + self.Radius - 4f) / ts);
            for (int k = 1; k <= 3; k++)
            {
                int gx = (int)MathF.Floor((self.Position.X + dir * (self.Radius + len * k / 3f)) / ts);
                if (s.Cell(gx, gy) == TileState.Solid) { lastDash = s.Frame - 24; return false; }
            }
            return true;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            EnemyTarget t; int age;
            bool fresh = false;
            float far = (coarse.Position - self.Position).Length();
            int period = far < 140f ? 2 : far < 260f ? 4 : 8;
            float floor = far < 140f ? 0.1f : _dashCost;
            if (self.Energy >= s.TargetCost + floor && s.Frame % period == 0) { t = s.Target(out age); fresh = true; }
            else t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = coarse; age = 0; fresh = false; }
            float half = t.Bounds.Right > t.Bounds.Left ? (t.Bounds.Right - t.Bounds.Left) * 0.5f : 12f;

            int flags = m.I1 & 7, lastDash = m.I1 >> 3;
            if (fresh && s.Visible && t.Known)
            {
                bool hide = t.Tell == ActionKind.Contact && t.TellProgress >= 0f;
                if (hide) { flags |= RamSeen; if ((flags & InHide) == 0) m.F3 = s.Frame - age; flags |= InHide; }
                else
                {
                    flags &= ~InHide; m.F3 = 0f;
                    if (t.TellProgress >= 0f && t.Tell != ActionKind.Special && t.Tell != ActionKind.Dash) flags |= OtherSeen;
                }
            }

            var pos = t.Position + t.Velocity * (age * s.Dt);
            var to = pos - self.Position;
            float dist = to.Length();
            var input = new EnemyInput { AimWorld = pos };
            int dir = to.X >= 0f ? 1 : -1;
            input.MoveDir.X = dir;

            // Shot tell → predicted release frame.
            if (fresh && s.Visible && (t.Tell == ActionKind.Ranged || t.Tell == ActionKind.RailShot)
                && t.TellProgress >= 0f && t.TellProgress < 1f)
            {
                float windup = t.Tell == ActionKind.Ranged ? 36f : 81f;
                m.F2 = s.Frame - age + (1f - t.TellProgress) * windup;
            }

            m.F1 += s.Dt;
            if (MathF.Abs(self.Velocity.X) < 25f && MathF.Abs(to.X) > 30f && !self.Staggered && !self.ActionCommitted) m.F0 += s.Dt; else m.F0 = 0f;

            float toFire = m.F2 > 0f ? m.F2 - s.Frame : -99f;
            // The ball is in the air until it has covered the gap (slow balls ~400 px/s):
            // a jump before then lifts the body into it and stretches the stagger.
            float flight = 4f + dist / 400f * 60f;
            if (toFire < -flight) m.F2 = 0f;
            bool shotSoon = toFire > -flight && toFire < 10f;

            if (m.F0 > 2.5f) { m.I0 = s.Frame + 50; m.F0 = 0f; }
            bool retreat = m.I0 > 0 && s.Frame < m.I0;
            bool committed = m.I0 < 0 && s.Frame < -m.I0;
            if (retreat) input.MoveDir.X = -dir;

            bool grounded = MathF.Abs(self.Velocity.Y) < 5f;
            bool idle = self.CurrentAction == ActionKind.Special || self.ActionProgress < 0f;
            float box = self.Radius + 3f + half;

            // ── Rammer duel. Two live hides that meet both land, so against a body that
            // carries one (or might: no tell seen yet) the hide is never fired first.
            // Their hide going live is the cue: get out of its reach for as long as it can
            // be live, then come back through its long cooldown.
            bool ramMode = (flags & RamSeen) != 0 || (flags & OtherSeen) == 0;
            bool evade = false, hold = false, through = false, lunge = false, wary = false;
            float closing = dist > 1f ? Vector2.Dot(self.Velocity - t.Velocity, to / dist) : 0f;
            if (ramMode && !retreat)
            {
                bool theirsLive = m.F3 > 0f && (s.Frame - m.F3) * s.Dt < EvadeFor;
                bool theirsCooling = m.F3 > 0f && !theirsLive;
                wary = !theirsCooling;
                if (theirsLive && dist < box + 90f) evade = true;
                // Their hide is cooling: cover the gap in one burst that ends overlapping,
                // so the hide goes in the frame the dash lets go.
                else if (theirsCooling && (s.Frame - m.F3) * s.Dt < 0.9f
                         && MathF.Abs(dist - _dashLen) < box - 8f && MathF.Abs(to.Y) < box) lunge = true;
                else if (!theirsCooling && dist < box + 45f && MathF.Abs(to.Y) < box)
                {
                    if (dist < box + 12f && s.Frame > 56) through = true;
                    else if (s.Frame <= 58) hold = true;
                    // Mixed: sometimes cross, sometimes walk in and take the touch.
                    else if (s.Chance(0.12f)) through = true;
                }
            }
            if (hold) input.MoveDir.X = -dir;
            if (evade) input.MoveDir.X = dist < box + 25f ? -dir : dist > box + 40f ? dir : 0f;

            bool jump = false;
            if (m.F0 > 0.35f) jump = true;
            bool duel = evade || hold || through || lunge;
            int mdir = input.MoveDir.X >= 0f ? 1 : -1;
            if (s.Frame % 3 == 1 && grounded && MathF.Abs(to.X) > 20f && !duel)
            {
                float ts = Chunk.TileSize;
                int gx = (int)MathF.Floor((self.Position.X + mdir * (self.Radius + 5f + MathF.Abs(self.Velocity.X) * 0.08f)) / ts);
                int gy = (int)MathF.Floor((self.Position.Y + self.Radius - 4f) / ts);
                if (s.Cell(gx, gy) == TileState.Solid) jump = true;
                else if (s.Cell(gx, (int)MathF.Floor(self.Position.Y / ts)) == TileState.Solid) jump = true;
            }
            if (!evade && !hold && to.Y < -20f && MathF.Abs(to.X) < 80f) jump = true;
            if (shotSoon || retreat) jump = false;
            if (jump && m.F1 > 0.35f) { input.Jump = true; m.F1 = 0f; }


            bool canDash = idle && grounded && self.Energy >= _dashCost && s.Frame - lastDash > 30;
            // Out of a live hide's reach: a dash away is untouchable and outruns any walk.
            if (evade && canDash && dist < box + 50f && Flat(s, self, -dir, _dashLen, ref lastDash))
            {
                input.MoveDir.X = -dir;
                m.I1 = flags | (s.Frame << 3);
                input.WantAttack = true; input.RequestedAction = 1; input.Jump = false;
                return input;
            }
            if (lunge && canDash && Flat(s, self, dir, _dashLen, ref lastDash))
            {
                m.I1 = flags | (s.Frame << 3);
                input.MoveDir.X = dir;
                input.WantAttack = true; input.RequestedAction = 1; input.Jump = false;
                return input;
            }
            // Through a hide that has not fired: untouchable across the whole overlap, so a
            // hide fired at the crossing is spent on nothing and its cooldown is ours.
            if (through && canDash && Flat(s, self, dir, _dashLen, ref lastDash))
            {
                m.I1 = flags | (s.Frame << 3);
                input.MoveDir.X = dir;
                input.WantAttack = true; input.RequestedAction = 1; input.Jump = false;
                return input;
            }
            // Through a shot: the release lands inside the untouchable window and the
            // dash ends near enough that the hide follows during the shooter's recovery.
            if (!evade && !hold && canDash && !retreat && s.Visible && toFire > 0f && toFire <= 4f
                && dist > box && dist < box + _dashLen + 90f && MathF.Abs(to.Y) < 30f)
            {
                m.I1 = flags | (s.Frame << 3);
                input.MoveDir.X = dir;
                input.WantAttack = true; input.RequestedAction = 1; input.Jump = false;
                return input;
            }
            m.I1 = flags | (lastDash << 3);

            float close = dist > 1f ? MathF.Max(0f, Vector2.Dot(self.Velocity - t.Velocity, to / dist)) : 0f;
            // Against a hide that is not cooling, never fire early: a hide fired at range
            // is a tell the other rammer walks around. Touch range only, or a body caught
            // in its own dash recovery.
            bool theirDash = t.Tell == ActionKind.Dash && t.TellProgress >= 1f;
            float reach = wary && !theirDash ? box + 2f : box + 2f + close * _active * 0.5f;
            // Already touching: a trade beats being touched for nothing.
            input.WantAttack = (!evade && !hold && dist <= reach || dist <= box + 2f) && MathF.Abs(to.Y) < box + 4f
                               && s.Frame > 64;
            input.RequestedAction = 0;
            return input;
        }
    }
}
