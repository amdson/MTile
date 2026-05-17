using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Axis-aligned rectangle that can be repositioned over time. Position+velocity
// are kept in lock-step by SetPosition (velocity = (newPos - oldPos)/dt) so the
// SolidShapeRef it yields carries the right value for relative-frame sweep math.
//
// Caller owns the motion model — Game1 ticks SetPosition each frame with the
// sinusoidal target. This class is just a shape provider with a moveable Position.
public class MovingRectangle : ISolidShapeProvider
{
    public Vector2 Position;
    public Vector2 Velocity;
    public readonly float HalfWidth;
    public readonly float HalfHeight;
    public readonly Polygon Polygon;

    public MovingRectangle(Vector2 position, float width, float height)
    {
        Position   = position;
        HalfWidth  = width  * 0.5f;
        HalfHeight = height * 0.5f;
        Polygon    = Polygon.CreateRectangle(width, height);
    }

    public void SetPosition(Vector2 newPosition, float dt)
    {
        Velocity = dt > 0f ? (newPosition - Position) / dt : Vector2.Zero;
        Position = newPosition;
    }

    public float Left   => Position.X - HalfWidth;
    public float Top    => Position.Y - HalfHeight;
    public float Right  => Position.X + HalfWidth;
    public float Bottom => Position.Y + HalfHeight;

    IEnumerable<SolidShapeRef> ISolidShapeProvider.ShapesInRect(BoundingBox region)
    {
        if (Right  <= region.Left || Left >= region.Right ||
            Bottom <= region.Top  || Top  >= region.Bottom) yield break;
        yield return new SolidShapeRef(Left, Top, Right, Bottom, Position, Velocity, Polygon);
    }

    bool ISolidShapeProvider.IsSolidAt(float worldX, float worldY)
        => worldX >= Left && worldX <= Right && worldY >= Top && worldY <= Bottom;
}
