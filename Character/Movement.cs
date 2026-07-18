using System;
using Microsoft.Xna.Framework;

namespace MTile;

public abstract class MovementState
{
    public abstract int ActivePriority { get; }
    public abstract int PassivePriority { get; }

    // Capabilities this state needs to be ENTERED. The selection loop skips this state
    // as a candidate while any required capability is in the frame's blocked mask
    // (currently: combat hitstun/stun blocks Jump). Does NOT gate continuation — a
    // running state keeps running via CheckConditions regardless. See MovementCapability.
    public virtual MovementCapability RequiredCapabilities => MovementCapability.None;

    // Lets the currently-active state (and, for one frame after it exits, the
    // just-departed state — the loop queries PreviousState(0)) veto specific candidates
    // that priority alone would let win. Used to keep an owned maneuver (e.g. an
    // in-progress ledge pull) from being stolen by a higher-passive bystander. Default:
    // suppress nothing.
    public virtual bool Suppresses(MovementState candidate, EnvironmentContext ctx) => false;

    // CheckPreConditions (candidate selection) reads only ctx + abilities, never the
    // current activation's vars — so it keeps the lean signature. The lifecycle
    // methods below run on the active/transitioning state and carry MovementVars,
    // the plain-data per-activation state (see MovementVars).
    public abstract bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities);
    public abstract bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars);

    public virtual void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars) {}
    public virtual void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars) {}

    public abstract void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars);

    // Snapshot/restore hook (roadmap goal 4). The only per-player instance data left
    // on a movement state is its transient soft-contact ref cache(s) (_ground,
    // _source, _wall, …). A restore drops the soft contacts from Body.Constraints
    // (only Maintained hard contacts survive — see BodyState), so these caches would
    // be left dangling. PlayerCharacter.RestoreState calls this on every registry
    // state to null them; the owning state's idempotent Ensure… then rebuilds its
    // contact on the next Update from the restored body pose. No-op for stateless
    // states (Falling, Stunned, jumps without a source cache).
    public virtual void ResetTransient() { }

    // The animation-facing CATEGORY of this state (AnimTag.None = generic: the animator picks
    // by grounded/velocity). Replaces substring matching on state class names, which silently
    // broke on renames and false-matched future states. Same render-only contract as the
    // virtuals below: the sim never reads it.
    public virtual AnimTag AnimationTag => AnimTag.None;

    // Normalized progress [0,1] of a guided maneuver, exposed to the animation layer for
    // overlays whose clip time is driven by SPATIAL progress rather than a clock — a vault
    // advances by body position vs. the ledge corner, not elapsed time, so its hand overlay
    // can't be timed off ActionTime. Default 0 (states with no natural progress). Render-only:
    // the sim never reads it; it is derived from deterministic body/world data each Update.
    public virtual float AnimationProgress => 0f;

    // A world point a limb should GRIP during a guided maneuver — the ledge corner a vault hand
    // reaches for. The animation layer turns this into a FixedPoint pin (which bone is animation
    // policy, see CharacterAnimator) so the hand lands exactly on the feature instead of just
    // playing an approximate canned reach. Default none. Render-only, same contract as
    // AnimationProgress: derived from deterministic body/world data, the sim never reads it.
    public virtual bool TryAnimationGrip(out Vector2 target) { target = default; return false; }
}

// Heavy-hit lock-out. Preempts Standing/Crouched/WallSliding/Falling so the
// muted air-control profile applies as soon as a stun-flagged hit lands. Does
// NOT preempt active jumps (50+) — a player hit mid-jump finishes the existing
// arc and only enters StunnedState after Falling takes over.
//
// While stunned:
//   - Horizontal accel × 0.4, max-air-speed × 0.7, air-drag × 1.5 — player can
//     nudge but can't redirect the knockback trajectory.
//   - Action FSM gates (Slash*, Stab) refuse to fire (gated on Combat.StunActive).
//   - HitstunActive is also true throughout (every hit sets it), keeping the
//     jump preconditions blocked even past the 8-frame hitstun base window.
//
// State holds no constraints — physics handles ground/wall contact through
// the world's collision resolver. HasDoubleJumped is NOT reset on exit; a
// player stunned out of a double-jump doesn't suddenly regain it.
public class StunnedState : MovementState
{
    public override int ActivePriority  => MovementPriorities.StunnedActive;
    public override int PassivePriority => MovementPriorities.StunnedPassive;

