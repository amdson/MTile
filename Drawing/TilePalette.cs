using Microsoft.Xna.Framework;

namespace MTile;

// Single source of truth for tile base colors. Shared by the chunk renderer (tile
// fills), the HUD (block-picker swatches), and the tile-break particle burst tint.
public static class TilePalette
{
    public static Color BaseColor(TileType type) => type switch
    {
        TileType.Sand  => new Color(220, 200, 150),  // warm light sandy
        TileType.Dirt  => new Color(110,  75,  45),  // earthy mid-brown
        TileType.Stone => Color.Gray,                 // existing
        TileType.Foam  => new Color(235, 240, 250),  // near-white, faint blue tint
        _              => Color.Gray,
    };
}
