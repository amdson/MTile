using System;
using System.Collections.Generic;

namespace MTile;

// Sparse per-cell decay timer for Foam tiles. Foam is the only TileType that
// disappears on its own (other tiles only break under damage), so this lives
// off the normal damage path. Lifecycle:
//   * Foam sprout finalizes → ChunkMap registers (gtx, gty, lifetime).
//   * Each TickSprouts call decrements all remaining timers.
//   * When a timer hits zero → ChunkMap.BreakCell on that cell.
//   * If a foam cell is broken early (by damage / overwrite), ChunkMap.Clear's
//     the entry so we don't try to break the (now empty) cell later.
public class FoamDecay
{
    private readonly Dictionary<(int gtx, int gty), float> _remaining = new();
    public const float DefaultLifetime = 4f;

    public void Register(int gtx, int gty, float lifetime = DefaultLifetime)
        => _remaining[(gtx, gty)] = lifetime;

    public void Clear(int gtx, int gty)
        => _remaining.Remove((gtx, gty));

    public float? GetRemaining(int gtx, int gty)
        => _remaining.TryGetValue((gtx, gty), out var r) ? r : null;

    // Decrement all entries; invoke onExpire for each cell that hit zero this
    // step. Caller wires it to BreakCell so the world actually changes.
    public void Tick(float dt, Action<int, int> onExpire)
    {
        if (_remaining.Count == 0) return;
        // Snapshot keys so we can mutate the dict mid-iteration.
        var keys = new List<(int, int)>(_remaining.Keys);
        foreach (var k in keys)
        {
            float r = _remaining[k] - dt;
            if (r <= 0f)
            {
                _remaining.Remove(k);
                onExpire(k.Item1, k.Item2);
            }
            else _remaining[k] = r;
        }
    }
}
