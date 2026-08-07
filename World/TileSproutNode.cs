using System;
using Microsoft.Xna.Framework;

namespace MTile;

public enum TileSproutStatus : byte { Pending, Growing }

// Which neighbouring cells are supporting (parenting) a growing sprout. The name
// is the *direction of the parent*, so Below means the parent cell is at
// (gtx, gty+1) and this sprout's volume for that face travels upward.
[Flags]
public enum SproutFaces : byte
{
    None  = 0,
    Below = 1,
    Left  = 2,
    Right = 4,
    Above = 8,
}

// A node in the TileSproutGraph. Pending = a requested ("ghost") cell with no
// solid neighbour yet. Growing = at least one solid neighbour supports it, and
// it is emitting one moving volume per supporting face.
//
// There are no parent/child edges. A Pending cell is promoted by whichever
// neighbours are Solid at promotion time, which is what makes a build expand as
// a shell: every ghost adjacent to the ring that just solidified promotes on the
// same tick, and each takes *all* of its solid faces rather than picking one.
//
// Growth payload (Faces, Lifetime, Age) is only meaningful while Status ==
// Growing. Per-face geometry is derived, not stored — see VolumeCenter.
public sealed class TileSproutNode
{
    public readonly Point ChunkPos;
    public readonly int   Tx, Ty;     // local indices within the owning chunk
    public readonly int   Gtx, Gty;   // global cell coords (graph dedupe key)
    // Material type stamped onto the Tile when this sprout commits. Defaults to
    // Stone (matches the legacy build-input behavior); the block-eruption move
    // overrides to Dirt via TryRequestTile's type parameter.
    public TileType Type = TileType.Stone;

    public TileSproutStatus Status;
    // Supporting faces, frozen at promotion. Bits are only ever *cleared* after
    // that — when a parent cell is broken (ChunkMap.BreakCell). Faces == None on
    // a Growing node means the sprout has lost all support and is cancelled.
    public SproutFaces Faces;

    public float Lifetime;
    public float Age;

    // Iteration order for the four faces. Fixed and shared so every consumer
    // walks a node's volumes in the same order (determinism under rollback).
    public static readonly SproutFaces[] FaceOrder =
        { SproutFaces.Below, SproutFaces.Left, SproutFaces.Right, SproutFaces.Above };

    public TileSproutNode(Point chunkPos, int tx, int ty, int gtx, int gty)
    {
        ChunkPos = chunkPos;
        Tx = tx; Ty = ty;
        Gtx = gtx; Gty = gty;
        Status = TileSproutStatus.Pending;
    }

    // Cell-index offset from this cell to the parent cell on the given face.
    public static Point FaceOffset(SproutFaces face) => face switch
    {
        SproutFaces.Below => new Point(0,  1),
        SproutFaces.Left  => new Point(-1, 0),
        SproutFaces.Right => new Point(1,  0),
        SproutFaces.Above => new Point(0, -1),
        _                 => Point.Zero,
    };

    // The face of `cell` that points at its neighbour `other` (both are 4-adjacent).
    public static SproutFaces FaceTowards(int fromGtx, int fromGty, int toGtx, int toGty)
    {
        int dx = toGtx - fromGtx, dy = toGty - fromGty;
        if (dx ==  0 && dy ==  1) return SproutFaces.Below;
        if (dx == -1 && dy ==  0) return SproutFaces.Left;
        if (dx ==  1 && dy ==  0) return SproutFaces.Right;
        if (dx ==  0 && dy == -1) return SproutFaces.Above;
        return SproutFaces.None;
    }

    public void PromoteToGrowing(SproutFaces faces, float lifetime, float initialAge = 0f)
    {
        Status   = TileSproutStatus.Growing;
        Faces    = faces;
        Lifetime = MathF.Max(1e-4f, lifetime);
        // initialAge carries over the parent's overshoot — when a parent finalized
        // because its Age crossed Lifetime mid-frame, the leftover (Age - Lifetime)
        // is time that conceptually belongs to this sprout. Without it, growth
        // starts at the parent's center for one frame and the volume exactly
        // overlaps the just-Solid parent cell. See ChunkMap.TickSprouts.
        Age      = MathF.Max(0f, initialAge);
    }

    // Clamped so a frame with dt > Lifetime doesn't overshoot before TickSprouts
    // gets a chance to finalize.
    public float Progress => MathF.Min(1f, Age / Lifetime);

    public Vector2 CellCenter => new(
        Gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
        Gty * Chunk.TileSize + Chunk.TileSize * 0.5f);

    // Each supporting face emits its own full-size tile volume, translating from
    // that parent's cell center into this cell over Lifetime. Multi-face sprouts
    // therefore push out of every parent symmetrically; the volumes overlap in
    // the middle, which is harmless — collision takes the union.
    public Vector2 VolumeStart(SproutFaces face)
    {
        var o = FaceOffset(face);
        return CellCenter + new Vector2(o.X, o.Y) * Chunk.TileSize;
    }

    public Vector2 VolumeCenter(SproutFaces face)
        => Vector2.Lerp(VolumeStart(face), CellCenter, Progress);

    public Vector2 VolumeVelocity(SproutFaces face)
    {
        var o = FaceOffset(face);
        return new Vector2(-o.X, -o.Y) * (Chunk.TileSize / Lifetime);
    }

    public bool IsComplete => Age >= Lifetime;
}
