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
    // Null = doesn't damage tiles on impact (the default — player, balloons).
    // Set by EntityFactory for "crasher" entities like Ball. PhysicsWorld checks
    // for null at each impulse site and dispatches damage when present.
    public ImpactDamage Impact;

    // Per-body multiplier on the global ground friction. 1.0 (default) matches
    // the player's tuned feel; enemies set this lower so a slash impulse actually
    // visibly chucks them across the ground instead of being eaten by friction
    // in 1-2 frames. Applied wherever PhysicsWorld assigns or computes friction.
    public float FrictionScale = 1f;

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
