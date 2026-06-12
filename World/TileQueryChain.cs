using System;
using System.Collections.Generic;

namespace MTile;

// Fluent query layer for tiles / edges / corners. Each chain holds the ChunkMap
// it was opened against plus a lazily-evaluated IEnumerable seed; Where wraps the
// seed in a predicate filter (passing chunks through), and the reductions
// (MaxBy / MinBy / FirstOrDefault / Any) consume it.
//
// Returns nullable result types (TileRef? etc.) so callers can distinguish "no
// match" from a default-valued match — `default(TileRef)` is a valid origin tile
// and would shadow a real result if reductions used the LINQ-style throw-or-default.
//
// foreach is supported via GetEnumerator forwarding. Enumeration order matches
// TileQuery.SolidTilesInRect (chunk-major, tx-major within chunk) so anything
// downstream that already depends on that order (snapshot determinism, the
// deterministic sim step) still observes the same sequence.

public readonly struct TileQueryChain
{
    public readonly ChunkMap Chunks;
    private readonly IEnumerable<TileRef> _source;

    public TileQueryChain(ChunkMap chunks, IEnumerable<TileRef> source)
    {
        Chunks = chunks;
        _source = source;
    }

    public TileQueryChain Where(TilePredicate pred)
        => new(Chunks, Filter(Chunks, _source, pred));

    private static IEnumerable<TileRef> Filter(ChunkMap chunks, IEnumerable<TileRef> source, TilePredicate pred)
    {
        foreach (var t in source) if (pred(chunks, t)) yield return t;
    }

    public IEnumerator<TileRef> GetEnumerator() => _source.GetEnumerator();

    public TileRef? MaxBy(Func<TileRef, float> selector)
    {
        TileRef best = default; float bestKey = float.NegativeInfinity; bool has = false;
        foreach (var t in _source)
        {
            float k = selector(t);
            if (!has || k > bestKey) { best = t; bestKey = k; has = true; }
        }
        return has ? best : null;
    }

    public TileRef? MinBy(Func<TileRef, float> selector)
    {
        TileRef best = default; float bestKey = float.PositiveInfinity; bool has = false;
        foreach (var t in _source)
        {
            float k = selector(t);
            if (!has || k < bestKey) { best = t; bestKey = k; has = true; }
        }
        return has ? best : null;
    }

    public TileRef? FirstOrDefault()
    {
        foreach (var t in _source) return t;
        return null;
    }

    public bool Any()
    {
        foreach (var _ in _source) return true;
        return false;
    }
}

public readonly struct EdgeQueryChain
{
    public readonly ChunkMap Chunks;
    private readonly IEnumerable<EdgeRef> _source;

    public EdgeQueryChain(ChunkMap chunks, IEnumerable<EdgeRef> source)
    {
        Chunks = chunks;
        _source = source;
    }

    public EdgeQueryChain Where(EdgePredicate pred)
        => new(Chunks, Filter(Chunks, _source, pred));

    private static IEnumerable<EdgeRef> Filter(ChunkMap chunks, IEnumerable<EdgeRef> source, EdgePredicate pred)
    {
        foreach (var e in source) if (pred(chunks, e)) yield return e;
    }

    public IEnumerator<EdgeRef> GetEnumerator() => _source.GetEnumerator();

    public EdgeRef? MaxBy(Func<EdgeRef, float> selector)
    {
        EdgeRef best = default; float bestKey = float.NegativeInfinity; bool has = false;
        foreach (var e in _source)
        {
            float k = selector(e);
            if (!has || k > bestKey) { best = e; bestKey = k; has = true; }
        }
        return has ? best : null;
    }

    public EdgeRef? MinBy(Func<EdgeRef, float> selector)
    {
        EdgeRef best = default; float bestKey = float.PositiveInfinity; bool has = false;
        foreach (var e in _source)
        {
            float k = selector(e);
            if (!has || k < bestKey) { best = e; bestKey = k; has = true; }
        }
        return has ? best : null;
    }

    public EdgeRef? FirstOrDefault()
    {
        foreach (var e in _source) return e;
        return null;
    }

    public bool Any()
    {
        foreach (var _ in _source) return true;
        return false;
    }
}

public readonly struct CornerQueryChain
{
    public readonly ChunkMap Chunks;
    private readonly IEnumerable<CornerRef> _source;

    public CornerQueryChain(ChunkMap chunks, IEnumerable<CornerRef> source)
    {
        Chunks = chunks;
        _source = source;
    }

    public CornerQueryChain Where(CornerPredicate pred)
        => new(Chunks, Filter(Chunks, _source, pred));

    private static IEnumerable<CornerRef> Filter(ChunkMap chunks, IEnumerable<CornerRef> source, CornerPredicate pred)
    {
        foreach (var c in source) if (pred(chunks, c)) yield return c;
    }

    public IEnumerator<CornerRef> GetEnumerator() => _source.GetEnumerator();

    public CornerRef? MaxBy(Func<CornerRef, float> selector)
    {
        CornerRef best = default; float bestKey = float.NegativeInfinity; bool has = false;
        foreach (var c in _source)
        {
            float k = selector(c);
            if (!has || k > bestKey) { best = c; bestKey = k; has = true; }
        }
        return has ? best : null;
    }

    public CornerRef? MinBy(Func<CornerRef, float> selector)
    {
        CornerRef best = default; float bestKey = float.PositiveInfinity; bool has = false;
        foreach (var c in _source)
        {
            float k = selector(c);
            if (!has || k < bestKey) { best = c; bestKey = k; has = true; }
        }
        return has ? best : null;
    }

    public CornerRef? FirstOrDefault()
    {
        foreach (var c in _source) return c;
        return null;
    }

    public bool Any()
    {
        foreach (var _ in _source) return true;
        return false;
    }
}
