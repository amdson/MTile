using Microsoft.Xna.Framework;

namespace MTile;

public class Chunk
{
    public const int Size = 32;
    public const int TileSize = 16;

    public Point ChunkPos;
    public readonly Tile[,] Tiles = new Tile[Size, Size];

    public Vector2 WorldPosition => new(ChunkPos.X * Size * TileSize, ChunkPos.Y * Size * TileSize);
}
