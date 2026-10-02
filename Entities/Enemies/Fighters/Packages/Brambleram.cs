using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Brambleram: a one-touch rammer with a ghost step. The hide is a Contact action with
// Damage 11.5 — more than any health pool seen so far — so the first touch ends the
// bout (a timeout is only ever a draw, so the bot is built to FINISH). Health 7.3
// outlasts a stock-priced touch (Thornbull's 7) and several shots. A Dash whose whole
// 120 px is untouchable does two jobs:
//  * vs shooters: the brain predicts the release frame from the opponent's fixed
//    windup, LEARNS its ball speed from the first hit it takes, and times the dash so
//    the ghost frames straddle the ball's arrival, carrying the body through the shot
//    and up to the shooter during its recovery;
//  * vs rammers (a touch tell seen, or an opponent closing with no other tell): a
//    touch-for-touch is a coin flip on health, so it passes straight through the
//    rammer's live box while untouchable, then turns on it inside the touch's fixed
//    0.85 s recovery (its live length is learned from the opponent's own cycle).
// Dashes are vetoed toward the arena's open ends; late in a bout it trades freely.
public sealed class Brambleram : IFighterPackage
{
    public string Name   => "Brambleram";
    public string Author => "opus-w4";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 7.3f,
        Strength       = 1f,
        Density        = 0.5f,
        ReactionFrames = 6,
        TargetMemory   = true,
        GroundPower    = 320f,
        JumpImpulse    = 400f,
        EnergyReserve  = 2.5f,
        EnergyRegen    = 0.8f,
        Color          = new Color(120, 150, 60),
        Actions        = { Hide(), Ghost() },
        EngageRange    = 0f,
        AlertRange     = 2000f,
        Brain          = s => new RamBrain(s),
    };

    private static ActionSpec Hide()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage    = 11.5f;
        a.Active    = 0.6f;
        a.MaxRange  = 90f;
        a.Knockback = new Vector2(60f, -40f);
        return a;
    }

    private static ActionSpec Ghost()
    {
        var a = ActionSpec.Default(ActionKind.Dash);
        a.Speed      = 790f;
        a.Active     = 0.15f;
        a.Intangible = 0.15f;
        return a;
    }

    // Scratch: F0 = seconds stalled, F1 = learned ball speed (0 = not yet), F2 = predicted
    // frame the opponent's shot leaves the muzzle (0 = none), F3 = frame the opponent's
    // touch went live (0 = none seen).
    // I0 = last dash frame | last jump frame << 12 | flags << 24.
    // I1 = retreat-end frame | (health × 50) << 12 | opponent half-width << 22 |
    //      touch-length step << 28.
    private sealed class RamBrain : FighterController
    {
        private const int HideIdx = 0, DashIdx = 1;
        private const int Mask = 4095;
        // flags
        private const int FRail = 1, FSeenTouch = 2, FSeenOther = 4, FTouchOn = 8, FRamSuspect = 16, FWeakTouch = 32;
        private readonly float _active, _dashSpeed, _dashCost, _dashGhost, _dashDist;
        private readonly int _dashWind, _dashFrames;
        public RamBrain(FighterSpec s)
        {
            _active = s.Actions[HideIdx].Active;
            var d = s.Actions[DashIdx];
            _dashSpeed = d.Speed;
            _dashWind = (int)MathF.Round(ActionSpec.Default(ActionKind.Dash).Windup * 60f);
            _dashFrames = (int)MathF.Round(d.Active * 60f);
            _dashGhost = d.Intangible;
            _dashDist = d.Speed * d.Active;
            _dashCost = 0.004f * d.Speed * d.Active + 3.6f * d.Intangible;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            int f12 = s.Frame & Mask;
            int lastDash = m.I0 & Mask, lastJump = (m.I0 >> 12) & Mask, flags = (m.I0 >> 24) & 255;
            int retreatEnd = m.I1 & Mask;
            float prevHp = ((m.I1 >> 12) & 1023) / 50f;
            int half = (m.I1 >> 22) & 63, touchQ = (m.I1 >> 28) & 7;
            if (s.Frame < 2 || prevHp <= 0f) prevHp = self.Health;
            int sinceDash = (f12 - lastDash) & Mask, sinceJump = (f12 - lastJump) & Mask;
            // The opponent's touch: how long its live window lasts (learned from its cycle).
            float touchLive = 0.5f + 0.4f * touchQ;

            var coarse = s.TargetCoarse();
            EnemyTarget t; int age;
            bool fresh = false;
            float far = (coarse.Position - self.Position).Length();
            // Ration reads so the meter always holds a dash: a read is 0.02, a dash ~1.
            int period = far < 170f ? 2 : 4;
            if (self.Energy < _dashCost + 0.15f) period = far < 120f ? 4 : 8;
            if (self.Energy >= s.TargetCost + 0.05f && s.Frame % period == 0) { t = s.Target(out age); fresh = true; }
            else t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = coarse; age = 0; fresh = false; }
            if (fresh && t.Bounds.Right > t.Bounds.Left) half = Math.Clamp((int)MathF.Round((t.Bounds.Right - t.Bounds.Left) * 0.5f), 1, 63);
            if (half <= 0) half = 12;

            var pos = t.Position + t.Velocity * (age * s.Dt);
            var to = pos - self.Position;
            float dist = to.Length();
            var input = new EnemyInput { AimWorld = pos };
            int dir = to.X >= 0f ? 1 : -1;
            input.MoveDir.X = dir;
            bool grounded = MathF.Abs(self.Velocity.Y) < 5f;
            float seen = s.Frame - age;

            if (fresh && s.Visible && t.TellProgress >= 0f)
            {
                // Track the opponent's shot: from the tell, predict the release frame.
                if ((t.Tell == ActionKind.Ranged || t.Tell == ActionKind.RailShot) && t.TellProgress < 1f)
                {
                    bool rail = t.Tell == ActionKind.RailShot;
                    m.F2 = seen + (1f - t.TellProgress) * (rail ? 81f : 36f);
                    flags = rail ? flags | FRail : flags & ~FRail;
                }
                if (t.Tell != ActionKind.Contact && t.Tell != ActionKind.Dash && t.Tell != ActionKind.PlaceBlock
                    && t.Tell != ActionKind.SpawnBlockInAir && t.Tell != ActionKind.Mine)
                    flags |= FSeenOther;
            }
            // The opponent's touch: note when it goes live and, when it ends, how long a
            // cycle it was, so its recovery (fixed 0.85 s) can be found again.
            if (fresh && s.Visible)
            {
                bool on = t.Tell == ActionKind.Contact;
                if (on && (flags & FTouchOn) == 0) { m.F3 = seen; flags |= FTouchOn | FSeenTouch; }
                else if (!on && (flags & FTouchOn) != 0)
                {
                    flags &= ~FTouchOn;
                    float live = (seen - m.F3) / 60f - 0.85f;
                    int q = Math.Clamp((int)MathF.Ceiling((live - 0.5f) / 0.4f), 0, 7);
                    if (q > touchQ) touchQ = q;
                    touchLive = 0.5f + 0.4f * touchQ;
                }
            }
            float sinceTouch = (flags & FTouchOn) != 0 ? s.Frame - m.F3 : -1f;
            bool theyLive    = sinceTouch >= 0f && sinceTouch < touchLive * 60f + 3f;
            bool theyRecover = sinceTouch >= 0f && !theyLive && sinceTouch < (touchLive + 0.85f) * 60f - 4f;
            float closingThem = dist > 1f ? -Vector2.Dot(t.Velocity, to / dist) : 0f;
            if ((flags & FSeenOther) == 0 && closingThem > 50f && dist < 250f) flags |= FRamSuspect;
            if ((flags & FSeenOther) != 0) flags &= ~FRamSuspect;
            // Late in the bout a trade is no worse than the timeout's draw.
            bool rammer = ((flags & FSeenTouch) != 0 || (flags & FRamSuspect) != 0) && s.Frame < 1300;

            // Survived its touch with plenty to spare: its hide is weak, trade freely.
            if (self.Health < prevHp - 0.01f && self.Health > prevHp - self.Health && (flags & FTouchOn) != 0
                && dist < self.Radius + half + 45f)
                flags |= FWeakTouch;
            if ((flags & FWeakTouch) != 0) rammer = false;

            float toFire = m.F2 > 0f ? m.F2 - s.Frame : -999f;
            // Took damage from a ball in flight: learn how fast this opponent's balls travel.
            if (self.Health < prevHp - 0.01f && m.F2 > 0f && (flags & FRail) == 0 && !theyLive)
            {
                float flight = -toFire;
                if (flight >= 2f && flight <= 90f)
                {
                    float est = MathF.Max(0f, dist - self.Radius - 4f) * 60f / flight;
                    if (est > 120f && est < 2500f) m.F1 = m.F1 > 0f ? 0.5f * (m.F1 + est) : est;
                    m.F2 = 0f;   // that ball is spent: nothing left to dodge
                }
            }
            if (toFire < -90f || m.F2 <= 0f) { m.F2 = 0f; toFire = -999f; }

            m.F0 = MathF.Abs(self.Velocity.X) < 25f && MathF.Abs(to.X) > 30f && !self.Staggered
                   && self.CurrentAction != ActionKind.Dash && sinceDash > 30 ? m.F0 + s.Dt : 0f;
            bool shotSoon = toFire > -12f && toFire < 10f;

            if (m.F0 > 2.5f) { retreatEnd = (f12 + 50) & Mask; m.F0 = 0f; }
            bool retreat = ((retreatEnd - f12) & Mask) is > 0 and <= 50;
            if (retreat) input.MoveDir.X = -dir;

            bool jump = false;
            if (m.F0 > 0.35f) jump = true;
            if (s.Frame % 3 == 1 && grounded && MathF.Abs(to.X) > 20f)
            {
                float ts = Chunk.TileSize;
                int gx = (int)MathF.Floor((self.Position.X + dir * (self.Radius + 5f + MathF.Abs(self.Velocity.X) * 0.08f)) / ts);
                int gy = (int)MathF.Floor((self.Position.Y + self.Radius - 4f) / ts);
                if (s.Cell(gx, gy) == TileState.Solid) jump = true;
                else if (s.Cell(gx, (int)MathF.Floor(self.Position.Y / ts)) == TileState.Solid) jump = true;
            }
            if (to.Y < -20f && MathF.Abs(to.X) < 80f) jump = true;
            if (shotSoon || retreat) jump = false;

            // The hide: fire when the boxes will meet inside its live window.
            float touchD = self.Radius + 3f + half;
            float closeRel = dist > 1f ? MathF.Max(0f, Vector2.Dot(self.Velocity - t.Velocity, to / dist)) : 0f;
            bool meetSoon = dist <= touchD + 2f + closeRel * _active * 0.6f && MathF.Abs(to.Y) < touchD + 4f;
            bool hide = meetSoon && s.Frame > 64;
            bool idle = self.CurrentAction == ActionKind.Special && !self.Staggered;
            bool canDash = idle && grounded && sinceDash > 20 && self.Energy >= _dashCost + 0.12f;
            int req = -1;

            // Against a rammer, a touch for a touch is a coin flip on health. Instead pass
            // straight through its live box while untouchable, then turn on its recovery.
            if (rammer && theyRecover)
            {
                // Only commit the hide if the touch lands before its recovery runs out.
                float toMeet = MathF.Max(0f, dist - touchD) / MathF.Max(closeRel, 30f) * 60f;
                if (sinceTouch + toMeet + 3f > (touchLive + 0.85f) * 60f) hide = false;
                jump = jump && MathF.Abs(to.Y) > 20f;
            }
            if (rammer && !theyRecover && !retreat)
            {
                if (canDash && MathF.Abs(to.Y) < 16f && s.Visible)
                {
                    // Distance when the ghost frames begin, and the relative ground they cover.
                    float dStart = dist - closeRel * (_dashWind + 1) / 60f;
                    float cover = (_dashSpeed + MathF.Max(0f, closingThem)) * _dashGhost;
                    if (dStart <= touchD + 10f + closeRel * 2f / 60f && dStart >= touchD + 2f
                        && cover >= dStart + touchD + 4f)
                        req = DashIdx;
                }
                if (req < 0)
                {
                    if (theyLive && dist < touchD + 50f)
                    {
                        // Its box is live: keep out of reach until it goes into recovery.
                        input.MoveDir.X = -dir;
                        hide = meetSoon && s.Frame > 64;   // cornered: trade
                    }
                    else if (self.Energy >= _dashCost * 0.7f && sinceDash > 8 && dist > touchD + 2f)
                    {
                        // Close in, but hold the hide back so the pass is free to go.
                        hide = false;
                        if (dist < touchD + 30f && !canDash) input.MoveDir.X = -dir;
                    }
                }
            }

            // Ghost step through a shot: dash toward the shooter so the untouchable frames
            // straddle the ball's arrival. Simulate the closing frame by frame.
            if (req < 0 && !hide && canDash && toFire > -60f && dist > touchD + 40f && s.Visible && !rammer)
            {
                float vb = (flags & FRail) != 0 ? 1500f : (m.F1 > 0f ? m.F1 : 450f);
                float gap = dist - self.Radius - 6f, mine = 0f, v0 = MathF.Max(0f, self.Velocity.X * dir);
                int meet = -1;
                for (int k = 1; k <= 24; k++)
                {
                    mine += (k <= _dashWind ? v0 : k <= _dashWind + _dashFrames ? _dashSpeed : _dashSpeed * 0.3f) / 60f;
                    float ball = k > toFire ? vb * (k - toFire) / 60f : 0f;
                    if (mine + ball >= gap) { meet = k; break; }
                }
                int lo = _dashWind + 2, hi = _dashWind + (int)MathF.Round(_dashGhost * 60f) - 1;
                if (meet >= lo && meet <= (lo + hi) / 2 + 1) req = DashIdx;
            }

            // Never dash toward the arena's open end: the skid (and a launch off a step)
            // carries the body on through the air.
            if (req == DashIdx)
            {
                float ts = Chunk.TileSize;
                int gx0 = (int)MathF.Floor(self.Position.X / ts);
                int floorRow = (int)MathF.Floor((self.Position.Y + self.Radius + 6f) / ts);
                // Only a deep drop vetoes it: a step up is a launch that also climbs.
                int cells = (int)MathF.Ceiling((_dashDist + 50f) / ts);
                for (int i = 3; i <= cells && req == DashIdx; i += 3)
                {
                    int x = gx0 + dir * i;
                    if (s.Cell(x, floorRow + 1) != TileState.Solid && s.Cell(x, floorRow + 3) != TileState.Solid) req = -1;
                }
            }
            if (req == DashIdx)
            {
                input.MoveDir.X = dir;
                input.WantAttack = true;
                input.RequestedAction = DashIdx;
                lastDash = f12;
                jump = false;
            }
            else
            {
                input.WantAttack = hide;
                input.RequestedAction = HideIdx;
            }
            if (jump && sinceJump > 20) { input.Jump = true; lastJump = f12; }


            m.I0 = lastDash | (lastJump << 12) | (flags << 24);
            m.I1 = retreatEnd | (Math.Clamp((int)MathF.Round(self.Health * 50f), 0, 1023) << 12) | (half << 22) | (touchQ << 28);
            return input;
        }
    }
}
