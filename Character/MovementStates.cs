using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class JumpingState : MovementState
{
    private const float JumpVelocity = -160f;
    private const float AirAccel = 1500f;
    private const float MaxAirSpeed = 150f;
    private const float AirDrag = 500f;
    private const float JumpHoldForce = -2500f;
    private const float JumpInitForce = -0;
    private const float MaxJumpHoldTime = 0.12f;

    public override int ActivePriority => 50;
    public override int PassivePriority => 30;

    private bool _jumpReleased;
    private float _timeInState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return (ctx.Input.Space || ctx.Input.Up) && ctx.TryGetGround(out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MaxJumpHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !(ctx.Input.Space || ctx.Input.Up);
        ctx.Body.Velocity.Y = JumpVelocity;
        
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!(ctx.Input.Space || ctx.Input.Up)) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += JumpHoldForce;
        if (_timeInState <= ctx.Dt) 
            force.Y += JumpInitForce;
        
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -AirDrag, AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}

public class WallSlidingState : MovementState
{
    private const float SlideTerminalSpeed = 40f;
    private const float SlideDrag = 300f;

    private readonly int _wallDir;
    private FloatingSurfaceDistance _wall;

    public WallSlidingState(int wallDir)
    {
        _wallDir = wallDir;
    }

    public override int ActivePriority => 20;
    public override int PassivePriority => 20;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool pressingIntoWall = (_wallDir == 1 && ctx.Input.Right) || (_wallDir == -1 && ctx.Input.Left);
        return pressingIntoWall && ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        bool pressingIntoWall = (_wallDir == 1 && ctx.Input.Right) || (_wallDir == -1 && ctx.Input.Left);
        return pressingIntoWall && ctx.TryGetWall(_wallDir, out _);
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.TryGetWall(_wallDir, out var contact))
        {
            _wall = contact;
            ctx.Body.Constraints.Add(_wall);
        }
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (_wall != null)
            ctx.Body.Constraints.Remove(_wall);
        _wall = null;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        if (ctx.TryGetWall(_wallDir, out var refreshed))
        {
            _wall.Position = refreshed.Position;
            _wall.Normal = refreshed.Normal;
            _wall.MinDistance = refreshed.MinDistance;
        }

        float vy = ctx.Body.Velocity.Y;
        ctx.Body.AppliedForce = vy > 0f
            ? new Vector2(0f, -(vy / SlideTerminalSpeed) * SlideDrag)
            : Vector2.Zero;
    }
}

public class WallJumpingState : MovementState
{
    private const float InitialVelX = 400f;
    private const float InitialVelY = -600f;
    
    private const float AirAccel = 1500f;
    private const float MaxAirSpeed = 150f;
    private const float AirDrag = 500f;
    private const float JumpHoldForce = -300f;
    private const float MaxHoldTime = 0.25f;

    private readonly int _wallDir;
    private float _timeInState;
    private bool _jumpReleased;

    public WallJumpingState(int wallDir)
    {
        _wallDir = wallDir;
    }

    public override int ActivePriority => 50;
    public override int PassivePriority => 40;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return (ctx.Input.Space || ctx.Input.Up) && ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !(ctx.Input.Space || ctx.Input.Up);
        
        int dirAwayFromWall = _wallDir == 1 ? -1 : 1;
        ctx.Body.Velocity = new Vector2(dirAwayFromWall * InitialVelX, InitialVelY);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        bool jumpHeld = ctx.Input.Space || ctx.Input.Up;
        if (!jumpHeld) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += JumpHoldForce;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -AirDrag, AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}
