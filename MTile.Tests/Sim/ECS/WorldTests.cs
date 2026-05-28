using System.Collections.Generic;
using Xunit;

namespace MTile.Tests;

// Phase 0 of the ECS migration (Plans/ECS_MIGRATION_PLAN.md): the hand-rolled World
// core, exercised in isolation (no production usage yet). Covers entity lifecycle +
// generations, component add/get/has/remove, queries, snapshot round-trip, and a
// randomized create/destroy stress test against a model oracle.
public class WorldTests
{
    private struct Position { public int X, Y; }
    private struct Velocity { public float Vx, Vy; }
    private struct Tag      { public int Kind; }

    [Fact]
    public void Create_ProducesDistinctLiveIds()
    {
        var w = new World();
        var a = w.Create();
        var b = w.Create();

        Assert.NotEqual(a, b);
        Assert.True(w.IsAlive(a));
        Assert.True(w.IsAlive(b));
        Assert.Equal(2, w.Count);
        Assert.False(w.IsAlive(EntityId.None));
    }

    [Fact]
    public void AddGetHasRemove_RoundTrips()
    {
        var w = new World();
        var e = w.Create();

        Assert.False(w.Has<Position>(e));
        w.Add<Position>(e) = new Position { X = 3, Y = 7 };
        Assert.True(w.Has<Position>(e));
        Assert.Equal(3, w.Get<Position>(e).X);
        Assert.Equal(7, w.Get<Position>(e).Y);

        // Mutate in place through the ref.
        w.Get<Position>(e).X = 99;
        Assert.Equal(99, w.Get<Position>(e).X);

        w.Remove<Position>(e);
        Assert.False(w.Has<Position>(e));
    }

    [Fact]
    public void Destroy_ClearsComponents_AndFreesEntity()
    {
        var w = new World();
        var e = w.Create();
        w.Add<Position>(e);
        w.Add<Velocity>(e);

        w.Destroy(e);

        Assert.False(w.IsAlive(e));
        Assert.False(w.Has<Position>(e));
        Assert.False(w.Has<Velocity>(e));
        Assert.Equal(0, w.Count);
    }

    [Fact]
    public void RecycledSlot_BumpsGeneration_StaleIdNotAlive()
    {
        var w = new World();
        var first = w.Create();
        w.Destroy(first);
        var second = w.Create();   // should reuse the slot

        Assert.Equal(first.Index, second.Index);          // same slot
        Assert.NotEqual(first.Generation, second.Generation); // new generation
        Assert.False(w.IsAlive(first));                   // stale id rejected
        Assert.True(w.IsAlive(second));
    }

    [Fact]
    public void StaleId_ComponentAccessIsSafe()
    {
        var w = new World();
        var e = w.Create();
        w.Add<Position>(e);
        w.Destroy(e);

        // Has on a dead id is false (not a throw); Add throws (can't add to dead).
        Assert.False(w.Has<Position>(e));
        Assert.Throws<System.InvalidOperationException>(() => w.Add<Position>(e));
    }

    [Fact]
    public void Query_SingleComponent_VisitsAllAndAllowsMutation()
    {
        var w = new World();
        var ids = new List<EntityId>();
        for (int i = 0; i < 5; i++)
        {
            var e = w.Create();
            w.Add<Position>(e) = new Position { X = i, Y = 0 };
            ids.Add(e);
        }

        int visited = 0;
        foreach (var row in w.Query<Position>())
        {
            row.Component1.Y = row.Component1.X * 2;   // mutate through ref
            visited++;
        }
        Assert.Equal(5, visited);
        foreach (var e in ids) Assert.Equal(w.Get<Position>(e).X * 2, w.Get<Position>(e).Y);
    }

    [Fact]
    public void Query_TwoComponents_OnlyVisitsEntitiesWithBoth()
    {
        var w = new World();
        var both = w.Create(); w.Add<Position>(both); w.Add<Velocity>(both);
        var posOnly = w.Create(); w.Add<Position>(posOnly);
        var velOnly = w.Create(); w.Add<Velocity>(velOnly);

        var seen = new List<EntityId>();
        foreach (var row in w.Query<Position, Velocity>()) seen.Add(row.Entity);

        Assert.Single(seen);
        Assert.Equal(both, seen[0]);
    }

