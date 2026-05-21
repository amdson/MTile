using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Reversible delta log for the dense tile grid (roadmap goal 6). The tile arrays are
// far too large to copy every frame for rollback, so instead every structural change
// to a cell — and every chunk that gets lazily materialized — is appended here as an
// inverse-recoverable entry. A snapshot records the journal's length (`Mark`); a
// restore replays the entries past that mark in reverse, undoing each, to roll the
// grid back to exactly its state at snapshot time.
//
// Only the *dense* grid is journaled. The small sparse side-structures (sprout graph,
// per-cell HP, foam timers, impact accumulator) mutate continuously (ages/timers tick
// every frame), which doesn't fit a delta log — they're value-snapshotted instead
// (see ChunkMap.CaptureTerrain). Every tile write in the sim funnels through ChunkMap,
// so journaling at those few sites captures all of it ("nothing mutates a chunk
// outside the journaled path").
//
// The log grows as the sim advances; netcode trims it once a frame is confirmed
// (no rollback can target before it). RewindTo truncates everything past the mark,
// so re-simulating forward after a restore re-appends fresh entries cleanly.
public sealed class TerrainJournal
{
    private enum Kind : byte { TileWrite, ChunkCreated }

    private struct Entry
    {
        public Kind      Kind;
        public Point     Chunk;
        public int       Tx, Ty;
        public TileState PrevState;   // TileWrite: cell's state/type BEFORE the write
        public TileType  PrevType;
    }

    private readonly List<Entry> _entries = new();

    // Current length — the value a snapshot stores and a later restore rewinds to.
    public int Mark => _entries.Count;

    // Record a cell's prior (state, type) before it is overwritten. Sprout refs are
    // NOT journaled — they're re-linked from the restored sprout graph (see ChunkMap).
    public void RecordTileWrite(Point chunk, int tx, int ty, TileState prevState, TileType prevType)
        => _entries.Add(new Entry { Kind = Kind.TileWrite, Chunk = chunk, Tx = tx, Ty = ty, PrevState = prevState, PrevType = prevType });

    // Record that a chunk was lazily created (a sprout/build landed in an unloaded
    // chunk). Inverse drops it so _dict matches the snapshot exactly.
    public void RecordChunkCreated(Point chunk)
        => _entries.Add(new Entry { Kind = Kind.ChunkCreated, Chunk = chunk });

    // Undo every entry past `mark`, newest first, then truncate. revertTile restores a
    // cell's prior state/type; removeChunk drops a lazily-created chunk. Both write
    // directly (no re-journaling, no break events).
    public void RewindTo(int mark,
                         Action<Point, int, int, TileState, TileType> revertTile,
                         Action<Point> removeChunk)
    {
        for (int i = _entries.Count - 1; i >= mark; i--)
        {
            var e = _entries[i];
            if (e.Kind == Kind.TileWrite) revertTile(e.Chunk, e.Tx, e.Ty, e.PrevState, e.PrevType);
            else                          removeChunk(e.Chunk);
        }
        if (mark < _entries.Count) _entries.RemoveRange(mark, _entries.Count - mark);
    }

    // Drop confirmed history before `mark` (netcode hook — once a frame can no longer
    // be rolled back to, its deltas are dead weight). Shifts the effective origin;
    // outstanding marks must be rebased by the caller. Unused by the first pass.
    public void TrimBefore(int mark)
    {
        if (mark <= 0) return;
        _entries.RemoveRange(0, Math.Min(mark, _entries.Count));
    }
}
