namespace MTile;

// Deterministic, snapshot-able source of Hitbox.HitId values. One instance is
// shared across all players + entities in a Simulation so ids are globally unique
// (CombatSystem dedupes per (HitId, Target); colliding ids across sources would
// dedupe attacks that should be independent).
//
// Replaces the former per-class `static int _nextHitId` counters, which were
// process-global and leaked across rollback boundaries: replaying a frame would
// mint different ids than the original run, desyncing the dedupe table. The whole
// state here is a single int, so snapshot/restore (roadmap §4) is trivial.
//
// Single-threaded by contract — the sim advances on one thread, so no Interlocked
// is needed (and determinism forbids racy increments anyway).
public sealed class HitIdAllocator
{
    private int _next;

    public HitIdAllocator(int start = 0) => _next = start;

    // Pre-increment, matching the old Interlocked.Increment semantics (first id is 1).
    public int Next() => ++_next;

    // Snapshot/restore hook (roadmap §4): the entire allocator state is this value.
    public int Value
    {
        get => _next;
        set => _next = value;
    }
}
