using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Plain-data capture of an entire Simulation (minus terrain — chunks/tiles get a
// separate journal mechanism, roadmap goal 6). Holds only value structs, arrays of
// value structs, and id-keyed maps — no live references into the running sim — so a
// SimSnapshot can outlive any number of Steps and be restored repeatedly (the
// rollback use case re-restores the same frame). Produced by Simulation.Snapshot()
// and consumed by Simulation.Restore().
public sealed class SimSnapshot
{
    // Sim-level scalars.
    public int   HitIdValue;            // HitIdAllocator.Value
    public float Elapsed;               // absolute sim clock (drives platform tickers)

    // ECS world identity bookkeeping (slot generations + free list). Owns EntityId
    // allocation; the live-only ref stores (bodies/entities/players) are rebuilt from
    // the entity/player snapshots below, not captured here.
    public WorldSnapshot World;

    // Players. The primary plus any secondaries, each with its own controller ring.
    public PlayerSnapshot   Primary;
    public ControllerState  PrimaryController;
    public PlayerSnapshot[]  Secondaries;
    public ControllerState[] SecondaryControllers;

    // Entities, in spawn order (so the rebuilt _entities/_bodies lists keep the same
    // iteration order the original run had — matters for deterministic stepping).
    public EntitySnapshot[] Entities;

    // Combat dedupe table, keyed HitId → set of already-hit EntityIds. See
    // CombatSystem.CaptureDedupe.
    public Dictionary<int, EntityId[]> Dedupe;

    // Moving-platform poses, in registration order. Position+velocity is enough since
    // the tickers re-derive motion purely from Elapsed.
    public PlatformState[] Platforms;

    // Terrain (roadmap goal 6): a journal mark for the dense tile grid + value copies
    // of the sparse side-structures. Restored against the same ChunkMap instance.
    public TerrainSnapshot Terrain;
}

// Snapshot of one moving platform's kinematic state (roadmap goal 4 §H).
public struct PlatformState
{
    public Vector2 Position;
    public Vector2 Velocity;
}
