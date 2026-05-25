using Microsoft.Xna.Framework;

namespace MTile;

// A read-only snapshot of everything the animation layer is allowed to look at,
// gathered once per render frame. This is the *one-way* boundary between the sim
// and animation: the animator reads this; movement/action code never produces it
// and is completely unaware it exists. Add fields here as animations need more
// signal (action vars, combat flags, surface normals, …) — always read-only.
public readonly struct CharacterAnimSample
{
    public readonly Vector2 Position;       // body world position
    public readonly Vector2 Velocity;       // body velocity (px/s)
    public readonly int     Facing;         // -1 / +1
    public readonly bool    Grounded;
    public readonly string  MovementState;  // PlayerCharacter.CurrentStateName
    public readonly string  Action;         // PlayerCharacter.CurrentActionName
    public readonly float   Dt;             // render delta time (NOT the sim fixed dt)

    public CharacterAnimSample(
        Vector2 position, Vector2 velocity, int facing, bool grounded,
        string movementState, string action, float dt)
    {
        Position = position; Velocity = velocity; Facing = facing; Grounded = grounded;
        MovementState = movementState; Action = action; Dt = dt;
    }

    // Pull the sample from a live character through its public surface only. The
    // direction of the dependency is animation -> character, never the reverse.
    public static CharacterAnimSample From(PlayerCharacter p, float dt)
        => new(p.Body.Position, p.Body.Velocity, p.Facing, p.IsGrounded,
               p.CurrentStateName, p.CurrentActionName, dt);
}
