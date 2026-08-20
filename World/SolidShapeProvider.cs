using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Where a SolidShapeRef came from. Carried so collision code can act on the
// *cell behind* a shape rather than just its geometry — specifically so an
// unresolvable overlap can destroy the sprout responsible for it (see
// PhysicsWorld.ResolveChunkCollisions). Without this, a shape is anonymous
// geometry and the solver cannot tell a permanent wall from a block that is
// still growing into the body.
public enum SolidShapeSource : byte
{
    Tile,       // a committed TileState.Solid cell; Gtx/Gty are its coords
    Sprout,     // a growing sprout's face volume; Gtx/Gty are the sprout's cell
    External,   // a registered ISolidShapeProvider (moving platform); no cell
}

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
    // Provenance. Gtx/Gty are meaningful only when Source != External.
    public readonly SolidShapeSource Source;
    public readonly int Gtx;
    public readonly int Gty;

    public SolidShapeRef(float left, float top, float right, float bottom,
                         Vector2 position, Vector2 velocity, Polygon polygon,
                         SolidShapeSource source = SolidShapeSource.External,
                         int gtx = 0, int gty = 0)
    {
        WorldLeft = left; WorldTop = top; WorldRight = right; WorldBottom = bottom;
        Position = position; Velocity = velocity; Polygon = polygon;
        Source = source; Gtx = gtx; Gty = gty;
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
