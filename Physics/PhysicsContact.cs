using Microsoft.Xna.Framework;

namespace MTile;

public abstract class PhysicsContact { }

public abstract class SurfaceContact(Vector2 position, Vector2 normal, float minDistance) : PhysicsContact
{
    public Vector2 Position = position;
    public Vector2 Normal = normal;
    public float MinDistance = minDistance;
}

// Hard contact created automatically by collision resolution. Prevents body from penetrating a surface.
public class SurfaceDistance(Vector2 position, Vector2 normal, float minDistance)
    : SurfaceContact(position, normal, minDistance) { }

// Soft contact managed by character movement logic. Prevents body from moving into the surface,
// but the upward push to maintain standing height is applied by the movement state, not the physics step.
public class FloatingSurfaceDistance(Vector2 position, Vector2 normal, float minDistance)
    : SurfaceContact(position, normal, minDistance) { }

public class PointForceContact(Vector2 position) : PhysicsContact
{
    public Vector2 Position = position;
}
