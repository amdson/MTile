using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Hollowstep: a counter-puncher. An exhibition bot by the wave-3 coordinator, written with
// every wave-3 report in hand — not a competition entry.
//
// The bet: the rammers (a one-touch Contact on fast legs) beat anything that stands and
// shoots, but a touch needs the bodies to overlap, and a swing reaches further than a touch
// box. Hollowstep holds its ground against a charging body and starts its swing so that the
// active window opens as the charger arrives at reach: the hit staggers it, which cancels the
// touch, and a second swing kills it. It carries enough health to survive one touch if the
// timing is off by a few frames. Against shooters it is Thornrush's reader: one exact look at
// a windup pins the release frame, it hops on that frame, and it fires its own ball into the
// shooter's recovery. It walks shooters down behind its own ball and finishes with the swing.
public sealed class Hollowstep : IFighterPackage
{
    public string Name   => "Hollowstep";
    public string Author => "coordinator-x";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 8.0f,
        Strength       = 1.45f,
        Density        = 1.0f,
        ReactionFrames = 4,
        GroundPower    = 200f,
        JumpImpulse    = 500f,
        EnergyReserve  = 3f,
        EnergyRegen    = 1.3f,
        Color          = new Color(90, 120, 200),
        Actions        = { Swing(), Shot() },
        EngageRange    = 20f,
        AlertRange     = 2000f,
        Brain          = s => new StepBrain(s),
    };

    private static ActionSpec Swing()
    {
        var a = ActionSpec.Default(ActionKind.Melee);
        a.Damage = 1.7f; a.Reach = 24f; a.MaxRange = 46f;
        return a;
    }

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.MinRange = 30f; a.MaxRange = 360f; a.Speed = 520f; a.Damage = 1.5f;
        return a;
    }

    // Scratch: I0 = frames to the next exact read (low 4 bits) | 16 once the target has been
    // seen closing fast (a rammer) | 32 while the last probe found the edge behind us,
    // I1 = stalled-chase frames, F0 = seconds since the last hop,
    // F1 = frame the opponent's projectile leaves (0 = none), F2 = frame its swing lands
    // (0 = none), F3 = frame our last ball left.
    private sealed class StepBrain : FighterController
    {
        private readonly float _swingMax, _shotCost, _shotMin, _shotMax, _shotSpeed;
        public StepBrain(FighterSpec s)
        {
            _swingMax  = s.Actions[0].MaxRange;
            _shotCost  = s.Actions[1].EnergyCost; _shotMin = s.Actions[1].MinRange; _shotMax = s.Actions[1].MaxRange;
            _shotSpeed = s.Actions[1].Speed;
        }

        private const float ChargeSpeed = 70f;    // closing px/s that marks a charger
        private static bool edgeKnownRecently(int flags) => false;   // probes are cheap enough at 1/20 frames
        private const float FastBall    = 800f, HopRise = 0.2f;
        private const int   StallFrames = 4;
        private static float WindupFrames(ActionKind k) => ActionSpec.Default(k).Windup * 60f;

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self   = s.Self;
            var coarse = s.TargetCoarse();
            float cd   = (coarse.Position - self.Position).Length();

            // ── Reads: every 3 frames when close, every 6 when far; the meter is for balls
            // (one is 1.5 energy), so reads slow down while a ball is unaffordable. ──
            int readIn = m.I0 & 15, flags = m.I0 & ~15;
            EnemyTarget t; int age;
            bool starving = self.Energy < 1.6f;
            if (readIn <= 0 && self.Energy >= s.TargetCost + 0.1f) { t = s.Target(out age); readIn = cd < 220f ? (starving ? 4 : 3) : 6; }
            else { t = s.TargetStale(out age); if (readIn > 0) readIn--; }
            bool exact = t.Known && age <= 10;
            if (!exact) { t = coarse; age = 6; }

            var to = t.Position - self.Position;
            if (exact) to += t.Velocity * (age * s.Dt);
            float d  = to.Length();
            float sx = to.X >= 0f ? 1f : -1f;
            // The target's own speed toward us (positive = it comes at us). Our walk is left
            // out on purpose: approaching a turret must not read as being charged.
            float closing = exact ? -t.Velocity.X * sx : 0f;
            if (exact && closing > ChargeSpeed && d < 260f) flags |= 16;
            bool charger = (flags & 16) != 0;

            m.F0 += s.Dt;
            float frame = s.Frame;

            // ── Tell reading: one fresh look pins the release / strike frame. ──
            if (exact && age <= 8 && s.Visible)
            {
                var k = t.Tell;
                bool proj  = k is ActionKind.Ranged or ActionKind.RailShot;
                bool melee = k is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash;
                bool winding = t.TellProgress >= 0f && t.TellProgress < 1f;
                m.F1 = proj  && winding ? frame - age + (1f - t.TellProgress) * WindupFrames(k) : (proj  ? m.F1 : 0f);
                m.F2 = melee && winding ? frame - age + (1f - t.TellProgress) * WindupFrames(k) : (melee ? m.F2 : 0f);
            }
            float projLeft  = m.F1 > 0f ? m.F1 - frame : 999f;
            float meleeLeft = m.F2 > 0f ? m.F2 - frame : 999f;
            float hopAt = -MathF.Max(0f, (d / FastBall - HopRise) * 60f);
            if (projLeft  < hopAt - 4f) { m.F1 = 0f; projLeft  = 999f; }
            if (meleeLeft < -12f)       { m.F2 = 0f; meleeLeft = 999f; }

            var input = new EnemyInput { AimWorld = self.Position + to, WantAttack = true };
            bool grounded = MathF.Abs(self.Velocity.Y) < 1f;
            bool idle     = !self.ActionCommitted;
            bool canShoot = s.Visible && self.Energy >= _shotCost && d >= _shotMin && d <= _shotMax && idle;
            float swingFrames = WindupFrames(ActionKind.Melee);
            int? request = null; bool attack = false, dodgeHop = false;

            // ── A charger: a melee cannot start outside its band (the ordering rule), so a
            // counter-swing is impossible; what works is the ball's stagger (half a second) plus
            // its slow legs. Retreat while it comes, fire whenever the ball lands before it
            // arrives, swing only when it is already in reach. Never retreat off the edge.
            float shotFramesNow = WindupFrames(ActionKind.Ranged) + (d / _shotSpeed) * 60f + 2f;
            if (charger && exact && d < 170f)
            {
                float reach  = _swingMax - 4f;
                float arrive = closing > 1f ? MathF.Max(0f, d - reach) / closing * 60f : 999f;
                bool  edge   = false;
                if (frame % 20 == 0 && self.Energy >= s.ProbeCost + 0.5f && !edgeKnownRecently(flags))
                {
                    var pr = s.Probe(sx > 0f ? -1 : 1);
                    edge = pr.Known && pr.DropAhead < 50f;
                    if (edge) flags |= 32; else flags &= ~32;
                }
                else edge = (flags & 32) != 0;
                if (d < reach && idle) { request = 0; attack = true; input.MoveDir.X = 0f; }
                else
                {
                    input.MoveDir.X = edge ? 0f : -sx;
                    if (idle && canShoot && arrive > shotFramesNow + 6f) { request = 1; attack = true; }
                }
            }
            else if (charger && exact && closing < -40f && d < 120f && idle && canShoot)
            {
                // A rammer dancing at arm's length hops a long flight; a short one it cannot.
                request = 1; attack = true;
            }
            else
            {
                input.MoveDir.X = MathF.Abs(to.X) > 4f ? sx : 0f;
                if (d < _swingMax && idle) { request = 0; attack = true; }
            }

            // ── Their swing: step out of it unless ours lands first. ──
            if (meleeLeft < 900f && d < 70f && !attack)
            {
                if (idle && d < _swingMax && meleeLeft > swingFrames + 1f) { request = 0; attack = true; }
                else if (meleeLeft > -6f) { input.MoveDir.X = -sx; if (d < 40f && meleeLeft < 8f) input.Jump = true; }
            }

            // ── Their ball: hop on the release frame; otherwise fire into the gap. A hop lifts
            // about 24 px in 0.15 s, so a fast ball from inside ~150 px cannot be hopped: keep
            // walking in instead (a turret is beaten by the swing, not the dodge). ──
            bool hoppable = d >= 150f;
            if (projLeft < 900f && !attack)
            {
                float shotFrames = WindupFrames(ActionKind.Ranged) + (d / _shotSpeed) * 60f + 3f;
                if (canShoot && projLeft > shotFrames) { request = 1; attack = true; }
                else if (hoppable && projLeft <= hopAt && m.F0 > 0.3f && grounded) { input.Jump = true; m.F0 = 0f; dodgeHop = true; }
            }

            // ── Phase shooting against a shooter: fire as its ball leaves (its recovery is next). ──
            if (!attack && canShoot && !charger)
            {
                bool justFired = exact && t.Tell == ActionKind.Ranged && t.TellProgress >= 1f;
                bool airShot   = m.F0 < 0.25f && !grounded;
                bool finishing = exact && t.Health <= 1.01f;
                if (justFired || airShot || finishing || (projLeft > 900f && frame - m.F3 > 90f && d > 120f))
                { request = 1; attack = true; }
            }
            if (attack && request == 1) m.F3 = frame;

            // ── Ledges, walls, stalls. ──
            if (to.Y < -18f && MathF.Abs(to.X) < 60f && !charger) input.Jump = true;
            if (grounded && MathF.Abs(self.Velocity.X) < 3f && MathF.Abs(to.X) > 30f && input.MoveDir.X != 0f && idle) m.I1++;
            else m.I1 = 0;
            if (m.I1 > StallFrames && m.I1 % 20 < 10) input.Jump = true;
            // Keep the legs for a pending dodge.
            if (hoppable && projLeft < 46f && projLeft > hopAt && !dodgeHop) input.Jump = false;
            // Never chase over the void.
            if (to.Y > 7f * Chunk.TileSize) { input.MoveDir.X = 0f; input.Jump = false; }

            // Forget a charger that has stopped coming (so we walk shooters down again).
            if (exact && closing < -40f && d > 220f) flags &= ~16;

            m.I0 = readIn | flags;
            input.WantAttack      = attack;
            input.RequestedAction = request;
            return input;
        }
    }
}
