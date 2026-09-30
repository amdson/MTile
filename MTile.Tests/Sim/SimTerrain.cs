using Microsoft.Xna.Framework;

namespace MTile.Tests.Sim;

// Builds a ChunkMap from a multi-line ASCII string ('X' = solid, anything else = empty,
// one character per 16 px tile, common leading whitespace stripped). The implementation
// moved into the library as MTile.AsciiTerrain so non-test tools can use it; this is the
// name the test suite has always used.
public static class SimTerrain
{
    public static ChunkMap FromAscii(string ascii, int originTileX = 0, int originTileY = 0)
        => AsciiTerrain.FromAscii(ascii, originTileX, originTileY);

    // Returns world-space pixel position of the top-left of tile (originTileX+col, originTileY+row).
    public static Vector2 TileWorldPos(int tileX, int tileY) => AsciiTerrain.TileWorldPos(tileX, tileY);
}
