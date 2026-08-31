using Microsoft.Xna.Framework;

namespace MTile;

public class Chunk
{
    public const int Size = 16;
    public const int TileSize = 10;   // 2/3-scale blocks (was 16). Player Radius/StandingHeight deliberately unchanged.

    public Point ChunkPos;
    public readonly Tile[,] Tiles = new Tile[Size, Size];

    public Vector2 WorldPosition => new(ChunkPos.X * Size * TileSize, ChunkPos.Y * Size * TileSize);
}
