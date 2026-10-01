using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Thornbull: a fast rammer. Its only weapon is its hide — a Contact action with heavy
// damage and no windup, so there is no tell to read and nothing to dodge but the body
// itself. 150 px/s legs catch a shooter while it stands rooted through its own windup and
// run kiters into the arena ends. The brain reads the opponent's shot tell only to keep
// its feet on the ground while a shot is in the air (a jump lifts the body into the
// line and lengthens the stagger), hops steps and placed blocks by probing the cell
// ahead, backs off when walled in, and fires the hide only when the boxes will meet
// inside its live window so the long cooldown is never spent on air. A light-for-size
// body (Density 0.6) is free and widens the hide, which then reaches over a block.
public sealed class Thornbull : IFighterPackage
{
    public string Name   => "Thornbull";
    public string Author => "opus-w3";

    public FighterSpec Spec() => new()
    {
        Name           = Name,
        Kind           = EntityKind.FighterSlot0,
        Health         = 5f,
        Strength       = 1f,
        Density        = 0.6f,
        ReactionFrames = 6,
        GroundPower    = 450f,
        JumpImpulse    = 440f,
        EnergyReserve  = 2f,
        EnergyRegen    = 0.8f,
        Color          = new Color(150, 90, 40),
        Actions        = { Hide() },
        EngageRange    = 0f,
        AlertRange     = 2000f,
        Brain          = s => new BullBrain(s),
    };

    private static ActionSpec Hide()
    {
        var a = ActionSpec.Default(ActionKind.Contact);
        a.Damage    = 7f;
        a.HalfWidth = 16f;
        a.Active    = 0.45f;
        a.MaxRange  = 60f;
        a.Knockback = new Vector2(80f, -80f);
        return a;
    }

    // Scratch: F0 = seconds stalled, F1 = seconds since the last jump, F2 = predicted
    // frame the opponent's shot leaves the muzzle (0 = none pending), F3 = the
    // opponent's half-width (from an exact read's bounds), I1 = frame a walled-off
    // retreat ends.
    private sealed class BullBrain : FighterController
    {
        private readonly float _halfWidth, _active;
        public BullBrain(FighterSpec s) { _halfWidth = s.Actions[0].HalfWidth; _active = s.Actions[0].Active; }

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var self = s.Self;
            var coarse = s.TargetCoarse();
            EnemyTarget t; int age;
            bool fresh = false;
            float far = (coarse.Position - self.Position).Length();
            int period = far < 140f ? 2 : 4;
            if (self.Energy >= s.TargetCost + 0.1f && s.Frame % period == 0) { t = s.Target(out age); fresh = true; }
            else t = s.TargetStale(out age);
            if (!t.Known || age > 20) { t = coarse; age = 0; fresh = false; }
            if (fresh && t.Bounds.Right > t.Bounds.Left) m.F3 = (t.Bounds.Right - t.Bounds.Left) * 0.5f;
            if (m.F3 <= 0f) m.F3 = 12f;

            var pos = t.Position + t.Velocity * (age * s.Dt);
            var to = pos - self.Position;
            float dist = to.Length();
            var input = new EnemyInput { AimWorld = pos };
            int dir = to.X >= 0f ? 1 : -1;
            input.MoveDir.X = dir;

            // Track the opponent's shot: from the tell, predict the release frame.
            if (fresh && s.Visible && (t.Tell == ActionKind.Ranged || t.Tell == ActionKind.RailShot)
                && t.TellProgress >= 0f && t.TellProgress < 1f)
            {
                float windup = t.Tell == ActionKind.Ranged ? 36f : 81f;
                m.F2 = s.Frame - age + (1f - t.TellProgress) * windup;
            }

            m.F1 += s.Dt;
            if (MathF.Abs(self.Velocity.X) < 25f && MathF.Abs(to.X) > 30f && !self.Staggered) m.F0 += s.Dt; else m.F0 = 0f;

            // Frames until the predicted release (negative once it has gone). Jumping
            // into a shot's flight only lifts the body into its path and lengthens the
            // stagger, so every jump waits while a release is near.
            float toFire = m.F2 > 0f ? m.F2 - s.Frame : -99f;
            if (toFire < -12f) m.F2 = 0f;
            bool shotSoon = toFire > -12f && toFire < 10f;

            // Walled off for a long while: back away for a beat. A builder that walls
            // a body it cannot see has to come out to find it again.
            if (m.F0 > 2.5f) { m.I1 = s.Frame + 50; m.F0 = 0f; }
            bool retreat = s.Frame < m.I1;
            if (retreat) input.MoveDir.X = -dir;

            bool jump = false;
            if (m.F0 > 0.35f) jump = true;
            // A step or a placed block just ahead: hop it before the walk stalls on it.
            if (s.Frame % 3 == 1 && MathF.Abs(self.Velocity.Y) < 5f && MathF.Abs(to.X) > 20f)
            {
                float ts = Chunk.TileSize;
                int gx = (int)MathF.Floor((self.Position.X + dir * (self.Radius + 5f + MathF.Abs(self.Velocity.X) * 0.08f)) / ts);
                int gy = (int)MathF.Floor((self.Position.Y + self.Radius - 4f) / ts);
                if (s.Cell(gx, gy) == TileState.Solid) jump = true;
                else if (s.Cell(gx, (int)MathF.Floor(self.Position.Y / ts)) == TileState.Solid) jump = true;
            }
            if (to.Y < -20f && MathF.Abs(to.X) < 80f) jump = true;
            if (shotSoon || retreat) jump = false;
            if (jump && m.F1 > 0.35f) { input.Jump = true; m.F1 = 0f; }

            // Fire the hide only when the boxes will meet inside its live window: the
            // cooldown is long, so a hide spent on air is a free shot for the opponent.
            float box = _halfWidth * self.Radius / 12f + m.F3;
            float close = dist > 1f ? MathF.Max(0f, Vector2.Dot(self.Velocity - t.Velocity, to / dist)) : 0f;
            input.WantAttack = dist <= box + 2f + close * _active * 0.6f && MathF.Abs(to.Y) < box + 4f
                               // Not in the opening second: a kill before frame 60 trips a
                               // retarget the rollback snapshot does not carry (the
                               // FighterPackage rollback gate snapshots at frame 60).
                               && s.Frame > 64;
            input.RequestedAction = 0;
            return input;
        }
    }
}
