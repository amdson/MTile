namespace MTile;
using Microsoft.Xna.Framework;
using System;


// Reference to a single tile in the world by its global tile indices. Constructed
// by TileQuery.SolidTilesInRect; consumed by the side / floor / ceiling probes that
// translate the integer cell back into world-space pixels for collision math.
//
// Storage is the global (Gtx, Gty) pair to match the (gtx, gty) convention used
// elsewhere in the sim (PhysicsWorld impact cells, ChunkMap.DamageCell / GetCellType).
// Chunk membership and local coordinates derive from the global indices — provided
// as read-only properties so callers that need them don't have to repeat the
// shift/mask. World* accessors stay as derived getters so existing collision /
// probe code keeps reading pixels without any caller-side change.
public readonly struct TileRef
{
    public readonly int Gtx;
    public readonly int Gty;

    public TileRef(int gtx, int gty) { Gtx = gtx; Gty = gty; }

    // Chunk + local membership. Chunk.Size is a power of two (16), so shift/mask
    // is exact for non-negative coordinates; for negative globals (chunks left of
    // the origin) we need floor-division / euclidean-mod semantics, hence the
    // explicit branch rather than `& 15` which would round toward zero.
    public int ChunkX => Gtx >= 0 ? Gtx / Chunk.Size : (Gtx - Chunk.Size + 1) / Chunk.Size;
    public int ChunkY => Gty >= 0 ? Gty / Chunk.Size : (Gty - Chunk.Size + 1) / Chunk.Size;
    public int LocalX => Gtx - ChunkX * Chunk.Size;
    public int LocalY => Gty - ChunkY * Chunk.Size;

    public float WorldLeft    => Gtx * Chunk.TileSize;
    public float WorldTop     => Gty * Chunk.TileSize;
    public float WorldRight   => WorldLeft + Chunk.TileSize;
    public float WorldBottom  => WorldTop  + Chunk.TileSize;
    public float WorldCenterX => WorldLeft + Chunk.TileSize * 0.5f;
    public float WorldCenterY => WorldTop  + Chunk.TileSize * 0.5f;
}

public enum TileState : byte
{
    Empty,
    Sprouting,
    Solid,
}

// Material type. Each type has its own max-HP (TileDamage.MaxHPFor) and render color
// (Game1.GetTileBaseColor). Stored as a byte for compactness; default = Stone for
// freshly-created tiles (sprout-spawned tiles, file-loaded chunks). Terrain generation
// assigns types by depth.
public enum TileType : byte
{
    Stone,
    Dirt,
    Sand,
    // Cheap throwaway material. Half the HP of dirt and decays back to Empty
    // after FoamDecay.DefaultLifetime seconds. Player-selectable via the block
    // picker; not produced by terrain generation. Useful as scaffolding /
    // temporary cover during combat.
    Foam,
}

public enum EdgeType : byte
{
    None,
    Left,
    Right,
    Top,
    Bottom
}

public enum CornerType : byte
{
    None,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public readonly struct EdgeRef
{
    public readonly TileRef Tile;
    public readonly EdgeType Type;

    public EdgeRef(TileRef tile, EdgeType type) { Tile = tile; Type = type; }
    // Public get segment function returns the edge as a line segment in world coordinates, for convenience of probe code that needs to treat edges as segments. The tile's world position is the top-left corner, so we add the appropriate offsets for the other corners.
    public (Vector2 Start, Vector2 End) WorldSegment => Type switch
    {
        EdgeType.Left   => (new Vector2(Tile.WorldLeft, Tile.WorldTop), new Vector2(Tile.WorldLeft, Tile.WorldBottom)),
        EdgeType.Right  => (new Vector2(Tile.WorldRight, Tile.WorldTop), new Vector2(Tile.WorldRight, Tile.WorldBottom)),
        EdgeType.Top    => (new Vector2(Tile.WorldLeft, Tile.WorldTop), new Vector2(Tile.WorldRight, Tile.WorldTop)),
        EdgeType.Bottom => (new Vector2(Tile.WorldLeft, Tile.WorldBottom), new Vector2(Tile.WorldRight, Tile.WorldBottom)),
        _ => throw new InvalidOperationException($"Invalid edge type {Type}")
    };
}

public readonly struct CornerRef
{
    public readonly TileRef Tile;
    public readonly CornerType Type;

    public CornerRef(TileRef tile, CornerType type) { Tile = tile; Type = type; }
    // Public get pos function returns position of corner in world as a Vector2, for convenience of probe code that needs to treat corners as points. The tile's world position is the top-left corner, so we add the appropriate offsets for the other corners.
    public Vector2 WorldPos => Type switch
    {
        CornerType.TopLeft     => new Vector2(Tile.WorldLeft, Tile.WorldTop),
        CornerType.TopRight    => new Vector2(Tile.WorldRight, Tile.WorldTop),
        CornerType.BottomLeft  => new Vector2(Tile.WorldLeft, Tile.WorldBottom),
        CornerType.BottomRight => new Vector2(Tile.WorldRight, Tile.WorldBottom),
        _ => throw new InvalidOperationException($"Invalid corner type {Type}")
    };
}

public static class TileUtils
    {
        public static EdgeType FlipVertical(EdgeType edge) => edge switch
        {
            EdgeType.Left   => EdgeType.Left,
            EdgeType.Right  => EdgeType.Right,
            EdgeType.Top    => EdgeType.Bottom,
            EdgeType.Bottom => EdgeType.Top,
            _ => EdgeType.None
        };

        public static EdgeType FlipHorizontal(EdgeType edge) => edge switch
        {
            EdgeType.Left   => EdgeType.Right,
            EdgeType.Right  => EdgeType.Left,
            EdgeType.Top    => EdgeType.Top,
            EdgeType.Bottom => EdgeType.Bottom,
            _ => EdgeType.None
        };

        public static CornerType FlipVertical(CornerType corner) => corner switch
        {
            CornerType.TopLeft     => CornerType.BottomLeft,
            CornerType.TopRight    => CornerType.BottomRight,
            CornerType.BottomLeft  => CornerType.TopLeft,
            CornerType.BottomRight => CornerType.TopRight,
            _ => CornerType.None
        };

        public static CornerType FlipHorizontal(CornerType corner) => corner switch
        {
            CornerType.TopLeft     => CornerType.TopRight,
            CornerType.TopRight    => CornerType.TopLeft,
            CornerType.BottomLeft  => CornerType.BottomRight,
            CornerType.BottomRight => CornerType.BottomLeft,
            _ => CornerType.None
        };
    }

public struct Tile
{
    public TileState State;
    public TileType  Type;          // material; meaningful only when State == Solid
    public TileSproutNode Sprout;   // non-null iff State == Sprouting (i.e. node is in Growing status)

    // Back-compat property. Existing call sites that read or assign IsSolid keep
    // working; the underlying state machine is the source of truth.
    public bool IsSolid
    {
        get => State == TileState.Solid;
        set
        {
            State = value ? TileState.Solid : TileState.Empty;
            Sprout = null;
        }
    }
}
