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
    private const float AirAccel = 1500f;
    private const float MaxAirSpeed = 150f;
    private const float AirDrag = 500f;

    public override int ActivePriority => 0;
    public override int PassivePriority => 0;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities) => true;
    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities) => true;

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        var force = Vector2.Zero;
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        
        if (inputX != 0f)
        {
            force.X += inputX * AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
            {
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
            }
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -AirDrag, AirDrag);
        }
        
        ctx.Body.AppliedForce = force;
    }
}

public class StandingState : MovementState
{
    private const float SpringK        = 300f;
    private const float WalkAccel      = 3000f;
    private const float MaxWalkSpeed   = 100f;
    private const float BrakingForce   = 3000f;

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
            _ground.Position  = refreshed.Position;
            _ground.Normal    = refreshed.Normal;
            _ground.MinDistance = refreshed.MinDistance;
        }

        var force = Vector2.Zero;

        float dist = Vector2.Dot(ctx.Body.Position - _ground.Position, _ground.Normal);
        float gap  = _ground.MinDistance - dist;
        if (gap > 0f)
            force += _ground.Normal * gap * SpringK;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * WalkAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MaxWalkSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -BrakingForce, BrakingForce);
        }
        
        ctx.Body.AppliedForce = force;
    }
}
