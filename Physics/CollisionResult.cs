using Microsoft.Xna.Framework;

namespace MTile;

public readonly struct CollisionResult
{
    public static readonly CollisionResult None = new(false, Vector2.Zero, 0f);

    public bool Intersects { get; }
    // Push A by MTV to fully separate it from B.
    public Vector2 MTV { get; }
    public float Depth { get; }

    public CollisionResult(bool intersects, Vector2 mtv, float depth)
    {
        Intersects = intersects;
        MTV = mtv;
        Depth = depth;
    }
}