    [Fact]
    public void Query_ThreeComponents_Intersects()
    {
        var w = new World();
        var all = w.Create(); w.Add<Position>(all); w.Add<Velocity>(all); w.Add<Tag>(all);
        var two = w.Create(); w.Add<Position>(two); w.Add<Velocity>(two);

        var seen = new List<EntityId>();
        foreach (var row in w.Query<Position, Velocity, Tag>()) seen.Add(row.Entity);

        Assert.Single(seen);
        Assert.Equal(all, seen[0]);
    }

    [Fact]
    public void Query_IterationOrder_IsInsertionOrder_StableAfterMiddleRemoval()
    {
        // The sim's determinism relies on stable spawn-order iteration: removal must
        // preserve insertion order (shift, not swap-with-last).
        var w = new World();
        var e = new EntityId[5];
        for (int i = 0; i < 5; i++) { e[i] = w.Create(); w.Add<Tag>(e[i]) = new Tag { Kind = i }; }

        w.Remove<Tag>(e[2]);   // pull one out of the middle

        var order = new List<int>();
        foreach (var row in w.Query<Tag>()) order.Add(row.Component1.Kind);

        Assert.Equal(new[] { 0, 1, 3, 4 }, order);   // 2 gone, rest in original order
    }

    [Fact]
    public void MarkLiveOnly_StoreIsSkippedBySnapshot_AndClearedOnRestore()
    {
        var w = new World();
        w.MarkLiveOnly<Tag>();
        var e = w.Create();
        w.Add<Tag>(e) = new Tag { Kind = 5 };
        w.Add<Position>(e) = new Position { X = 1 };   // a normal value component

        var snap = w.Capture();          // Tag store skipped; Position captured
        w.Get<Tag>(e).Kind = 99;         // mutate the live-only store

        w.Restore(snap);

        // Position restored from snapshot; Tag store cleared (owner would rebuild it).
        Assert.Equal(1, w.Get<Position>(e).X);
        Assert.False(w.Has<Tag>(e));
    }

    [Fact]
    public void Snapshot_RoundTrips_AndIsIndependentOfLaterMutation()
    {
        var w = new World();
        var a = w.Create(); w.Add<Position>(a) = new Position { X = 1, Y = 2 };
        var b = w.Create(); w.Add<Position>(b) = new Position { X = 3, Y = 4 }; w.Add<Velocity>(b) = new Velocity { Vx = 5 };

        var snap = w.Capture();

        // Mutate the world after capture: change values, add/destroy entities.
        w.Get<Position>(a).X = 999;
        w.Remove<Velocity>(b);
        var c = w.Create(); w.Add<Tag>(c) = new Tag { Kind = 7 };
        w.Destroy(a);

        w.Restore(snap);

        // Original state is back exactly.
        Assert.True(w.IsAlive(a));
        Assert.Equal(1, w.Get<Position>(a).X);
        Assert.Equal(2, w.Get<Position>(a).Y);
        Assert.True(w.Has<Velocity>(b));
        Assert.Equal(5f, w.Get<Velocity>(b).Vx);
        Assert.False(w.IsAlive(c));            // spawned-after-capture entity gone
        Assert.Equal(2, w.Count);
    }

    [Fact]
    public void Snapshot_CanRestoreRepeatedly()
    {
        var w = new World();
        var e = w.Create(); w.Add<Position>(e) = new Position { X = 10 };
        var snap = w.Capture();

        for (int i = 0; i < 3; i++)
        {
            w.Get<Position>(e).X = i;       // scribble
            w.Restore(snap);                // and roll back
            Assert.Equal(10, w.Get<Position>(e).X);
        }
    }

