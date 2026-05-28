using System.Collections.Generic;

namespace MTile;

// Sparse per-cell HP store. Almost no tiles are damaged at any given moment, so this
// is a hashmap keyed on global cell coords rather than a field on every Tile. An
// entry only exists while a tile has accumulated > 0 damage; broken or fully
// undamaged tiles aren't present.
public class TileDamage
{
    private readonly Dictionary<(int gtx, int gty), float> _hp = new();

    // Normalized HP unit. Damage values in the codebase are tuned against this
    // (e.g. SlashDamagePerFrame = TileMaxHP / 2), so a Dirt tile (1.0 × TileMaxHP)
    // takes one full slash to break, regardless of how TileMaxHP is tuned.
    public const float TileMaxHP = 1.0f;

    // Per-type durability. Delegates to MaterialStrengths so the values can be
    // tuned from material_strengths.json without recompiling. Defaults there
    // match the legacy switch (Sand 0.5, Dirt 1.0, Stone 2.0, Foam 0.5),
    // expressed in the same TileMaxHP-multiple units.
    public static float MaxHPFor(TileType type) => MaterialStrengths.MaxHPFor(type);

    // Add `amount` damage to (gtx, gty). Returns true iff the tile crossed its
    // type-specific max HP and should be broken — the caller is responsible for
    // actually flipping the cell state (ChunkMap.DamageCell does both atomically).
    public bool ApplyDamage(int gtx, int gty, float amount, TileType type)
    {
        _hp.TryGetValue((gtx, gty), out float cur);
        cur += amount;
        if (cur >= MaxHPFor(type))
        {
            _hp.Remove((gtx, gty));
            return true;
        }
        _hp[(gtx, gty)] = cur;
        return false;
    }

    public float Get(int gtx, int gty)
        => _hp.TryGetValue((gtx, gty), out float v) ? v : 0f;

    // Called when a cell becomes Empty (broken or destroyed) so a future re-spawn at
    // the same coords doesn't inherit ghost damage.
    public void Clear(int gtx, int gty) => _hp.Remove((gtx, gty));

    // Iterate damaged cells. For debug overlays / cracks rendering.
    public IEnumerable<KeyValuePair<(int gtx, int gty), float>> Damaged => _hp;

    // Snapshot/restore (roadmap goal 6). Sparse + value-typed, so a dict copy is a
    // full deep copy with no aliasing into the live store.
    public Dictionary<(int gtx, int gty), float> Capture() => new(_hp);

    public void Restore(Dictionary<(int gtx, int gty), float> src)
    {
        _hp.Clear();
        if (src == null) return;
        foreach (var kv in src) _hp[kv.Key] = kv.Value;
    }
}
