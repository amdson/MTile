using Microsoft.Xna.Framework;

namespace MTile;

public static class TileWorld
{
    public static readonly Polygon TileShape = Polygon.CreateRectangle(Chunk.TileSize, Chunk.TileSize);
}
