using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Thornrush: a fast, spiked rammer with a ball for what its legs cannot reach.
//
// Contact damage has no windup, so it cannot be read off a tell, and any hit staggers —
// which cancels whatever the opponent was winding up. Thornrush runs the opponent down
// (141 px/s, faster than any kiter walks) and touches for 3.
//
// Windups are fixed per kind, so one fresh read of a tell pins the frame it goes off.
// A ball is aimed where we stand as it leaves, so Thornrush hops on exactly that frame
// (only the lift dodges it) and fires its own ball from the top of the hop: it leaves
// as the shooter's recovery ends, which catches a shooter that chains straight into its
// next windup — rooted, unable to hop. A long windup (a rail) is outrun or out-shot
// before it fires; a swing is touched first when the spikes reach in time, else
// stepped away from, and a swinger is kept at arm's length while the spikes recharge.
public sealed class Thornrush : IFighterPackage
{
    public string Name   => "Thornrush";
    public string Author => "opus-w3";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 5f,
        Strength       = 1f,
        Density        = 1.2f,
        ReactionFrames = 4,
        TargetMemory   = true,
        GroundPower    = 400f,
        JumpImpulse    = 600f,
        EnergyReserve  = 3f,
        EnergyRegen    = 1.0f,
        Color          = new Color(150, 200, 90),
        Actions        = { Spikes(), Shot() },
        EngageRange    = 10f,
        AlertRange     = 2000f,
        Brain          = s => new ThornBrain(s),
    };

    private static ActionSpec Spikes()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage = 3f; a.HalfWidth = 16f; a.MaxRange = 30f;
        a.Knockback = new Vector2(120f, -80f);
        return a;
    }

    private static ActionSpec Shot()
    {
        var a = ActionSpec.Default(ActionKind.Ranged);
        a.MinRange = 70f; a.MaxRange = 380f; a.Speed = 600f;
        return a;
    }

    // Scratch: I0 = frames until the next exact read (low 4 bits) | 16 once the target
    // has been seen swinging | 32 while its last shot was a rail, I1 = frames the chase has stalled, F0 = seconds since the
    // last dodge hop, F1 = frame the opponent's projectile leaves (0 = none pending),
    // F2 = frame its melee-band strike lands (0 = none), F3 = hold-at-the-edge deadline.
    //
    // The compiled mass (the --spec total); the brain uses it for walk acceleration.
    private const float BodyMass = 2.48f;

    private sealed class ThornBrain : FighterController
    {
        private readonly float _touch, _shotCost, _shotMin, _shotMax, _shotSpeed, _speed, _accel;
        public ThornBrain(FighterSpec s)
        {
            _touch = s.Actions[0].MaxRange;
            _shotCost = s.Actions[1].EnergyCost; _shotMin = s.Actions[1].MinRange; _shotMax = s.Actions[1].MaxRange;
            _shotSpeed = s.Actions[1].Speed;
            _speed = MathF.Sqrt(s.GroundPower / 0.02f);
            _accel = s.GroundPower / BodyMass;
        }

        // Tuning. HopLead: frames before the release to leave the ground (0 = on it).
        private const float HopLead = 0f, SaveFrames = 46f, RushSlack = 3f, BackOff = 55f;
        private const float FastBall = 800f, HopRise = 0.2f;   // px/s; seconds of lift a dodge needs
        private const int   StallFrames = 3;

        // Ground we can cover in `t` seconds from closing speed `v0`, accelerating to top speed.
        private float Cover(float v0, float t)
        {
            if (t <= 0f) return 0f;
            v0 = MathF.Max(v0, 0f);
            float tTop = (_speed - v0) / _accel;
            if (tTop >= t) return v0 * t + 0.5f * _accel * t * t;
            return v0 * tTop + 0.5f * _accel * tTop * tTop + _speed * (t - tTop);
        }

        // Windup frames per kind (fixed by the arena: ActionSpec.Default).
        private static float WindupFrames(ActionKind k) => ActionSpec.Default(k).Windup * 60f;

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            float cd = (coarse.Position - self.Position).Length();

            EnemyTarget t; int age;
            // I0: low 4 bits count down to the next exact read; bit 4 = the target swings.
            int readIn = m.I0 & 15, flags = m.I0 & ~15;
            if (readIn <= 0 && self.Energy >= s.TargetCost + 0.2f)
            {
                t = s.Target(out age);
                readIn = cd < 200f ? 2 : 5;
            }
            else { t = s.TargetStale(out age); if (readIn > 0) readIn--; }
            bool exact = t.Known && age <= 10;
            if (!exact) { t = coarse; age = 6; }

            var to = t.Position - self.Position;
            if (exact) to += t.Velocity * (age * s.Dt);
            float d = to.Length();
            float sx = to.X >= 0f ? 1f : -1f;
            m.F0 += s.Dt;
            float frame = s.Frame;

            // ── Read the opponent's windup: one fresh look pins when it goes off. ──
            if (exact && age <= 8 && s.Visible)
            {
                var k = t.Tell;
                bool proj  = k is ActionKind.Ranged or ActionKind.RailShot;
                bool melee = k is ActionKind.Melee or ActionKind.Lunge or ActionKind.Lash;
                if (melee) flags |= 16;
                if (proj) flags = k == ActionKind.RailShot ? flags | 32 : flags & ~32;
                bool winding = t.TellProgress >= 0f && t.TellProgress < 1f;
                m.F1 = proj  && winding ? frame - age + (1f - t.TellProgress) * WindupFrames(k) : (proj ? m.F1 : 0f);
                m.F2 = melee && winding ? frame - age + (1f - t.TellProgress) * WindupFrames(k) : (melee ? m.F2 : 0f);
            }
            float projLeft  = m.F1 > 0f ? m.F1 - frame : 999f;
            float meleeLeft = m.F2 > 0f ? m.F2 - frame : 999f;
            // A long flight (a ball from far off) can land after a hop at the release has come
            // back down: delay the hop by what even a fast ball still takes beyond the lift.
            // A rail bolt is never slow.
            float hopAt = (flags & 32) == 0
                ? HopLead - MathF.Max(0f, (d / FastBall - HopRise) * 60f) : HopLead;
            if (projLeft < hopAt - 4f) { m.F1 = 0f; projLeft = 999f; }
            if (meleeLeft < -12f) { m.F2 = 0f; meleeLeft = 999f; }

            var input = new EnemyInput { AimWorld = self.Position + to, WantAttack = true };
            input.MoveDir.X = MathF.Abs(to.X) > 4f ? sx : 0f;

            bool grounded = MathF.Abs(self.Velocity.Y) < 1f;
            bool contactReady = self.CurrentAction != ActionKind.Contact;
            float gap = MathF.Max(0f, d - _touch);
            bool canShoot = s.Visible && self.Energy >= _shotCost && d >= _shotMin && d <= _shotMax;
            float shotFrames = WindupFrames(ActionKind.Ranged) + (d / _shotSpeed) * 60f + 3f;
            int? request = null; bool dodgeHop = false;
            bool attack = false;

            if (meleeLeft < 900f && d < 80f)
            {
                // A swing is coming. Touch first if we can (a hit staggers it away), else step out.
                if (contactReady && gap < Cover(self.Velocity.X * sx, meleeLeft * s.Dt) + RushSlack) { request = 0; attack = d < _touch; }
                else if (meleeLeft > -6f)
                {
                    // Step out; a hop drifts toward where we face, so face away for it.
                    input.MoveDir.X = -sx;
                    if (d < 40f && meleeLeft < 8f) { input.Jump = true; input.AimWorld = self.Position - new Vector2(sx * 40f, 0f); }
                }
            }
            else if (projLeft < 900f)
            {
                if (gap < Cover(self.Velocity.X * sx, projLeft * s.Dt) * 0.9f) { /* rush: we touch before it leaves */ }
                else if (canShoot && projLeft > shotFrames && !self.ActionCommitted) { request = 1; attack = true; }
                else if (projLeft <= hopAt && m.F0 > 0.3f && grounded) { input.Jump = true; m.F0 = 0f; dodgeHop = true; }
            }

            // A swinger gets touched, then kept at arm's length until the spikes are ready.
            else if ((flags & 16) != 0 && !contactReady && d < BackOff) input.MoveDir.X = -sx;

            // Fire from the top of a dodge hop: the ball leaves as their recovery ends.
            bool airShot = m.F0 < 0.25f && MathF.Abs(self.Velocity.Y) > 1f;
            if (!attack)
            {
                if (d < _touch && contactReady) { request = 0; attack = true; }
                else if (airShot && canShoot) { request = 1; attack = true; }
                else if (canShoot && self.Energy >= _shotCost + 0.6f && (to.Y < -60f || m.I1 > 40)) { request = 1; attack = true; }
                // A target running for it (often toward the void) gets a ball in the back.
                else if (canShoot && exact && t.Velocity.X * sx > 30f && d > 100f && projLeft > 900f
                         && t.Health <= 1.01f)
                { request = 1; attack = true; }
            }

            // Step up ledges / over walls: target above, or the chase has stalled.
            if (to.Y < -18f && MathF.Abs(to.X) < 60f) input.Jump = true;
            if (grounded && MathF.Abs(self.Velocity.X) < 3f && MathF.Abs(to.X) > 30f && input.MoveDir.X != 0f && !self.ActionCommitted) m.I1++;
            else m.I1 = 0;
            if (m.I1 > StallFrames && m.I1 % 20 < 10) input.Jump = true;
            // Save the legs for the dodge: a ledge hop now would leave us falling, unable
            // to hop, when the pending ball leaves.
            if (projLeft < SaveFrames && projLeft > hopAt && !dodgeHop) input.Jump = false;

            // The arena ends in a void: never follow a target that has gone over an edge.
            bool below = to.Y > 7f * Chunk.TileSize;
            if (below && MathF.Abs(self.Velocity.Y) < 1f && input.MoveDir.X != 0f && s.Frame % 6 == 0
                && self.Energy >= s.ProbeCost + 1f)
            {
                var pr = s.Probe(input.MoveDir.X > 0f ? 1 : -1);
                if (pr.Known && pr.DropAhead < 2.5f * Chunk.TileSize) m.F3 = frame + 7f;
            }
            if (below && frame < m.F3) { input.MoveDir.X = 0f; input.Jump = false; }

            m.I0 = readIn | flags;
            input.WantAttack = attack;
            input.RequestedAction = request;
            return input;
        }
    }
}
