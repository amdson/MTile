using System;
using System.Collections.Generic;

namespace MTile;

// Plain-data capture of a World: the entity-slot bookkeeping plus a per-store
// snapshot (each value is the opaque object returned by IComponentStore.Capture).
// Holds only value arrays and those opaque blobs — no live references into the World
// — so it can outlive any number of Steps and be restored repeatedly (rollback).
public sealed class WorldSnapshot
{
    public int[]  Generations;
    public bool[] Alive;
    public int[]  Free;          // free-slot stack contents, top-first (pop order)
    public int    AliveCount;

    // Keyed by component type → that store's captured arrays. Restored by handing each
    // blob back to the matching live store via IComponentStore.RestoreFrom.
    public Dictionary<Type, object> Stores;
}
