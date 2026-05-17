using System.Collections.Generic;

namespace MTile;

// World-level façade over the registered ISolidShapeProvider's. Most callers
// today still go through TileQuery's tile-only fast paths; new code that needs
// to see dynamic surfaces (moving platforms, growing blocks) routes here.
//
// The ChunkMap argument doubles as the world root — it is both the first
// provider (its tiles) and the holder of additional providers (chunks.Providers).
public static class WorldQuery
{
    public static IEnumerable<SolidShapeRef> SolidShapesInRect(ChunkMap chunks, BoundingBox region)
    {
        foreach (var s in ((ISolidShapeProvider)chunks).ShapesInRect(region)) yield return s;
        foreach (var provider in chunks.Providers)
            foreach (var s in provider.ShapesInRect(region)) yield return s;
    }

    public static bool IsSolidAt(ChunkMap chunks, float worldX, float worldY)
    {
        if (((ISolidShapeProvider)chunks).IsSolidAt(worldX, worldY)) return true;
        foreach (var provider in chunks.Providers)
            if (provider.IsSolidAt(worldX, worldY)) return true;
        return false;
    }
}
