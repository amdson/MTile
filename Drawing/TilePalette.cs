using Microsoft.Xna.Framework;

namespace MTile;

// Single source of truth for tile base colors. Shared by the chunk renderer (tile
// fills), the HUD (block-picker swatches), and the tile-break particle burst tint.
public static class TilePalette
{
    public static Color BaseColor(TileType type) => type switch
    {
        TileType.Sand  => new Color(228, 190, 100),  // golden yellow (backdrop highlights)
        TileType.Dirt  => new Color(190, 130,  85),  // tan clay (backdrop foreground ridges)
        TileType.Stone => new Color(115,  82,  56),  // sandy dark brown (backdrop shadows)
        TileType.Foam  => new Color(235, 240, 250),  // near-white, faint blue tint
        // Darkened stone: the same brown pulled most of the way to black and cooled
        // slightly, so it reads as stone that has been compacted rather than as a
        // different rock. Dark enough that a hardened seam is obvious against Stone
        // (115,82,56) at a glance, which is the whole point — the player needs to know
        // not to swing at it.
        TileType.Hardened => new Color(44, 38, 40),
        _              => Color.Gray,
    };

    // Spike-overlay tint by owning side, so a player can tell their own spikes (safe)
    // from an opponent's (not) at a glance. Saturated and light so the teeth read
    // against every material fill, dark stone included.
    public static Color SpikeColor(Faction side) => side switch
    {
        Faction.Player1 => new Color(120, 200, 255),   // cool blue
        Faction.Player2 => new Color(255, 120, 110),   // warm red
        Faction.Enemy   => new Color(200, 120, 255),   // violet
        _               => new Color(230, 230, 230),
    };
}
