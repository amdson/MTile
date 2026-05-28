using System;

namespace MTile;

// Value identity for a simulation entity (player, enemy, projectile, prop).
// Replaces the direct object references that combat / hurtbox / hitbox code used
// to hold, so durable cross-frame state (the combat dedupe table) and snapshots
// key on a stable value instead of a pointer that goes stale across a restore.
//
// Phase 1 of the ECS migration (Plans/ECS_MIGRATION_PLAN.md) introduces this
// type and routes every cross-entity reference through it; the Index is minted
// by Simulation's deterministic id counter. Generation is reserved for the
// later World layer (it bumps on entity destroy so a stale id fails a liveness
// check) — in this phase it's always 0.
public readonly struct EntityId : IEquatable<EntityId>
{
    public readonly int Index;
    public readonly int Generation;

    public EntityId(int index, int generation = 0)
    {
        Index      = index;
        Generation = generation;
    }

    // The null entity. Minted ids start at Index 1 (Simulation's counter
    // pre-increments), so None never collides with a live entity.
    public static readonly EntityId None = default;

    public bool IsNone => Index == 0 && Generation == 0;

    public bool Equals(EntityId other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object obj) => obj is EntityId e && Equals(e);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(EntityId a, EntityId b) => a.Equals(b);
    public static bool operator !=(EntityId a, EntityId b) => !a.Equals(b);
    public override string ToString() => $"E{Index}.{Generation}";
}