    // Recoil flinch, not the generic ground clips: without this the muted-control window
    // is invisible (a stunned body sliding under knockback reads as a walk cycle).
    public override AnimTag AnimationTag => AnimTag.Stunned;

    // Grounded-only since Phase 4: an airborne heavy hit goes to TumbleState (launch
    // band) instead, so a launched body can't be rescued by terrain. A grounded
    // stun (horizontal hit, body stays on the floor) still lands here. When a
    // grounded stun gets knocked airborne mid-window, this CheckConditions drops
    // (→ Falling) and TumbleState's higher passive grabs the body.
    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => ctx.Combat?.StunActive == true && ctx.TryGetGround(out _);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => ctx.Combat?.StunActive == true && ctx.TryGetGround(out _);

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        var force = Vector2.Zero;
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel    * 0.4f,
            cfg.MaxAirSpeed * m.MaxAirSpeed * 0.7f,
            cfg.AirDrag     * m.AirDrag     * 1.5f);

        ctx.Body.AppliedForce = force;
    }
}

// Airborne heavy-hit launch (COMBAT_FEEL_PLAN Phase 4). A hit whose impulse crosses
// the stun threshold sets StunActive; while the victim is airborne that becomes a
// Tumble rather than a grounded Stun. Tumble lives in the launch band (Active 51) so
// once launched the body stays tumbling until it lands or techs — combined with the
// capability mask (which blocks WallCling/LedgeGrab during stun/hitstun) this is what
// makes a knockback into a juggle/edgeguard instead of a free wall-cling reset.
//
// Control is muted air-control (DI only), like StunnedState. PreserveExternalVelocity
// is forced on so the muted speed cap never brakes the launch even in the stun tail
// after hitstun lapses.
//
// Tech (defensive option): a buffered Jump intent while a surface is within the tech
// probe (just before landing) ends the launch early, grants brief i-frames, and pops
// the body up — so a read launch can be survived with precise timing. Outside that
// window the body just rides the tumble down and eats the landing.
public class TumbleState : MovementState
{
    // Tech window: ground detected within this slack below the body (but the body
    // isn't yet "grounded" by the normal 20px probe, which would exit Tumble) opens
    // the tech window — roughly the last few frames of the descent.
    private const float TechProbeSlack   = 60f;
    private const float TechInvulnSeconds = 0.25f;
    private const float TechBounceVy     = 260f;   // upward pop on a successful tech
    private const float TechHorizKeep    = 0.3f;   // fraction of horizontal speed kept

    public override int ActivePriority  => MovementPriorities.TumbleActive;
    public override int PassivePriority => MovementPriorities.TumblePassive;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => ctx.Combat?.StunActive == true && !ctx.TryGetGround(out _);

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => ctx.Combat?.StunActive == true && !ctx.TryGetGround(out _);

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Tech: buffered jump + a surface within the tech probe ⇒ bail the launch.
        if (ctx.Combat != null
            && ctx.Intents.Peek(IntentType.Jump, ctx.CurrentFrame, out _, ctx.JumpBufferFrames)
            && GroundChecker.TryFind(ctx.Body, ctx.Chunks,
                   PlayerCharacter.Radius, PlayerCharacter.Radius,
                   TechProbeSlack, ctx.Dt, out _))
        {
            ctx.Intents.Consume(IntentType.Jump, ctx.CurrentFrame, ctx.JumpBufferFrames);
            ctx.Combat.Tech(ctx.CurrentFrame, ctx.Dt, TechInvulnSeconds);
            ctx.Body.Velocity = new Vector2(ctx.Body.Velocity.X * TechHorizKeep, -TechBounceVy);
            ctx.Body.AppliedForce = Vector2.Zero;
            return;
        }

        // Launch must never be braked by the muted speed cap (the stun tail can
        // outlive hitstun, which is what otherwise forces PreserveExternalVelocity).
        ctx.Modifiers.PreserveExternalVelocity = true;

        var force = Vector2.Zero;
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel    * 0.4f,
            cfg.MaxAirSpeed * m.MaxAirSpeed * 0.7f,
            cfg.AirDrag     * m.AirDrag     * 1.5f);

        ctx.Body.AppliedForce = force;
    }
}

