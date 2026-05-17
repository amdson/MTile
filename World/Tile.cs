namespace MTile;

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
