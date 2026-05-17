using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Lightweight view of a solid shape from a provider: the AABB plus the data the
// sweep / spatial-query code needs to act on it without reaching back to the
// provider. Tiles materialize one of these on demand from a TileRef; future
// dynamic surfaces (moving platforms, growing blocks) carry per-instance
// polygons and nonzero Velocity.
public readonly struct SolidShapeRef
{
    public readonly float   WorldLeft;
    public readonly float   WorldTop;
    public readonly float   WorldRight;
    public readonly float   WorldBottom;
    public readonly Vector2 Position;
    public readonly Vector2 Velocity;
    public readonly Polygon Polygon;

    public SolidShapeRef(float left, float top, float right, float bottom,
                         Vector2 position, Vector2 velocity, Polygon polygon)
    {
        WorldLeft = left; WorldTop = top; WorldRight = right; WorldBottom = bottom;
        Position = position; Velocity = velocity; Polygon = polygon;
    }

    public float WorldCenterX => (WorldLeft + WorldRight) * 0.5f;
    public float WorldCenterY => (WorldTop  + WorldBottom) * 0.5f;
}

// A source of solid shapes in the world. ChunkMap is the first provider;
// future shape-generating entities (moving platforms, growing blocks)
// implement this and register via ChunkMap.Providers. World-level queries
// (WorldQuery) fan out across all registered providers.
public interface ISolidShapeProvider
{
    IEnumerable<SolidShapeRef> ShapesInRect(BoundingBox region);
    bool IsSolidAt(float worldX, float worldY);
}
