using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

public enum TileSproutStatus : byte { Pending, Growing }

// A node in the TileSproutGraph. Pending = waiting on at least one parent to
// finalize before growing. Growing = currently translating from a parent's
// center toward its target cell. Once age reaches Lifetime, the owning cell
// flips to TileState.Solid and the node is dropped from the graph.
//
// Movement payload (StartCenter, EndCenter, Velocity, Polygon, Lifetime, Age)
// is only valid while Status == Growing — it's stamped during PromoteToGrowing.
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
    // Parents that were Sprouting/Pending at request time. Cleared on promotion.
    public readonly List<TileSproutNode> SproutParents = new();
    // Forward edges, populated when this node is registered as a child of a parent.
    public readonly List<TileSproutNode> Children = new();

    public Vector2 StartCenter;
    public Vector2 EndCenter;
    public Vector2 Velocity;
    public Polygon Polygon;
    public float   Lifetime;
    public float   Age;

    public TileSproutNode(Point chunkPos, int tx, int ty, int gtx, int gty)
    {
        ChunkPos = chunkPos;
        Tx = tx; Ty = ty;
        Gtx = gtx; Gty = gty;
        Status = TileSproutStatus.Pending;
    }

    public void PromoteToGrowing(Vector2 startCenter, Vector2 endCenter, float lifetime)
    {
        Status      = TileSproutStatus.Growing;
        StartCenter = startCenter;
        EndCenter   = endCenter;
        Lifetime    = MathF.Max(1e-4f, lifetime);
        Velocity    = (endCenter - startCenter) / Lifetime;
        Polygon     = TileWorld.TileShape;
        Age         = 0f;
        SproutParents.Clear();
    }

    // Clamped lerp so a frame with dt > Lifetime doesn't overshoot before
    // TickSprouts gets a chance to finalize.
    public Vector2 Center
    {
        get
        {
            float t = MathF.Min(1f, Age / Lifetime);
            return Vector2.Lerp(StartCenter, EndCenter, t);
        }
    }

    public bool IsComplete => Age >= Lifetime;
}
