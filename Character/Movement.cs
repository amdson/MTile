using System;
using Microsoft.Xna.Framework;

namespace MTile;

public abstract class MovementState
{
    public abstract MovementState Update(PhysicsBody body, PlayerInput input, ChunkMap chunks, float dt);
}

public class FallingState : MovementState
{
    private const float BodyHalfHeight = PlayerCharacter.Radius;
    private const float FloatHeight = PlayerCharacter.Radius;

    private const float AirAccel = 1500f;
    private const float MaxAirSpeed = 150f;
    private const float AirDrag = 500f;

    public override MovementState Update(PhysicsBody body, PlayerInput input, ChunkMap chunks, float dt)
    {
        if (GroundChecker.TryFind(body, chunks, BodyHalfHeight, FloatHeight, out var contact))
        {
            body.Constraints.Add(contact);
            return new StandingState(contact);
        }

        float inputX = (input.Right ? 1f : 0f) - (input.Left ? 1f : 0f);

        if (inputX != 0f)
        {
            int wallDir = (int)MathF.Sign(inputX);
            if (WallChecker.TryFind(body, chunks, PlayerCharacter.Radius, 0f, wallDir, out var wallContact))
            {
                body.Constraints.Add(wallContact);
                return new WallSlidingState(wallDir, wallContact);
            }
        }

        var force = Vector2.Zero;
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

public class StandingState : MovementState
{
    private const float BodyHalfHeight = PlayerCharacter.Radius;
    private const float FloatHeight    = PlayerCharacter.Radius;
    private const float SpringK        = 300f;   // upward spring stiffness (stable when < 4/dt²)
    private const float WalkAccel      = 3000f;
    private const float MaxWalkSpeed   = 100f;
    private const float BrakingForce   = 3000f;

    private readonly FloatingSurfaceDistance _ground;

    public StandingState(FloatingSurfaceDistance ground) => _ground = ground;

    public override MovementState Update(PhysicsBody body, PlayerInput input, ChunkMap chunks, float dt)
    {
        if (input.Space || input.Up)
        {
            body.Constraints.Remove(_ground);
            return new JumpingState(body);
        }

        if (!GroundChecker.TryFind(body, chunks, BodyHalfHeight, FloatHeight, out var refreshed))
        {
            body.Constraints.Remove(_ground);
            return new FallingState();
        }

        _ground.Position  = refreshed.Position;
        _ground.Normal    = refreshed.Normal;
        _ground.MinDistance = refreshed.MinDistance;

        var force = Vector2.Zero;

        // Spring: push upward when body dips below standing height.
        float dist = Vector2.Dot(body.Position - _ground.Position, _ground.Normal);
        float gap  = _ground.MinDistance - dist;
        if (gap > 0f)
            force += _ground.Normal * gap * SpringK;

        // Horizontal: accelerate toward input or brake to a stop.
        float inputX = (input.Right ? 1f : 0f) - (input.Left ? 1f : 0f);
        if (inputX != 0f)
        {
            force.X += inputX * WalkAccel;
            // Cancel any velocity exceeding MaxWalkSpeed in the walk direction this frame.
            float excess = MathF.Abs(body.Velocity.X) - MaxWalkSpeed;
            if (excess > 0f && MathF.Sign(body.Velocity.X) == MathF.Sign(inputX) && dt > 0f)
                force.X -= MathF.Sign(body.Velocity.X) * excess / dt;
        }
        else if (dt > 0f)
        {
            // Cancel horizontal velocity up to BrakingForce this frame.
            force.X = Math.Clamp(-body.Velocity.X / dt, -BrakingForce, BrakingForce);
        }
        
        body.AppliedForce = force;
        return this;
    }
}
