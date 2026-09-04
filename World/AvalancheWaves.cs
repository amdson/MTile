using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Per-wave constants for avalanche provenance (AVALANCHE_RIDING_V2 Part 2). A wave
// is one MassBall's eruption: the ball carries no gravity and only scalar drag, so
// its direction never changes — one unit vector per wave is the complete macro-flow
// record, and everything per-cell (along-sweep position, etc.) is a dot product away.
//
// Entries are touched on every deposit and pruned on a generous horizon well past
// the longest cascade tail, so the table stays a handful of entries. Sim state:
// value-snapshotted into TerrainSnapshot alongside the other sparse tables.
public sealed class AvalancheWaves
{
    public struct WaveInfo
    {
        public Vector2 Direction;   // unit; Zero until a nonzero-velocity touch arrives
        public float   LastTouch;   // ChunkMap.SproutClock seconds
    }

    // Prune horizon. Deposits stop when the ball dies; the cascade + schedule lag
    // keep promoting for a while after. 30s dwarfs any of that without letting a
    // long session grow the table.
    private const float StaleSeconds = 30f;

    private readonly Dictionary<EntityId, WaveInfo> _waves = new();
    private readonly List<EntityId> _scratchPrune = new();

    // Register/refresh a wave. Direction is set by the first touch with a real
    // velocity and never overwritten — the ball's direction is constant, so a
    // later touch carries no new information.
    public void Touch(EntityId wave, Vector2 velocity, float now)
    {
        if (wave.IsNone) return;
        _waves.TryGetValue(wave, out var info);
        if (info.Direction == Vector2.Zero && velocity.LengthSquared() > 1f)
            info.Direction = Vector2.Normalize(velocity);
        info.LastTouch = now;
        _waves[wave] = info;
    }

    public bool TryGetDirection(EntityId wave, out Vector2 direction)
    {
        if (_waves.TryGetValue(wave, out var info) && info.Direction != Vector2.Zero)
        {
            direction = info.Direction;
            return true;
        }
        direction = Vector2.Zero;
        return false;
    }

    public void Tick(float now)
    {
        if (_waves.Count == 0) return;
        _scratchPrune.Clear();
        foreach (var kv in _waves)
            if (now - kv.Value.LastTouch > StaleSeconds) _scratchPrune.Add(kv.Key);
        foreach (var key in _scratchPrune) _waves.Remove(key);
    }

    // Snapshot/restore — dict copy is a deep copy (value-typed entries).
    public Dictionary<EntityId, WaveInfo> Capture() => new(_waves);

    public void Restore(Dictionary<EntityId, WaveInfo> s)
    {
        _waves.Clear();
        if (s == null) return;
        foreach (var kv in s) _waves[kv.Key] = kv.Value;
    }
}
