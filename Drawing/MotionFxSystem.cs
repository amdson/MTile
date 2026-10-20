using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Render-only movement + attack particles for every player: jump dust, the air-jump
// ring, wall-jump puffs, landing dust, skid and wall-slide friction dust, speed lines,
// the dash burst/afterimage, launch smoke, stab-charge motes, and blade sparks along
// the knife's swept path. Reads sim state, writes nothing back — same contract as
// HitFeelSystem/CosmeticUpdateSystem.
//
// Triggers are rendered-frame edges and levels on final sim state (state type changed,
// HasDoubleJumped flipped, grounded flipped), the same shape as the old landing puff.
// A rollback that rewrites history between two rendered frames can at worst drop or
// double one puff — cosmetic, and far cheaper than routing every movement event through
// the presentation log. Continuous effects use per-player rate accumulators so their
// density doesn't depend on the render frame rate.
public sealed class MotionFxSystem
{
    // Speed lines start above anything locomotion reaches on its own (walk 80, air 120)
    // and saturate at the dash's burst speed.
    private const float SpeedLineMin  = 200f;
    private const float SpeedLineFull = 360f;
    private const float SkidMinSpeed  = 90f;
    private const float SkidDecel     = 700f;    // px/s² of braking that reads as a skid
    private const float LaunchSmokeMin = 220f;

    // Dash lives in the dash action family; matched by name so this file doesn't pin a
    // compile dependency on it.
    private const string DashActionName = "DashAction";
    private static readonly Color DashTint = new(170, 220, 255);

    private sealed class Track
    {
        public Type    State;
        public string  Action;
        public bool    Grounded, DoubleJumped, InSweetSpot, Seen;
        public Vector2 Vel;
        public Vector2 TipPrev; public bool TipActive;
        public float   SpeedAcc, SkidAcc, WallAcc, SmokeAcc, DashAcc, MoteAcc;
    }

    private readonly ParticleSystem          _particles;
    private readonly CharacterAnimator       _animator;
    private readonly List<CharacterAnimator> _secondaryAnimators;
    private readonly float                   _skeletonScale;
    private readonly List<Track>             _tracks = new();

    public MotionFxSystem(ParticleSystem particles, CharacterAnimator animator,
                          List<CharacterAnimator> secondaryAnimators, float skeletonScale)
    {
        _particles          = particles;
        _animator           = animator;
        _secondaryAnimators = secondaryAnimators;
        _skeletonScale      = skeletonScale;
    }

    // Stage reload: the new sim's players start fresh, so forget the old edges.
    public void Clear() => _tracks.Clear();

    public void Update(Simulation sim, float dt)
    {
        if (dt <= 0f) return;
        Player(sim.Player, 0, _animator, dt);
        var sec = sim.SecondaryPlayers;
        for (int i = 0; i < sec.Count; i++)
            Player(sec[i].Player, i + 1, i < _secondaryAnimators.Count ? _secondaryAnimators[i] : null, dt);
    }

