using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class JumpingState : MovementState
{
    private const float JumpVelocity = -160f; // Initial impulse velocity upward
    private const float AirAccel = 1500f;
    private const float MaxAirSpeed = 150f;
    private const float AirDrag = 500f;
    private const float JumpHoldForce = -2500f; // Extra upward force while holding
    private const float JumpInitForce = -0; // Additional force applied on the first frame of the jump
    private const float MaxJumpHoldTime = 0.12f;

    private bool _jumpReleased;
    private float _timeInState;

    public JumpingState(PhysicsBody body)
    {
        body.Velocity.Y = JumpVelocity;
        // Break any existing ground constraints since we jumped
        body.Constraints.RemoveAll(c => c is FloatingSurfaceDistance);
    }

    public override MovementState Update(PhysicsBody body, PlayerInput input, ChunkMap chunks, float dt)
    {
        _timeInState += dt;
        bool jumpHeld = input.Space || input.Up;
        
        if (!jumpHeld) _jumpReleased = true;

        if (_jumpReleased || _timeInState >= MaxJumpHoldTime)
            return new FallingState();

        var force = Vector2.Zero;
        force.Y += JumpHoldForce; // Upward force to counter gravity
        if (_timeInState <= dt) 
            force.Y += JumpInitForce; // Apply initial jump force only on the first frame
        
        float inputX = (input.Right ? 1f : 0f) - (input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * AirAccel;
            float excess = MathF.Abs(body.Velocity.X) - MaxAirSpeed;
            if (excess > 0f && MathF.Sign(body.Velocity.X) == MathF.Sign(inputX) && dt > 0f)
            {
                force.X -= MathF.Sign(body.Velocity.X) * excess / dt;
            }
        }
        else if (dt > 0f)
        {
            force.X = Math.Clamp(-body.Velocity.X / dt, -AirDrag, AirDrag);
        }

        body.AppliedForce = force;
        return this;
    }
}

public class WallSlidingState : MovementState
{
    private const float BodyHalfWidth      = PlayerCharacter.Radius;
    private const float SlideTerminalSpeed = 40f;  // downward speed at which drag equals gravity
    private const float SlideDrag          = 300f; // drag force magnitude at terminal speed

    private readonly int _wallDir;
    private readonly FloatingSurfaceDistance _wall;

    public WallSlidingState(int wallDir, FloatingSurfaceDistance wall)
    {
        _wallDir = wallDir;
        _wall    = wall;
    }

    public override MovementState Update(PhysicsBody body, PlayerInput input, ChunkMap chunks, float dt)
    {
        if (input.Space || input.Up)
        {
            body.Constraints.Remove(_wall);
            return new WallJumpingState(body, _wallDir == 1 ? -1 : 1);
        }

        if (GroundChecker.TryFind(body, chunks, PlayerCharacter.Radius, PlayerCharacter.Radius, out var ground))
        {
            body.Constraints.Remove(_wall);
            body.Constraints.Add(ground);
            return new StandingState(ground);
        }

        bool pressingIntoWall = (_wallDir == 1 && input.Right) || (_wallDir == -1 && input.Left);
        if (!WallChecker.TryFind(body, chunks, BodyHalfWidth, 0f, _wallDir, out var refreshed) || !pressingIntoWall)
        {
            body.Constraints.Remove(_wall);
            return new FallingState();
        }

        _wall.Position    = refreshed.Position;
        _wall.Normal      = refreshed.Normal;
        _wall.MinDistance = refreshed.MinDistance;

        // Dynamic friction: drag proportional to downward speed; balances gravity at SlideTerminalSpeed.
        float vy = body.Velocity.Y;
        body.AppliedForce = vy > 0f
            ? new Vector2(0f, -(vy / SlideTerminalSpeed) * SlideDrag)
            : Vector2.Zero;

        return this;
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

    private float _timeInState;
    private bool _jumpReleased;

    public WallJumpingState(PhysicsBody body, int dirAwayFromWall)
    {
        body.Velocity = new Vector2(dirAwayFromWall * InitialVelX, InitialVelY);
    }

    public override MovementState Update(PhysicsBody body, PlayerInput input, ChunkMap chunks, float dt)
    {
        _timeInState += dt;
        bool jumpHeld = input.Space || input.Up;
        if (!jumpHeld) _jumpReleased = true;

        if (_jumpReleased || _timeInState >= MaxHoldTime)
            return new FallingState();

        var force = Vector2.Zero;
        force.Y += JumpHoldForce;

        float inputX = (input.Right ? 1f : 0f) - (input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * AirAccel;
            float excess = MathF.Abs(body.Velocity.X) - MaxAirSpeed;
            if (excess > 0f && MathF.Sign(body.Velocity.X) == MathF.Sign(inputX) && dt > 0f)
            {
                force.X -= MathF.Sign(body.Velocity.X) * excess / dt;
            }
        }
        else if (dt > 0f)
        {
            force.X = Math.Clamp(-body.Velocity.X / dt, -AirDrag, AirDrag);
        }

        body.AppliedForce = force;
        return this;
    }
}
