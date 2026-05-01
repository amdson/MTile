using Microsoft.Xna.Framework;

namespace MTile;

public abstract class PhysicsContact { }

// To check if a polygon surface distance constraint is supported by the current tilemap / collision system,
// we can sweep the polygon 2 epsilon distance opposite the surface normal and see if it hits anything within the min distance. 
// Which produces the same surface normal. 
// TODO remove minDistance and simply assume it is zero. Rename to SurfaceContact
public class SurfaceDistance(Vector2 position, Vector2 normal, float minDistance) : PhysicsContact
{
    public Vector2 Position = position;
    public Vector2 Normal = normal;
    public float MinDistance = minDistance;
}

// An alternative to SurfaceDistance that must be bundled with a separate maintainance test
// This is used for floating constraints, where we want to maintain a certain distance from the surface, but not necessarily be in contact with it.
// For instance, while walking the actual player body should be floating above the ground,
// with the player maintaining a distance contact. This allows the player to step up small ledges without losing contact, 
// and also allows the player to walk over small holes without falling in. 
public class FloatingSurfaceDistance(Vector2 position, Vector2 normal, float minDistance) : PhysicsContact
{
    public Vector2 Position = position;
    public Vector2 Normal = normal;
    public float MinDistance = minDistance;
}

public class PointForceContact(Vector2 position) : PhysicsContact
{
    public Vector2 Position = position;
}
