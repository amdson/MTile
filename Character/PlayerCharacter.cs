using Microsoft.Xna.Framework;

namespace MTile;

public class PlayerCharacter
{
    public const float Radius = 12f;

    public readonly PhysicsBody Body;
    private MovementState _state = new FallingState();

    public PlayerCharacter(Vector2 startPosition)
    {
        Body = new PhysicsBody(Polygon.CreateRegular(Radius, 6), startPosition);
    }

    public void Update(PlayerInput input, ChunkMap chunks, float dt)
    {
        _state = _state.Update(Body, input, chunks, dt);
    }

    public bool IsGrounded => _state is StandingState;
}
