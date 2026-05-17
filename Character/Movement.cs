using System;
using Microsoft.Xna.Framework;

namespace MTile;

public abstract class MovementState
{
    public abstract int ActivePriority { get; }
    public abstract int PassivePriority { get; }

    public abstract bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities);
    public abstract bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities);

    public virtual void Enter(EnvironmentContext ctx, PlayerAbilityState abilities) {}
    public virtual void Exit(EnvironmentContext ctx, PlayerAbilityState abilities) {}

    public abstract void Update(EnvironmentContext ctx, PlayerAbilityState abilities);
}

public class FallingState : MovementState
{
    public override int ActivePriority => 0;
    public override int PassivePriority => 0;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities) => true;
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities) => true;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
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
    public override int ActivePriority => 10;
    public override int PassivePriority => 10;

    private FloatingSurfaceDistance _ground;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return ctx.TryGetGround(out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return ctx.TryGetGround(out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.TryGetGround(out var contact))
        {
            _ground = contact;
            ctx.Body.Constraints.Add(_ground);
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_ground != null)
            ctx.Body.Constraints.Remove(_ground);
        _ground = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
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
    public override int ActivePriority => 15;
    public override int PassivePriority => 15;

    private FloatingSurfaceDistance _ground;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        // return (ctx.Input.Down || ctx.TryGetCeiling(out _)) && ctx.TryGetCrouchGround(out _);
        return ctx.Input.Down && ctx.TryGetCrouchGround(out _);

    }   

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return (ctx.Input.Down || ctx.TryGetCeiling(out _)) && ctx.TryGetCrouchGround(out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.TryGetCrouchGround(out var contact))
        {
            _ground = contact;
            ctx.Body.Constraints.Add(_ground);
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_ground != null)
            ctx.Body.Constraints.Remove(_ground);
        _ground = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
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
