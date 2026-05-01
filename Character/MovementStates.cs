using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class JumpingState : MovementState
{
    private const float JumpVelocity = -600f; // Initial impulse velocity upward
    private const float AirAccel = 1500f;
    private const float MaxAirSpeed = 150f;
    private const float AirDrag = 500f;
    private const float JumpHoldForce = -300f; // Extra upward force while holding
    private const float MaxJumpHoldTime = 0.25f;

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
        
        float inputX = (input.Right ? 1f : 0f) - (input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * AirAccel;
            float excess = MathF.Abs(body.Velocity.X) - MaxAirSpeed;
            if (excess > 0f && MathF.Sign(body.Velocity.X) == MathF.Sign(inputX))
            {
                force.X -= MathF.Sign(body.Velocity.X) * excess / dt;
            }
        }
        else
        {
            force.X = Math.Clamp(-body.Velocity.X / dt, -AirDrag, AirDrag);
        }

        body.AppliedForce = force;
        return this;
    }
}

public class WallSlidingState : MovementState
{
    private readonly int _wallDir;
    private const float SlideGravityReduction = 200f; // Apply upward force to reduce effective gravity

    public WallSlidingState(int wallDir)
    {
        _wallDir = wallDir;
    }

    public override MovementState Update(PhysicsBody body, PlayerInput input, ChunkMap chunks, float dt)
    {
        if (input.Space || input.Up)
            return new WallJumpingState(body, _wallDir == 1 ? -1 : 1);

        int currentWallDir = WallChecker.Check(body, chunks, PlayerCharacter.Radius);
        
        if (currentWallDir == 0 || (currentWallDir == 1 && !input.Right) || (currentWallDir == -1 && !input.Left))
            return new FallingState();

        if (GroundChecker.TryFind(body, chunks, PlayerCharacter.Radius, PlayerCharacter.Radius, out var contact))
        {
            body.Constraints.Add(contact);
            return new StandingState(contact);
        }

        // Apply upward force to counter gravity partially
        body.AppliedForce = new Vector2(0, -SlideGravityReduction);
        
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
            if (excess > 0f && MathF.Sign(body.Velocity.X) == MathF.Sign(inputX))
            {
                force.X -= MathF.Sign(body.Velocity.X) * excess / dt;
            }
        }
        else
        {
            force.X = Math.Clamp(-body.Velocity.X / dt, -AirDrag, AirDrag);
        }

        body.AppliedForce = force;
        return this;
    }
}
