using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Twinbarrel: a walker-jumper carrying two identical energy cannons and a jab. The two
// guns are the whole idea — a ball hit staggers its target and cancels whatever the
// target was winding up, so the side whose balls arrive more often holds the tempo.
// Each gun's PassivePriority sits above the other's ActivePriority, so once gun A has
// fired the brain asks for gun B and the sim hands over immediately: a ball every
// windup (0.6 s) instead of every windup + recovery (1.1 s). The brain only opens a
// windup when no opponent shot can land during it; otherwise it waits and jumps the
// shot (balls are aimed at the live position on the fire frame, so the jump goes on
// the frame after the fire; rail bolts at the position locked on the windup frame, so
// that jump goes a quarter second before the shot) and fires into the opponent's
// recovery, which is where the stagger lock starts. TargetMemory keeps it advancing on
// a target that hides behind a wall or a ledge; the jab is for anything that gets
// inside the muzzle.
public sealed class Twinbarrel : IFighterPackage
{
    public string Name   => "Twinbarrel";
    public string Author => "fable-w3";

    private const float ShotSpeed  = 550f;
    private const float ShotDamage = 1.0f;
    private const float ShotMax    = 520f;

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 6f,
        Strength       = 1.0f,
        Density        = 1.5f,
        ReactionFrames = 2,
        GroundPower    = 160f,
        JumpImpulse    = 560f,
        EnergyReserve  = 7f,
        EnergyRegen    = 2.0f,
        TargetMemory   = true,
        Color          = new Color(230, 120, 40),
        Actions        = { Gun(), Gun(), Jab() },
        EngageRange    = 26f,
        AlertRange     = 900f,
        Brain          = s => new TwinbarrelBrain(s),
    };

    private static ActionSpec Gun()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.Speed = ShotSpeed; a.Damage = ShotDamage;
        a.MinRange = 0f; a.MaxRange = ShotMax;
        // Passive above the other gun's Active: the brain can swap guns the moment one
        // has fired. The kit's own priorities, as the spec contract allows.
        a.ActivePriority = 28; a.PassivePriority = 29;
        return a;
    }

    private static ActionSpec Jab()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 1.0f; a.Reach = 22f; a.MaxRange = 36f;
        // Below the guns so a swap never lands on the jab by accident; the brain
        // requests it explicitly at point blank.
        a.ActivePriority = 27; a.PassivePriority = 20;
        return a;
    }

    // Scratch:
    //   I0 = gun to request next (0 or 1); bitwise-NOT'd while a swap is pending
    //   I1 = shots fired since the target's health last dropped
    //   F0 = > 0: seconds left retreating (from a melee windup, or to open the range
    //        on a shooter above us); < 0: seconds left in a deliberate advance (counted
    //        while free to move); 0: neither
    //   F1 = > 0: seconds stalled (wanting to move, not moving); < 0: seconds until an
    //        opponent ball now in flight has passed us
    //   F2 = seconds since our last jump request, negated for a dodge jump (0 = none)
    //   F3 = lowest target health seen (0 = none yet)
    private sealed class TwinbarrelBrain : FighterController
    {
        private const int GunA = 0, GunB = 1, JabIdx = 2;
        private const float AirSeconds = 0.75f;     // a jump at our launch speed lasts ~0.75 s
        private const float KeepRange  = 170f;      // from below a target, the line only clears from here
        private readonly float _shotCost, _shotMax, _jabMax, _engage, _windup, _recovery;

        public TwinbarrelBrain(FighterSpec s)
        {
            _shotCost = s.Actions[GunA].EnergyCost;
            _shotMax  = s.Actions[GunA].MaxRange;
            _jabMax   = s.Actions[JabIdx].MaxRange;
            _engage   = s.EngageRange;
            var d = ActionSpec.Default(ActionKind.Ranged);
            _windup   = d.Windup;
            _recovery = d.Active + d.Recovery;
        }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self   = s.Self;
            var coarse = s.TargetCoarse();
            float cd   = coarse.Known ? (coarse.Position - self.Position).Length() : 9999f;

            // Read cadence. Every frame while an opponent shot is late in its windup (the
            // dodge is timed off this), every 2nd frame in gun range, every 5th beyond.
            var last = s.TargetStale(out int lastAge);
            bool hot = last.Known && lastAge <= 6 && last.TellProgress >= 0.45f && last.TellProgress < 1f
                       && last.Tell is ActionKind.Ranged or ActionKind.RailShot;
            int cadence = hot ? 1 : cd < 560f ? 2 : 5;
            bool paid = s.Frame % cadence == 0 && self.Energy >= s.TargetCost + 0.25f;
            EnemyTarget t; int age;
            if (paid) t = s.Target(out age);
            else      t = s.TargetStale(out age);
            bool exact = t.Known && age <= 12;
            if (!exact) { t = coarse; age = 6; }
            if (!t.Known) return new EnemyInput { WantAttack = false };

            var pos    = t.Position + t.Velocity * (age * s.Dt);
            var to     = pos - self.Position;
            float dist = to.Length();
            float dx   = to.X >= 0f ? 1f : -1f;
            var input  = new EnemyInput { AimWorld = pos };

            // Airborne for AirSeconds after a jump request (the apex also has |vy| ≈ 0,
            // so velocity alone cannot tell the top of a jump from the ground). A dodge
            // jump is stored negated: it may open a windup once it is clearly rising.
            if (m.F2 > 0f) m.F2 += s.Dt; else if (m.F2 < 0f) m.F2 -= s.Dt;
            float sinceJump = MathF.Abs(m.F2);
            bool jumping  = m.F2 != 0f && sinceJump < AirSeconds;
            bool airborne = jumping || MathF.Abs(self.Velocity.Y) >= 30f;
            bool grounded = !airborne;
            bool dodgeAir = m.F2 < 0f && sinceJump >= 0.1f && sinceJump < AirSeconds;
            bool free     = !self.ActionCommitted;
            // An opponent ball in flight: count it down.
            if (m.F1 < 0f) { m.F1 += s.Dt; if (m.F1 > 0f) m.F1 = 0f; }
            bool ballInFlight = m.F1 < 0f;

            // ── Did a shot land? ───────────────────────────────────────────
            if (m.F3 <= 0f || t.Health > m.F3) m.F3 = t.Health;
            else if (t.Health < m.F3 - 0.01f) { m.F3 = t.Health; m.I1 = 0; }

            // ── Opponent tells ──────────────────────────────────────────────
            bool shooter  = exact && s.Visible && t.Tell is ActionKind.Ranged or ActionKind.RailShot;
            bool tellLive = exact && s.Visible && t.TellProgress >= 0f && t.TellProgress < 1f;
            bool ballTell = tellLive && t.Tell == ActionKind.Ranged;
            bool railTell = tellLive && t.Tell == ActionKind.RailShot;
            bool meleeTell = tellLive && t.Tell is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash
                             && dist < 90f;
            // The frame a ball left: the previous read still showed the windup, this one
            // shows it done. With per-frame reads late in the windup this is 2-3 frames
            // after the fire, which is exactly when a jump moves us furthest from the
            // point the ball was aimed at before it arrives.
            bool justFired = paid && s.Visible && t.Known && last.Known && lastAge <= 4
                             && last.Tell == ActionKind.Ranged && last.TellProgress < 1f
                             && t.Tell == ActionKind.Ranged && t.TellProgress >= 1f;
            if (justFired) { m.F1 = -(dist / 450f + 0.05f); ballInFlight = true; }
            // Seconds until the opponent fires (windup is fixed per kind).
            float windupOpp = railTell ? 1.35f : 0.60f;
            float remain = (1f - t.TellProgress) * windupOpp - age * s.Dt;
            // When would their shot land on us? (a ball at a guessed 600 px/s; a bolt at once)
            float arrive = ballTell ? remain + dist / 600f : railTell ? remain : 99f;
            // Opening a windup now would eat that shot mid-windup.
            bool shotPending = arrive < _windup + 0.05f;
            // Keep the legs for the dodge while a shooter is mid-cycle: the only safe moment
            // to leave the ground against one is the frame after its ball leaves.
            bool saveJump = shooter && !justFired && !(t.Tell == ActionKind.RailShot && t.TellProgress >= 1f);

            // ── Own gun bookkeeping ────────────────────────────────────────
            bool firing = self.CurrentAction == ActionKind.Ranged && self.ActionCommitted;
            if (firing && self.ActionProgress >= 1f && m.I0 >= 0)
            {
                // This gun has fired: the other barrel is next, and the shot counts
                // until the target's health says it landed.
                m.I0 = ~(m.I0 == GunA ? GunB : GunA);
                m.I1++;
            }
            if (firing && self.ActionProgress < 1f && m.I0 < 0) m.I0 = ~m.I0;   // the new gun is winding up
            int gun = m.I0 < 0 ? ~m.I0 : m.I0;

            // A target standing higher than us. A ball at it from close by runs nearly flat
            // past the step edge and clips it, and a short flight gives the dodge no time:
            // open the range instead of closing it.
            bool uphill = to.Y < -10f && MathF.Abs(to.X) > 40f;

            // Three shots without the target losing health (the newest may still be in
            // flight): a ledge, a wall or a dodger. Level with it, walk in for a beat —
            // closer balls clear steps and land before a dodge can start. Below it, back
            // off further: the line only clears from range.
            if (m.I1 >= 3 && s.Visible && m.F0 == 0f) { m.F0 = uphill ? 1.0f : -0.5f; m.I1 = 0; }
            if (uphill && dist < KeepRange && !firing && free && m.F0 == 0f && s.Visible) m.F0 = 0.4f;
            // Melee windup inside its reach: step back for a beat (only when free).
            bool meleeHop = false;
            if (m.F0 <= 0f && meleeTell && t.TellProgress >= 0.25f && free)
            {
                m.F0 = 0.3f;
                // A lunge covers the step; hop it instead. A swing: step, sometimes hop.
                meleeHop = t.Tell == ActionKind.Lunge || s.Chance(0.4f);
            }
            bool advancing  = m.F0 < 0f;
            bool retreating = m.F0 > 0f;
            if (advancing && free) { m.F0 += s.Dt; if (m.F0 > 0f) m.F0 = 0f; }
            if (retreating)        { m.F0 -= s.Dt; if (m.F0 < 0f) m.F0 = 0f; }

            // ── Dodges ─────────────────────────────────────────────────────
            bool dodgeJump = false, climbJump = meleeHop;
            if (free && grounded)
            {
                // Ball: it was aimed at where we stood on the fire frame; rise out of the line.
                if (justFired && dist > 50f) dodgeJump = true;
                // Rail: aim is frozen at windup start; be well above it at the shot.
                if (railTell && remain <= 0.26f && remain > 0.10f) dodgeJump = true;
            }

            // ── Attack choice ──────────────────────────────────────────────
            // Chain to the next barrel only while the lock is worth holding: not when the
            // misses (or a melee windup in our face) say we should be moving, and not into
            // a rail bolt we could otherwise jump (the next gun's windup + recovery would
            // still be rooting us when it fires).
            bool chainOk = m.F0 == 0f && !(railTell && remain < _windup + _recovery + 0.1f);
            // A fresh windup: from the ground with nothing incoming, or from a dodge jump
            // that is already rising (the ball passes under; this one lands in their windup).
            bool openOk = dodgeAir || (grounded && !ballInFlight && !retreating);
            bool canShoot = s.Visible && dist <= _shotMax && self.Energy >= _shotCost
                            && (firing ? chainOk : (openOk && !advancing && !shotPending));
            bool jabRange = dist <= _jabMax && MathF.Abs(to.Y) < 22f;
            // Jab when they are on top of us and not about to swing first.
            if (jabRange && free && !retreating && !(meleeTell && t.TellProgress < 0.5f))
            {
                input.WantAttack      = true;
                input.RequestedAction = JabIdx;
            }
            else if (canShoot)
            {
                input.WantAttack      = true;
                input.RequestedAction = gun;
            }
            else
            {
                input.WantAttack = false;
            }

            // ── Movement ───────────────────────────────────────────────────
            float moveX;
            if (retreating)                 moveX = -dx;
            else if (!s.Visible)            moveX = dist > _engage ? dx : 0f;    // walk to the tracked tile
            else if (dist > _shotMax - 60f) moveX = dx;                           // bring them into range
            else                            moveX = advancing && dist > 60f ? dx : 0f;
            input.MoveDir.X = moveX;

            // Stalled against a step, a block, or a tile seam the walk cannot start across:
            // hop. A shooter mid-cycle keeps the legs for the dodge, but not forever —
            // standing still in front of it is the one thing that is certainly hit.
            bool wantMove = moveX != 0f && free;
            if (m.F1 >= 0f)
            {
                m.F1 = wantMove && grounded && MathF.Abs(self.Velocity.X) < 10f ? m.F1 + s.Dt : 0f;
                if (m.F1 > (saveJump ? 1.2f : 0.2f)) { climbJump = true; m.F1 = 0f; }
            }
            // Target above a ledge and near: hop up.
            if (moveX == dx && to.Y < -24f && MathF.Abs(to.X) < 60f && !saveJump) climbJump = true;

            // Facing steers only the jump's drift and the jab (the guns aim themselves).
            // Face away while retreating, through the first tenth of any jump (a jump that
            // drifts into the step it is climbing is braked by wall friction and peaks 20 px
            // short), and for the whole of a jump under a target that stands higher.
            bool faceAway = retreating || (jumping && sinceJump < 0.1f) || (jumping && uphill);
            if (faceAway) input.AimWorld = self.Position - to;

            // A jump and a windup cannot share a frame (a committed body cannot jump), and
            // the jump is the one that was timed.
            if ((dodgeJump || climbJump) && grounded && free)
            {
                input.Jump = true;
                m.F2 = dodgeJump ? -s.Dt : s.Dt;
                input.AimWorld = self.Position - to;
                input.WantAttack = false; input.RequestedAction = null;
            }
            return input;
        }
    }
}
