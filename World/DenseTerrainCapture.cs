using Microsoft.Xna.Framework;

namespace MTile;

// A FULL, self-contained copy of the dense tile grid — every loaded chunk's State+Type
// cells — captured independently of the TerrainJournal.
//
// Why not reuse SimSnapshot's TerrainSnapshot? That one is an inverse-delta tuned for
// rollback (restore, then RE-SIMULATE forward), and TerrainJournal.RewindTo TRUNCATES
// history past the restored mark. That's correct for netcode but fatal for the in-game
// recorder's free scrubbing: jump back a few frames then forward again and the dense
// grid is stuck, because the entries needed to roll forward were truncated. This capture
// stores each frame's grid outright, so any frame restores in any order.
//
// Cheap enough to keep one per recorded frame: a chunk is 16x16 cells x 2 bytes = 512 B.
// Sprout refs are NOT stored — they're re-linked from the (separately value-snapshotted)
// sprout graph on restore, exactly as ChunkMap.RestoreTerrain does.
public sealed class DenseTerrainCapture
{
    public struct ChunkCells
    {
        public Point       Pos;
        public TileState[] State;   // flattened, index = tx * Chunk.Size + ty
        public TileType[]  Type;
    }

    public ChunkCells[] Chunks;
}
