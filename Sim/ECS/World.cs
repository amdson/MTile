using System;
using System.Collections.Generic;

namespace MTile;

// Hand-rolled minimal ECS world (Plans/ECS_MIGRATION_PLAN.md, Phase 0). Owns entity
// slots (with generations) and a set of sparse-set component stores. One World per
// Simulation — instance-owned, never static, so it snapshots/restores cleanly for
// rollback.
//
// Entity identity: an EntityId is { slot Index, Generation }. A slot's generation is
// bumped each time it's (re)allocated, so an EntityId held after its entity was
// destroyed fails IsAlive even once the slot is recycled for a new entity. Slot 0 is
// reserved so EntityId.None (Index 0) is never a live entity.
public sealed class World
{
    // Per-slot generation + liveness. Index 0 is reserved (see above), so both lists
    // start with a dummy entry.
    private readonly List<int>  _generations = new() { 0 };
    private readonly List<bool> _alive       = new() { false };
    private readonly Stack<int> _free        = new();   // recyclable slot indices
    private readonly Dictionary<Type, IComponentStore> _stores = new();
    // Component types whose store holds class references (e.g. PhysicsBodyComponent,
    // EntityRef) and therefore can't be value-snapshotted — capturing the array would
    // alias live objects. These are skipped by Capture and cleared by Restore; their
    // owner (Simulation) rebuilds them from the rehydrated entities after a restore.
    private readonly HashSet<Type> _liveOnly = new();
    private int _aliveCount;

    public int Count => _aliveCount;

    // Total slots ever allocated (never shrinks; recycled slots stay counted). Used by
    // the determinism checksum as the analogue of the old monotonic id counter.
    public int SlotCount => _generations.Count;

    public EntityId Create()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            index = _generations.Count;
            _generations.Add(0);
            _alive.Add(false);
        }
        _generations[index]++;     // new occupant ⇒ new generation
        _alive[index] = true;
        _aliveCount++;
        return new EntityId(index, _generations[index]);
    }

    public bool IsAlive(EntityId e)
        => e.Index > 0
        && e.Index < _generations.Count
        && _alive[e.Index]
        && _generations[e.Index] == e.Generation;

    public void Destroy(EntityId e)
    {
        if (!IsAlive(e)) return;
        // Drop every component this entity owned. Iterating all stores is fine — each
        // RemoveEntity is a no-op for stores the entity wasn't in.
        foreach (var store in _stores.Values) store.RemoveEntity(e.Index);
        _alive[e.Index] = false;
        _aliveCount--;
        _free.Push(e.Index);
    }

    public ref T Add<T>(EntityId e) where T : struct
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"Add<{typeof(T).Name}> on dead entity {e}.");
        return ref Store<T>().Add(e);
    }

    public ref T Get<T>(EntityId e) where T : struct
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"Get<{typeof(T).Name}> on dead entity {e}.");
        return ref Store<T>().Get(e.Index);
    }

    public bool Has<T>(EntityId e) where T : struct
        => IsAlive(e) && Store<T>().HasEntity(e.Index);

    public void Remove<T>(EntityId e) where T : struct
    {
        if (!IsAlive(e)) return;
        Store<T>().RemoveEntity(e.Index);
    }

    // Queries iterate the first type's store and gate on the rest, so iteration order
    // is the packed order of T1's store — deterministic given the same op sequence,
    // and preserved across snapshot/restore (packed arrays are captured verbatim).
    // Do not structurally modify the world (Create/Destroy/Add/Remove) mid-query.
    public Query<T1> Query<T1>() where T1 : struct
        => new(Store<T1>());

    public Query<T1, T2> Query<T1, T2>() where T1 : struct where T2 : struct
        => new(Store<T1>(), Store<T2>());

    public Query<T1, T2, T3> Query<T1, T2, T3>() where T1 : struct where T2 : struct where T3 : struct
        => new(Store<T1>(), Store<T2>(), Store<T3>());

    internal ComponentStore<T> Store<T>() where T : struct
    {
        if (!_stores.TryGetValue(typeof(T), out var s))
        {
            s = new ComponentStore<T>();
            _stores[typeof(T)] = s;
        }
        return (ComponentStore<T>)s;
    }

    // Mark a component type as live-only: its store holds class references that can't
    // be value-snapshotted, so Capture skips it and Restore clears it. The owner
    // rebuilds it after a restore. Idempotent; also materializes the store.
    public void MarkLiveOnly<T>() where T : struct
    {
        Store<T>();
        _liveOnly.Add(typeof(T));
    }

    // ── Snapshot / restore ───────────────────────────────────────────────────────
    public WorldSnapshot Capture()
    {
        var stores = new Dictionary<Type, object>(_stores.Count);
        foreach (var (type, store) in _stores)
        {
            if (_liveOnly.Contains(type)) continue;   // rebuilt by owner on restore
            stores[type] = store.Capture();
        }
        return new WorldSnapshot
        {
            Generations = _generations.ToArray(),
            Alive       = _alive.ToArray(),
            Free        = _free.ToArray(),      // LIFO/pop order
            AliveCount  = _aliveCount,
            Stores      = stores,
        };
    }

    // Restores against the same World instance. Assumes the set of component-store
    // types is stable between capture and restore (true for rollback: the same code
    // path registers the same components). Live stores absent from the snapshot are
    // cleared; stores present are restored.
    public void Restore(WorldSnapshot snap)
    {
        _generations.Clear(); _generations.AddRange(snap.Generations);
        _alive.Clear();       _alive.AddRange(snap.Alive);
        _aliveCount = snap.AliveCount;

        _free.Clear();
        // Stack.ToArray() yields top-first; push back-to-front to restore pop order.
        for (int i = snap.Free.Length - 1; i >= 0; i--) _free.Push(snap.Free[i]);

        foreach (var (type, store) in _stores)
        {
            // Live-only stores aren't in the snapshot — clear them; the owner rebuilds
            // from the rehydrated entities. Value stores restore from their blob, or
            // clear if absent from the snapshot.
            if (_liveOnly.Contains(type)) store.Clear();
            else if (snap.Stores.TryGetValue(type, out var s)) store.RestoreFrom(s);
            else store.Clear();
        }
    }
}