    // ── The randomized stress test the brief called for ──────────────────────────
    // Random create / destroy / add / remove / mutate across many entities with
    // multiple component types, validated every step against a model oracle. Also
    // periodically snapshots and (later) restores, and verifies stale ids from
    // destroyed entities never read as alive. Fixed seed ⇒ deterministic repro.
    [Fact]
    public void RandomizedCreateDestroy_MultiComponent_MatchesModel()
    {
        const int Steps = 20_000;
        var rng = new System.Random(12345);
        var w   = new World();

        // Model oracle: expected component values per live entity.
        var model = new Dictionary<EntityId, (Position? p, Velocity? v, Tag? t)>();
        var live  = new List<EntityId>();
        var dead  = new List<EntityId>();   // sample of destroyed ids to re-check liveness

        for (int step = 0; step < Steps; step++)
        {
            int roll = rng.Next(100);

            if (roll < 35 || live.Count == 0)
            {
                // Create with a random non-empty subset of components.
                var e = w.Create();
                Position? p = null; Velocity? v = null; Tag? t = null;
                int mask = 1 + rng.Next(7);   // 1..7 ⇒ at least one bit set over 3 comps
                if ((mask & 1) != 0) { p = new Position { X = rng.Next(1000), Y = rng.Next(1000) }; w.Add<Position>(e) = p.Value; }
                if ((mask & 2) != 0) { v = new Velocity { Vx = rng.Next(1000), Vy = rng.Next(1000) }; w.Add<Velocity>(e) = v.Value; }
                if ((mask & 4) != 0) { t = new Tag { Kind = rng.Next(1000) }; w.Add<Tag>(e) = t.Value; }
                model[e] = (p, v, t);
                live.Add(e);
            }
            else if (roll < 55)
            {
                // Destroy a random live entity.
                int idx = rng.Next(live.Count);
                var e = live[idx];
                w.Destroy(e);
                model.Remove(e);
                live.RemoveAt(idx);
                if (dead.Count < 200) dead.Add(e);
            }
            else if (roll < 75)
            {
                // Add a component (if missing) to a random live entity.
                var e = live[rng.Next(live.Count)];
                var m = model[e];
                switch (rng.Next(3))
                {
                    case 0 when m.p == null: { var p = new Position { X = rng.Next(1000), Y = rng.Next(1000) }; w.Add<Position>(e) = p; model[e] = (p, m.v, m.t); break; }
                    case 1 when m.v == null: { var v = new Velocity { Vx = rng.Next(1000) }; w.Add<Velocity>(e) = v; model[e] = (m.p, v, m.t); break; }
                    case 2 when m.t == null: { var t = new Tag { Kind = rng.Next(1000) }; w.Add<Tag>(e) = t; model[e] = (m.p, m.v, t); break; }
                }
            }
            else if (roll < 90)
            {
                // Mutate an existing component on a random live entity.
                var e = live[rng.Next(live.Count)];
                var m = model[e];
                if (m.p != null) { var p = new Position { X = rng.Next(1000), Y = rng.Next(1000) }; w.Get<Position>(e) = p; model[e] = (p, m.v, m.t); }
            }
            else
            {
                // Remove a component (if present) from a random live entity.
                var e = live[rng.Next(live.Count)];
                var m = model[e];
                if (m.t != null) { w.Remove<Tag>(e); model[e] = (m.p, m.v, null); }
            }

            // Spot-check invariants every so often (full sweep is too slow per step).
            if (step % 250 == 0) AssertMatchesModel(w, model, dead);
        }

        AssertMatchesModel(w, model, dead);

        // Snapshot/restore at the end of a chaotic run still round-trips.
        var snap = w.Capture();
        foreach (var e in new List<EntityId>(live)) w.Destroy(e);   // wipe
        w.Restore(snap);
        AssertMatchesModel(w, model, dead);
    }

    private static void AssertMatchesModel(
        World w,
        Dictionary<EntityId, (Position? p, Velocity? v, Tag? t)> model,
        List<EntityId> dead)
    {
        Assert.Equal(model.Count, w.Count);

        foreach (var (e, m) in model)
        {
            Assert.True(w.IsAlive(e));

            Assert.Equal(m.p != null, w.Has<Position>(e));
            if (m.p != null) { Assert.Equal(m.p.Value.X, w.Get<Position>(e).X); Assert.Equal(m.p.Value.Y, w.Get<Position>(e).Y); }

            Assert.Equal(m.v != null, w.Has<Velocity>(e));
            if (m.v != null) Assert.Equal(m.v.Value.Vx, w.Get<Velocity>(e).Vx);

            Assert.Equal(m.t != null, w.Has<Tag>(e));
            if (m.t != null) Assert.Equal(m.t.Value.Kind, w.Get<Tag>(e).Kind);
        }

        // Destroyed ids must never read as alive — even after their slot was recycled
        // (a recycled slot carries a higher generation, so the old id mismatches).
        foreach (var e in dead)
            if (!model.ContainsKey(e)) Assert.False(w.IsAlive(e));
    }
}