    private void Player(PlayerCharacter p, int index, CharacterAnimator anim, float dt)
    {
        if (p == null) return;
        while (_tracks.Count <= index) _tracks.Add(new Track());
        var t = _tracks[index];

        var state    = p.CurrentState;
        var stateT   = state?.GetType();
        var action   = p.CurrentAction;
        string actName = action?.GetType().Name;
        var pos      = p.Body.Position;
        var vel      = p.Body.Velocity;
        bool grounded = p.IsGrounded;
        bool dj       = p.Abilities.HasDoubleJumped;
        var feet     = new Vector2(pos.X, pos.Y - PlayerCharacter.Radius + PlayerCharacter.StandingHeight);
        float halfW  = PlayerCharacter.Radius * PlayerCharacter.BodyWidthScale;

        if (!t.Seen)
        {
            // First sight: adopt the current state as the baseline, fire nothing.
            t.Seen = true; t.State = stateT; t.Action = actName; t.Grounded = grounded;
            t.DoubleJumped = dj; t.Vel = vel;
            return;
        }

        bool entered = stateT != t.State;

        // ── Take-offs ─────────────────────────────────────────────────────────
        if (entered)
        {
            switch (state)
            {
                case JumpingState:        Effects.JumpDust(_particles, feet, vel.X); break;
                case RunningJumpState:    Effects.JumpDust(_particles, feet, vel.X, 1.25f); break;
                case LedgeJumpState:      Effects.JumpDust(_particles, feet, vel.X, 0.6f); break;
                case WallJumpingState wj:
                    Effects.WallJumpPuff(_particles, pos + new Vector2(wj.WallDir * halfW, 4f), wj.WallDir);
                    break;
            }
        }
        if (dj && !t.DoubleJumped)
            Effects.AirJumpRing(_particles, feet, vel.X);

        // ── Landing ───────────────────────────────────────────────────────────
        if (grounded && !t.Grounded)
            Effects.LandingDust(_particles, feet, MathF.Max(t.Vel.Y, 0f));

        // ── Ground skid: hard braking against the current travel ──────────────
        if (grounded && MathF.Abs(t.Vel.X) > SkidMinSpeed
            && (vel.X - t.Vel.X) * MathF.Sign(t.Vel.X) < -SkidDecel * dt)
        {
            if (Tick(ref t.SkidAcc, 40f, dt) > 0) Effects.SkidDust(_particles, feet, t.Vel.X);
        }

        // ── Wall-slide friction ───────────────────────────────────────────────
        if (state is WallSlidingState ws && vel.Y > 40f)
        {
            int n = Tick(ref t.WallAcc, 12f + vel.Y * 0.1f, dt);
            for (int i = 0; i < n; i++)
                Effects.WallSlideDust(_particles, pos + new Vector2(ws.WallDir * halfW, 6f), ws.WallDir, vel.Y);
        }

        // ── Speed lines ───────────────────────────────────────────────────────
        float speed = vel.Length();
        if (speed > SpeedLineMin)
        {
            float s = MathHelper.Clamp((speed - SpeedLineMin) / (SpeedLineFull - SpeedLineMin), 0f, 1f);
            int n = Tick(ref t.SpeedAcc, 20f + 60f * s, dt);
            if (n > 0) Effects.SpeedLines(_particles, pos, vel, PlayerCharacter.Radius, s, n);
        }

        // ── Dash ──────────────────────────────────────────────────────────────
        bool dashing = actName == DashActionName;
        if (dashing && t.Action != DashActionName)
        {
            var dir = speed > 1f ? vel / speed : new Vector2(p.Abilities.Facing, 0f);
            Effects.DashBurst(_particles, pos, dir, grounded, feet);
        }
        if (dashing && Tick(ref t.DashAcc, 50f, dt) > 0)
            Effects.DashTrail(_particles, pos, vel, DashTint);

        // ── Launched: smoke behind a body sent flying ─────────────────────────
        bool launched = state is TumbleState
                     || (p.Combat != null && p.Combat.HitstunActive && !grounded && speed > LaunchSmokeMin);
        if (launched && speed > 60f)
        {
            float s = MathHelper.Clamp((speed - 60f) / 400f, 0f, 1f);
            int n = Tick(ref t.SmokeAcc, 25f + 35f * s, dt);
            for (int i = 0; i < n; i++) Effects.LaunchSmoke(_particles, pos, vel, s);
        }

        // ── Stab charge: motes gather onto the hand, flash on the sweet spot ──
        bool sweet = false;
        if (action is RecoveryAction && p.CurrentActionVars.Charging)
        {
            var v = p.CurrentActionVars;
            float f = RecoveryAction.ChargeFraction(v.TimeInState);
            var hue = v.IsGrounded ? new Color(255, 210, 110) : new Color(200, 140, 255);
            var hand = AttackGlowSystem.ChargeCenter(p, anim, _skeletonScale);
            int n = Tick(ref t.MoteAcc, 20f + 50f * f, dt);
            for (int i = 0; i < n; i++) Effects.ChargeMotes(_particles, hand, hue, f);
            sweet = RecoveryAction.InSweetSpot(v.TimeInState);
            if (sweet && !t.InSweetSpot) Effects.Flash(_particles, hand, 34f, Color.White, 0.1f);
        }

        // ── Blade sparks along the knife's swept segment ──────────────────────
        if (anim != null && AttackGlowSystem.TryAttackTipPos(p, anim, _skeletonScale, out var tip))
        {
            if (t.TipActive)
                Effects.BladeSparks(_particles, t.TipPrev, tip,
                                    AttackGlowSystem.AttackGlowColor(action, p.CurrentActionVars), dt);
            t.TipPrev = tip; t.TipActive = true;
        }
        else t.TipActive = false;

        t.State = stateT; t.Action = actName; t.Grounded = grounded; t.DoubleJumped = dj;
        t.Vel = vel; t.InSweetSpot = sweet;
    }

    // Rate accumulator: returns how many emissions `rate`/s owes this frame.
    private static int Tick(ref float acc, float rate, float dt)
    {
        acc += rate * dt;
        int n = (int)acc;
        acc -= n;
        return n;
    }
}
