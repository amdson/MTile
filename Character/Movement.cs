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

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
        => ctx.Combat?.StunActive == true;

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
        => ctx.Combat?.StunActive == true;

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
        return ctx.TryGetGround(out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities, ref MovementVars vars)
    {
        return ctx.TryGetGround(out _);
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
        float velExcess = velAlongNormal - cfg.SpringMaxRiseSpeed;
        if (velExcess > 0f && ctx.Dt > 0f)
            force -= _ground.Normal * velExcess / ctx.Dt;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            float walkAccel    = cfg.WalkAccel    * m.WalkAccel;
            float maxWalkSpeed = cfg.MaxWalkSpeed * m.MaxWalkSpeed;
            force.X += inputX * walkAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - maxWalkSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
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
        float velExcess = velAlongNormal - MovementConfig.Current.SpringMaxRiseSpeed;
        if (velExcess > 0f && ctx.Dt > 0f)
            force -= _ground.Normal * velExcess / ctx.Dt;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.CrouchWalkAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.CrouchMaxWalkSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
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
