using System;

namespace MTile;

// Non-generic handle so World can hold a heterogeneous set of component stores in
// one dictionary and drive the entity-wide operations (remove-on-destroy, snapshot)
// without knowing each component's concrete type.
internal interface IComponentStore
{
    bool HasEntity(int entityIndex);
    void RemoveEntity(int entityIndex);
    void Clear();
    object Capture();
    void RestoreFrom(object snapshot);
}

// Sparse-set storage for one component type. Three parallel arrays:
//   _sparse[entityIndex]  → packed slot, or -1 if the entity has no T
//   _dense[slot]          → the owning EntityId (full id, so queries can yield it)
//   _data[slot]           → the component value
// The packed arrays stay contiguous (no holes), so iteration walks _data[0.._count)
// linearly — cache-friendly.
//
// Removal is ORDER-PRESERVING (shift the tail down, not swap-with-last): iteration
// stays in insertion order even after removals. MTile's determinism relies on this —
// the sim steps and hashes bodies/entities in stable spawn order, and a restore
// re-registers in that same order, so the World must reproduce it. Removal is O(n)
// in the tail length; entity counts are small, so that's fine.
//
// T must be a plain-data struct for snapshot purposes IF the store is World-snapshotted
// (Capture/Restore array-copy the values, so a mutable reference field would alias the
// live store into the snapshot). Stores that hold class refs (PhysicsBodyComponent,
// EntityRef, …) are marked live-only on the World and are never captured.
internal sealed class ComponentStore<T> : IComponentStore where T : struct
{
    private int[]      _sparse;
    private EntityId[] _dense;
    private T[]        _data;
    private int        _count;

    // Optional deep-clone hook. Pure-value structs leave this null and snapshot via a
    // shallow array copy (correct — every field is a value). Structs that wrap class
    // refs whose state is mutated in place (BodyState's contact list, a player's cloned
    // ability/gesture helpers) set a cloner so capture/restore deep-copy each element
    // instead of aliasing the live store into the snapshot. Applied symmetrically on
    // BOTH capture and restore so one snapshot can be restored repeatedly (the rollback
    // case re-restores the same frame) without sharing mutable refs.
    public Func<T, T> Cloner;

    public ComponentStore(int capacity = 8)
    {
        _sparse = NewSparse(capacity);
        _dense  = new EntityId[capacity];
        _data   = new T[capacity];
        _count  = 0;
    }

    public int Count => _count;

    // Packed-slot accessors used by Query enumerators.
    public EntityId EntityAt(int slot) => _dense[slot];
    public ref T    DataAt(int slot)   => ref _data[slot];

    public ref T Add(EntityId e)
    {
        EnsureSparse(e.Index);
        if (_sparse[e.Index] >= 0)
            throw new InvalidOperationException($"Entity {e} already has component {typeof(T).Name}.");
        if (_count == _data.Length) GrowDense();

        int slot = _count++;
        _sparse[e.Index] = slot;
        _dense[slot]     = e;
        _data[slot]      = default;
        return ref _data[slot];
    }

    public bool HasEntity(int entityIndex)
        => entityIndex >= 0 && entityIndex < _sparse.Length && _sparse[entityIndex] >= 0;

    public ref T Get(int entityIndex)
    {
        if (!HasEntity(entityIndex))
            throw new InvalidOperationException($"Entity index {entityIndex} has no component {typeof(T).Name}.");
        return ref _data[_sparse[entityIndex]];
    }

    public void RemoveEntity(int entityIndex)
    {
        if (!HasEntity(entityIndex)) return;
        int slot = _sparse[entityIndex];
        int last = --_count;

        // Shift the tail down one to close the hole, preserving insertion order.
        // Each shifted element's sparse mapping moves back by one slot.
        for (int i = slot; i < last; i++)
        {
            _dense[i] = _dense[i + 1];
            _data[i]  = _data[i + 1];
            _sparse[_dense[i].Index] = i;
        }

        _sparse[entityIndex] = -1;
        _dense[last] = default;
        _data[last]  = default;
    }

    public void Clear()
    {
        Array.Fill(_sparse, -1);
        Array.Clear(_dense, 0, _count);
        Array.Clear(_data, 0, _count);
        _count = 0;
    }

    private void EnsureSparse(int entityIndex)
    {
        if (entityIndex < _sparse.Length) return;
        int len = _sparse.Length;
        while (len <= entityIndex) len *= 2;
        var grown = NewSparse(len);
        Array.Copy(_sparse, grown, _sparse.Length);
        _sparse = grown;
    }

    private void GrowDense()
    {
        Array.Resize(ref _dense, _dense.Length * 2);
        Array.Resize(ref _data,  _data.Length * 2);
    }

    private static int[] NewSparse(int len)
    {
        var a = new int[len];
        Array.Fill(a, -1);
        return a;
    }

    // ── Snapshot ────────────────────────────────────────────────────────────────
    // Clone on both capture AND restore so a snapshot can be restored repeatedly
    // (the rollback case re-restores the same frame) without aliasing the live arrays.
    private sealed class Snap
    {
        public int[]      Sparse;
        public EntityId[] Dense;
        public T[]        Data;
        public int        Count;
    }

    public object Capture() => new Snap
    {
        Sparse = (int[])_sparse.Clone(),
        Dense  = (EntityId[])_dense.Clone(),
        Data   = CloneData(_data, _count),
        Count  = _count,
    };

    public void RestoreFrom(object snapshot)
    {
        var s = (Snap)snapshot;
        _sparse = (int[])s.Sparse.Clone();
        _dense  = (EntityId[])s.Dense.Clone();
        _data   = CloneData(s.Data, s.Count);
        _count  = s.Count;
    }

    // Shallow array copy when no cloner is set (pure-value T); otherwise per-element
    // deep clone of the live slots (0..count). Slots past count are unused and left
    // default — symmetric with Clear/RemoveEntity, which default the tail.
    private T[] CloneData(T[] src, int count)
    {
        if (Cloner == null) return (T[])src.Clone();
        var copy = new T[src.Length];
        for (int i = 0; i < count; i++) copy[i] = Cloner(src[i]);
        return copy;
    }
}