public class FallingState : MovementState
{
    public override int ActivePriority => MovementPriorities.FallingActive;
    public override int PassivePriority => MovementPriorities.FallingPassive;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities) => true;
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars) => true;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        var force = Vector2.Zero;
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;
        force.X = AirControl.Apply(ctx,
            cfg.AirAccel    * m.AirAccel,
            cfg.MaxAirSpeed * m.MaxAirSpeed,
            cfg.AirDrag     * m.AirDrag);

        if (ctx.Input.Down)
            force.Y += cfg.FastFallForce;

        ctx.Body.AppliedForce = force;
    }
}

public class StandingState : MovementState
{
    public override int ActivePriority => MovementPriorities.StandingActive;
    public override int PassivePriority => MovementPriorities.StandingPassive;

    private FloatingSurfaceDistance _ground;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return IsStandingGround(ctx);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        // Stay-active uses plain ground detection — only ENTRY is gated (below). An
        // already-standing body that's briefly flung up (e.g. a sprout growing up
        // under it) must keep Standing so its spring tracks the lift; kicking it to
        // Falling for a frame would slam its velocity. The launch case the entry gate
        // guards against never has Standing as the current state (the jump does).
        return ctx.TryGetGround(out _);
    }

    // GroundChecker's 20px ProbeSlack reports "grounded" for a body up to ~20px above
    // rest height — which, with the slow JumpVelocity launch, holds for the whole jump
    // window. So a quick jump-release would drop JumpingState and let Standing re-grab
    // the still-ascending body, subjecting it to ground friction + the anti-pop rise
    // clamp below ("hit a ceiling" dead-end). Refuse to grab a body rising faster than
    // the spring would ever push it (SpringMaxRiseSpeed): that's a launch, not standing.
    // Landing bodies descend (riseSpeed ≤ 0), so the soft-landing catch zone is untouched.
    private static bool IsStandingGround(EnvironmentContext ctx)
    {
        if (!ctx.TryGetGround(out var ground)) return false;
        float riseSpeed = Vector2.Dot(ctx.Body.Velocity - ground.SurfaceVelocity, ground.Normal);
        return riseSpeed <= MovementConfig.Current.SpringMaxRiseSpeed;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => EnsureGround(ctx);

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_ground != null)
            ctx.Body.Constraints.Remove(_ground);
        _ground = null;
    }

    // Idempotent ground-contact acquisition. Called from Enter and the top of Update
    // so the (non-snapshotted) soft contact self-heals after a restore drops it.
    // No-op in normal play, where Enter already established it.
    private void EnsureGround(EnvironmentContext ctx)
    {
        if (_ground != null) return;
        if (ctx.TryGetGround(out var contact))
        {
            _ground = contact;
            ctx.Body.Constraints.Add(_ground);
        }
    }

    public override void ResetTransient() => _ground = null;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureGround(ctx);
        if (ctx.TryGetGround(out var refreshed))
        {
            _ground.Position        = refreshed.Position;
            _ground.Normal          = refreshed.Normal;
            _ground.MinDistance     = refreshed.MinDistance;
            _ground.SurfaceVelocity = refreshed.SurfaceVelocity;
        }
        // Refresh friction every frame so action-driven modifiers (e.g. Stab's
        // lunge dip) take effect immediately without needing a state re-entry.
        _ground.Friction = MovementConfig.Current.GroundFriction * ctx.Modifiers.GroundFriction;

        var force = Vector2.Zero;
        var cfg = MovementConfig.Current;
        var m   = ctx.Modifiers;

        // Spring damping uses *relative* normal velocity so the body's
        // surface-matched motion (carried by a moving platform) isn't damped
        // back to zero — only deviations from the surface are.
        float dist           = Vector2.Dot(ctx.Body.Position - _ground.Position, _ground.Normal);
        float gap            = _ground.MinDistance - dist;
        float velAlongNormal = Vector2.Dot(ctx.Body.Velocity - _ground.SurfaceVelocity, _ground.Normal);
        if (gap > 0f)
            force += _ground.Normal * (gap * cfg.SpringK - velAlongNormal * cfg.SpringDamping);
        // Anti-pop clamp: cap the rise only while at/below float height (gap > 0), where
        // the spring above could be flinging the body up. Once it's risen above rest
        // height (gap < 0 — e.g. an early jump-release leaving Standing momentarily in
        // charge of an ascending body), braking the ascent would dead-end it like a
        // ceiling, so leave it alone.
        float velExcess = velAlongNormal - cfg.SpringMaxRiseSpeed;
        if (gap > 0f && velExcess > 0f && ctx.Dt > 0f)
            force -= _ground.Normal * velExcess / ctx.Dt;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            float walkAccel    = cfg.WalkAccel    * m.WalkAccel;
            float maxWalkSpeed = cfg.MaxWalkSpeed * m.MaxWalkSpeed;
            force.X += inputX * walkAccel;
            // Over-cap brake skipped during hitstun so horizontal knockback on a
            // grounded victim isn't clipped back to walk speed in one frame — see
            // MovementModifiers.PreserveExternalVelocity.
            float excess = MathF.Abs(ctx.Body.Velocity.X) - maxWalkSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f
                && !m.PreserveExternalVelocity)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
            // Excess correction can zero out the walk force when the body is already
            // moving faster than MaxWalkSpeed in the input direction (e.g. just exited
            // a vault). Without a residual tangential force here, the physics solver's
            // friction would brake the body back down to MaxWalkSpeed. Keep a tiny
            // walk-intent signal so friction recognizes the state is still driving.
            if (MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && MathF.Abs(force.X) < 2f)
                force.X = inputX * 2f;
        }
        // No-input braking: handled by SurfaceContact.Friction in the physics solver
        // now — applying tangential force here would just suppress that friction.

        ctx.Body.AppliedForce = force;
    }
}

