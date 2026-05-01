using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class JumpingState : MovementState
{
    public override int ActivePriority => 50;
    public override int PassivePriority => 30;

    private bool _jumpReleased;
    private float _timeInState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return abilities.JumpJustPressed && ctx.TryGetGround(out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.MaxJumpHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !(ctx.Input.Space || ctx.Input.Up);
        ctx.Body.Velocity.Y = MovementConfig.Current.JumpVelocity;
        
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!(ctx.Input.Space || ctx.Input.Up)) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.JumpHoldForce;
        if (_timeInState <= ctx.Dt) 
            force.Y += MovementConfig.Current.JumpInitForce;
        
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.AirDrag, MovementConfig.Current.AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}

public class RunningJumpState : MovementState
{
    public override int ActivePriority => 55;
    public override int PassivePriority => 35;

    private bool _jumpReleased;
    private float _timeInState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return abilities.JumpJustPressed && ctx.TryGetGround(out _) && Math.Abs(ctx.Body.Velocity.X) >= MovementConfig.Current.RunJumpMinSpeed;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.MaxJumpHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !(ctx.Input.Space || ctx.Input.Up);
        ctx.Body.Velocity.Y = MovementConfig.Current.RunJumpVelocity;
        
        ctx.Body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!(ctx.Input.Space || ctx.Input.Up)) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.RunJumpHoldForce;
        if (_timeInState <= ctx.Dt) 
            force.Y += MovementConfig.Current.JumpInitForce;
        
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.AirDrag, MovementConfig.Current.AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}

public class WallSlidingState : MovementState
{
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

        float terminalSpeed = ctx.Input.Down 
            ? MovementConfig.Current.FastSlideTerminalSpeed 
            : MovementConfig.Current.SlideTerminalSpeed;

        float vy = ctx.Body.Velocity.Y;
        ctx.Body.AppliedForce = vy > 0f
            ? new Vector2(0f, -(vy / terminalSpeed) * MovementConfig.Current.SlideDrag)
            : Vector2.Zero;
    }
}

public class WallJumpingState : MovementState
{
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
        return abilities.JumpJustPressed && ctx.TryGetWall(_wallDir, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.WallJumpMaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !(ctx.Input.Space || ctx.Input.Up);
        
        int dirAwayFromWall = _wallDir == 1 ? -1 : 1;
        ctx.Body.Velocity = new Vector2(dirAwayFromWall * MovementConfig.Current.WallJumpInitialVelX, MovementConfig.Current.WallJumpInitialVelY);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        bool jumpHeld = ctx.Input.Space || ctx.Input.Up;
        if (!jumpHeld) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.WallJumpHoldForce;

        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.WallJumpAirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.WallJumpMaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.WallJumpAirDrag, MovementConfig.Current.WallJumpAirDrag);
        }

        if (ctx.Input.Down)
        {
            force.Y += MovementConfig.Current.FastFallForce;
        }

        ctx.Body.AppliedForce = force;
    }
}

public class DoubleJumpingState : MovementState
{
    public override int ActivePriority => 60;
    public override int PassivePriority => 40;

    private bool _jumpReleased;
    private float _timeInState;

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return abilities.JumpJustPressed && !abilities.HasDoubleJumped && !ctx.TryGetGround(out _) && !ctx.TryGetWall(1, out _) && !ctx.TryGetWall(-1, out _);
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        return !_jumpReleased && _timeInState < MovementConfig.Current.DoubleJumpMaxHoldTime;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState = 0f;
        _jumpReleased = !(ctx.Input.Space || ctx.Input.Up);
        abilities.HasDoubleJumped = true;
        
        // Kill existing vertical momentum entirely
        ctx.Body.Velocity.Y = MovementConfig.Current.DoubleJumpVelocity;
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState abilities)
    {
        _timeInState += ctx.Dt;
        if (!(ctx.Input.Space || ctx.Input.Up)) _jumpReleased = true;

        var force = Vector2.Zero;
        force.Y += MovementConfig.Current.DoubleJumpHoldForce;
        if (_timeInState <= ctx.Dt) 
            force.Y += MovementConfig.Current.DoubleJumpInitForce;
        
        float inputX = (ctx.Input.Right ? 1f : 0f) - (ctx.Input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * MovementConfig.Current.AirAccel;
            float excess = MathF.Abs(ctx.Body.Velocity.X) - MovementConfig.Current.MaxAirSpeed;
            if (excess > 0f && MathF.Sign(ctx.Body.Velocity.X) == MathF.Sign(inputX) && ctx.Dt > 0f)
                force.X -= MathF.Sign(ctx.Body.Velocity.X) * excess / ctx.Dt;
        }
        else if (ctx.Dt > 0f)
        {
            force.X = Math.Clamp(-ctx.Body.Velocity.X / ctx.Dt, -MovementConfig.Current.AirDrag, MovementConfig.Current.AirDrag);
        }

        ctx.Body.AppliedForce = force;
    }
}
