using System.Collections.Generic;

namespace MTile.Tests.Sim;

// Test helpers for spike tiles (TileState.Spike). A spike lives in the OPEN cell beside
// the solid it clings to, so "spike the floor cell (x, FloorRow)" is a spike tile at
// (x, FloorRow - 1).
public static class SpikeTiles
{
    // Place a spike tile and skip its seed telegraph: armed this frame.
    public static void PlaceArmed(ChunkMap chunks, int gtx, int gty, int team)
    {
        Xunit.Assert.True(chunks.TryRequestSpike(gtx, gty, team), $"spike refused at ({gtx},{gty})");
        chunks.SpikeSeeds.Clear(gtx, gty);
    }

    // Every spike tile in the loaded grid (armed or seeding).
    public static List<(int gtx, int gty)> All(ChunkMap chunks)
    {
        var list = new List<(int, int)>();
        foreach (var chunk in chunks)
            for (int tx = 0; tx < Chunk.Size; tx++)
            for (int ty = 0; ty < Chunk.Size; ty++)
                if (chunk.Tiles[tx, ty].State == TileState.Spike)
                    list.Add((chunk.ChunkPos.X * Chunk.Size + tx, chunk.ChunkPos.Y * Chunk.Size + ty));
        return list;
    }

    public static int Count(ChunkMap chunks) => All(chunks).Count;

    // Spike tiles done seeding — the ones that bite.
    public static int ArmedCount(ChunkMap chunks) => All(chunks).FindAll(c => !chunks.SpikeSeeds.Has(c.gtx, c.gty)).Count;
}