public class CrouchedState : MovementState
{
    public override AnimTag AnimationTag => AnimTag.Crouch;
    public override int ActivePriority => MovementPriorities.CrouchedActive;
    public override int PassivePriority => MovementPriorities.CrouchedPassive;

    private FloatingSurfaceDistance _ground;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // return (ctx.Input.Down || ctx.TryGetCeiling(out _)) && ctx.TryGetCrouchGround(out _);
        return ctx.Input.Down && ctx.TryGetCrouchGround(out _);

    }   

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        return (ctx.Input.Down || ctx.TryGetCeiling(out _)) && ctx.TryGetCrouchGround(out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => EnsureGround(ctx);

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        if (_ground != null)
            ctx.Body.Constraints.Remove(_ground);
        _ground = null;
    }

    // Idempotent crouch-ground acquisition — see StandingState.EnsureGround.
    private void EnsureGround(EnvironmentContext ctx)
    {
        if (_ground != null) return;
        if (ctx.TryGetCrouchGround(out var contact))
        {
            _ground = contact;
            ctx.Body.Constraints.Add(_ground);
        }
    }

    public override void ResetTransient() => _ground = null;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        EnsureGround(ctx);
        if (ctx.TryGetCrouchGround(out var refreshed))
        {
            _ground.Position        = refreshed.Position;
            _ground.Normal          = refreshed.Normal;
            _ground.MinDistance     = refreshed.MinDistance;
            _ground.SurfaceVelocity = refreshed.SurfaceVelocity;
        }
        // Same per-frame friction refresh as Standing — see comment there.
        _ground.Friction = MovementConfig.Current.GroundFriction * ctx.Modifiers.GroundFriction;

        var force = Vector2.Zero;

        float dist           = Vector2.Dot(ctx.Body.Position - _ground.Position, _ground.Normal);
        float gap            = _ground.MinDistance - dist;
        float velAlongNormal = Vector2.Dot(ctx.Body.Velocity - _ground.SurfaceVelocity, _ground.Normal);
        if (gap > 0f)
            force += _ground.Normal * (gap * MovementConfig.Current.SpringK - velAlongNormal * MovementConfig.Current.SpringDamping);
        // Anti-pop clamp gated on gap > 0 — see StandingState.Update.
        float velExcess = velAlongNormal - MovementConfig.Current.SpringMaxRiseSpeed;
        if (gap > 0f && velExcess > 0f && ctx.Dt > 0f)
            force -= _ground.Normal * velExcess / ctx.Dt;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.CrouchWalkAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.CrouchMaxWalkSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f
                && !ctx.Modifiers.PreserveExternalVelocity)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
            // Walk-intent signal so the solver's friction doesn't brake an
            // overspeed-coasting body — see StandingState.Update.
            if (MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && MathF.Abs(force.X) < 2f)
                force.X = inputX * 2f;
        }
        // No-input braking handled by SurfaceContact.Friction in the physics solver.
        
        ctx.Body.AppliedForce = force;
    }
}
