using Microsoft.Xna.Framework;

namespace MTile;

public static class TileWorld
{
    public static readonly Polygon TileShape = Polygon.CreateRectangle(Chunk.TileSize, Chunk.TileSize);

    public static Vector2 TileCenter(Point chunkPos, Point tileIndex) => new(
        chunkPos.X * Chunk.Size * Chunk.TileSize + (tileIndex.X + 0.5f) * Chunk.TileSize,
        chunkPos.Y * Chunk.Size * Chunk.TileSize + (tileIndex.Y + 0.5f) * Chunk.TileSize);

    public static bool IsSolid(ChunkMap chunks, Point chunkPos, Point tileIndex) =>
        chunks.TryGet(chunkPos, out var chunk) && chunk.Tiles[tileIndex.X, tileIndex.Y].IsSolid;
}
