using System;
using Microsoft.Xna.Framework;

namespace MTile;

// A fading ribbon built from recent positions. Push() one sample per frame from
// wherever you want the trail's tip to be; Tick(dt) ages all samples and drops
// any past Lifetime; Draw()/Emit() connect consecutive samples with line segments
// whose color and width lerp from "newest" toward "oldest."
//
// Backing storage is a fixed-size ring buffer — no per-frame allocation. Owned
// by whoever wants the trail (an action, the cursor, a projectile); independent
// of the global ParticleSystem so it can run in either the world-space or
// screen-space SpriteBatch pass.
public sealed class Trail
{
    private readonly Vector2[] _pos;
    private readonly float[]   _age;
    private int _head;     // index where the NEXT Push will write
    private int _count;

    public int   Capacity { get; }
    public float Lifetime { get; }
    public int   Count    => _count;

    public Trail(int capacity, float lifetime)
    {
        if (capacity < 2)   capacity = 2;
        if (lifetime <= 0f) lifetime = 0.01f;
        Capacity = capacity;
        Lifetime = lifetime;
        _pos = new Vector2[capacity];
        _age = new float[capacity];
    }

    public void Push(Vector2 p)
    {
        _pos[_head] = p;
        _age[_head] = 0f;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    public void Tick(float dt)
    {
        for (int i = 0; i < _count; i++) _age[i] += dt;
        // Drop samples that have aged past Lifetime. Because Push writes in order,
        // the oldest live sample sits at `(_head - _count) mod Capacity`.
        while (_count > 0)
        {
            int oldest = (_head - _count + Capacity) % Capacity;
            if (_age[oldest] >= Lifetime) _count--;
            else break;
        }
    }

    public void Clear() => _count = 0;

    // Sample access, newest (i=0) → oldest (i=Count-1). For consumers that render the
    // trail themselves (e.g. the glow renderer) instead of via Draw.
    public Vector2 PositionFromNewest(int i) => _pos[(_head - 1 - i + Capacity) % Capacity];
    public float   AgeFromNewest(int i)      => _age[(_head - 1 - i + Capacity) % Capacity];
    public float   AgeFractionFromNewest(int i)
        => MathHelper.Clamp(AgeFromNewest(i) / Lifetime, 0f, 1f);

    // Connects consecutive samples newest → oldest with a Catmull-Rom spline,
    // emitted as `Subdivisions` short line segments per source-segment. The
    // spline passes through every sample, so the trail shape matches the raw
    // Push() positions but the kinks between them are smoothed out.
    //
    // Width tapers monotonically from `startWidth` at the head to zero, as a
    // linear function of chord arc length (sum of L2 distances between source
    // samples). The default fade length is `FadeRate * startWidth` world-pixels
    // — long trails hit zero before the buffer's tail and the remaining samples
    // are skipped, so the trail fades to a point instead of cutting off. Short
    // trails (totalLen < defaultLen) renormalize the slope so the tail still
    // reaches zero at the very last sample instead of leaving a stubby nub.
    // Color still lerps over age so freshly-pushed samples read bright.
    private const int   Subdivisions = 10;
    private const float FadeRate     = 30f;

    // Immediate draw through DrawContext (cursor trail, tool viewers).
    public void Draw(DrawContext ctx, Color startColor, Color endColor, float startWidth)
    {
        var sink = new ContextSink(ctx);
        Walk(ref sink, startColor, endColor, startWidth);
    }

    // Deferred: append the ribbon's segments to a TelegraphList. This is how a
    // sim-side owner (an action's beam motes) shows its trail without touching a
    // SpriteBatch — Drawing/TelegraphRenderer draws the list later.
    public void Emit(TelegraphList list, Color startColor, Color endColor, float startWidth)
    {
        var sink = new ListSink(list);
        Walk(ref sink, startColor, endColor, startWidth);
    }

    // One segment consumer per output path. Structs + generic constraint so the
    // walker below is shared without a delegate allocation per segment.
    private interface ISegmentSink { void Segment(Vector2 a, Vector2 b, Color color, float width); }

    private readonly struct ContextSink : ISegmentSink
    {
        private readonly DrawContext _ctx;
        public ContextSink(DrawContext ctx) => _ctx = ctx;
        public void Segment(Vector2 a, Vector2 b, Color color, float width) => _ctx.Line(a, b, color, width);
    }

    private readonly struct ListSink : ISegmentSink
    {
        private readonly TelegraphList _list;
        public ListSink(TelegraphList list) => _list = list;
        public void Segment(Vector2 a, Vector2 b, Color color, float width) => _list.Line(a, b, color, width);
    }

    private void Walk<TSink>(ref TSink sink, Color startColor, Color endColor, float startWidth)
        where TSink : struct, ISegmentSink
    {
        if (_count < 2) return;
        int last = _count - 1;

        // First pass — sum chord distances between consecutive samples to
        // get total trail length. Straight chords, not the rendered spline
        // arc length; the two are close enough for picking a taper slope.
        float totalLen = 0f;
        for (int i = 0; i < last; i++)
        {
            int newer = (_head - 1 - i + Capacity) % Capacity;
            int older = (_head - 2 - i + Capacity) % Capacity;
            totalLen += Vector2.Distance(_pos[newer], _pos[older]);
        }
        if (totalLen < 1e-4f) return;

        float defaultLen = startWidth * FadeRate;
        float slope      = startWidth / MathF.Min(defaultLen, totalLen);

        // Second pass — emit newer → older, accumulating arc by source
        // segment chord. When the linear width hits zero we stop entirely.
        float arc = 0f;
        for (int i = 0; i < last; i++)
        {
            int newer = (_head - 1 - i + Capacity) % Capacity;
            int older = (_head - 2 - i + Capacity) % Capacity;

            Vector2 p1 = _pos[newer];
            Vector2 p2 = _pos[older];
            // Catmull-Rom neighbors. At the ribbon's ends we clamp to the
            // endpoint itself so the curve still passes through p1 / p2 with
            // a zero-tangent boundary instead of spiraling off.
            Vector2 p0 = (i > 0)        ? _pos[(_head     - i + Capacity) % Capacity] : p1;
            Vector2 p3 = (i < last - 1) ? _pos[(_head - 3 - i + Capacity) % Capacity] : p2;

            float arcStart = arc;
            float arcEnd   = arc + Vector2.Distance(p1, p2);
            arc = arcEnd;

            float uNewer = MathHelper.Clamp(_age[newer] / Lifetime, 0f, 1f);
            float uOlder = MathHelper.Clamp(_age[older] / Lifetime, 0f, 1f);

            Vector2 prev = p1;
            for (int s = 1; s <= Subdivisions; s++)
            {
                float t      = (float)s / Subdivisions;
                float segArc = MathHelper.Lerp(arcStart, arcEnd, t);
                float w      = startWidth - slope * segArc;
                if (w <= 0f) return;

                Vector2 cur = CatmullRom(p0, p1, p2, p3, t);
                float u   = MathHelper.Lerp(uNewer, uOlder, t);
                var col   = Color.Lerp(startColor, endColor, u);
                sink.Segment(prev, cur, col, w);
                prev = cur;
            }
        }
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1)
            + (-p0 + p2) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
}
