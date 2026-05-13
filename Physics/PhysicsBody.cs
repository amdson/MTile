using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public class PhysicsBody
{
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector2 AppliedForce;
    public Polygon Polygon;
    public List<PhysicsContact> Constraints = new();

    // Float-precision AABB of the polygon at the body's current position. Recomputes on access (cheap
    // for a 6-vertex hex). Doubles as a probe-region builder — `body.Bounds.StripAbove(20)` gives the
    // 20px slab right above the body for ceiling-probing, etc. See BoundingBox.
    public BoundingBox Bounds => Polygon.GetBoundingBox(Position);

    public PhysicsBody(Polygon polygon, Vector2 position)
    {
        Polygon = polygon;
        Position = position;
    }
}
