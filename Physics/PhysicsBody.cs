using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public class PhysicsBody
{
    public Vector2 Position;
    public Vector2 Velocity;
    public Polygon Polygon;
    public List<PhysicsContact> Constraints = new();

    public PhysicsBody(Polygon polygon, Vector2 position)
    {
        Polygon = polygon;
        Position = position;
    }
}
