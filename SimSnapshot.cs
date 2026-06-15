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

    // The ECS world snapshot: slot generations + free list (EntityId bookkeeping) PLUS
    // every snapshotted value-component store — PlayerData, EntityData, and BodyStateComp.
    // This is the single rollback substrate now: players' and entities' serializable
    // state live here, not in separate per-type arrays. The live-only ref stores
    // (PlayerRef/EntityRef/PhysicsBodyComponent) are skipped and rebuilt on restore.
    // Spawn order is preserved by the order-preserving component stores.
    public WorldSnapshot World;

    // Player controllers. Controllers live outside the World (one input channel per
    // player), so their rings are captured here alongside the World snapshot.
    public ControllerState   PrimaryController;
    public ControllerState[] SecondaryControllers;

    // Combat dedupe table, keyed HitId → set of already-hit EntityIds. See
    // CombatSystem.CaptureDedupe.
    public Dictionary<int, EntityId[]> Dedupe;

    // Hit-confirm inbox, keyed HitId → entities connected on the snapshot frame. A
    // pending frame-N→N+1 message read by attackers the frame after a hit lands
    // (CombatSystem.PeekHits); must round-trip so a replay can't miss a connection.
    public Dictionary<int, int> HitConfirm;

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
