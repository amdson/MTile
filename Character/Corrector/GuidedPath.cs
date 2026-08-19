using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Piecewise cubic Hermite path. The path is one or more `Segment`s joined
// end-to-end; the planner is responsible for C¹ continuity (segment[i].V1 ==
// segment[i+1].V0). Global t ∈ [0, 1] is divided across segments by their
// durations — a long segment occupies more of the t-range than a short one.
//
// Each segment uses world-space velocities for V0 and V1, scaled internally by
// the segment's Duration so SampleVelocity returns world-space velocity.
//
//   P(t)  = h00·P0 + h10·V0·Dur + h01·P1 + h11·V1·Dur
//   P'(t) = (dh00·P0 + dh10·V0·Dur + dh01·P1 + dh11·V1·Dur) / Dur
//
// Multi-segment lets the planner route the body around obstacles (e.g. the
// vault path goes start → above-corner → goal). PD control in GuidedState is
// unchanged: it still queries Sample / SampleVelocity by global t.
public sealed class GuidedPath
{
    public readonly struct Segment
    {
        public readonly Vector2 P0, V0, P1, V1;
        public readonly float   Duration;

        public Segment(Vector2 p0, Vector2 v0, Vector2 p1, Vector2 v1, float duration)
        {
            P0 = p0; V0 = v0; P1 = p1; V1 = v1;
            Duration = MathF.Max(duration, 0.0001f);
        }

        public Vector2 Sample(float localT)
        {
            localT = MathHelper.Clamp(localT, 0f, 1f);
            float t2 = localT * localT;
            float t3 = t2 * localT;
            float h00 =  2f * t3 - 3f * t2 + 1f;
            float h10 =       t3 - 2f * t2 + localT;
            float h01 = -2f * t3 + 3f * t2;
            float h11 =       t3 -      t2;
            return h00 * P0 + h10 * (V0 * Duration) + h01 * P1 + h11 * (V1 * Duration);
        }

        public Vector2 SampleVelocity(float localT)
        {
            localT = MathHelper.Clamp(localT, 0f, 1f);
            float t2 = localT * localT;
            float dh00 =  6f * t2 - 6f * localT;
            float dh10 =  3f * t2 - 4f * localT + 1f;
            float dh01 = -6f * t2 + 6f * localT;
            float dh11 =  3f * t2 - 2f * localT;
            Vector2 dPdt = dh00 * P0 + dh10 * (V0 * Duration) + dh01 * P1 + dh11 * (V1 * Duration);
            return dPdt / Duration;
        }
    }

    private readonly Segment[] _segments;
    // Global-t boundary at the START of each segment. _segStartT[0] is always 0.
    // _segStartT[i+1] - _segStartT[i] = segment[i].Duration / TotalDuration.
    private readonly float[]   _segStartT;
    public  readonly float     TotalDuration;

    // Overall start/end pose, exposed for callers that snap to or query the
    // path's natural endpoint velocities (e.g. final goalVel for state hand-off).
    public Vector2 P0 => _segments[0].P0;
    public Vector2 V0 => _segments[0].V0;
    public Vector2 P1 => _segments[^1].P1;
    public Vector2 V1 => _segments[^1].V1;

    private float _lastT;

    public GuidedPath(IReadOnlyList<Segment> segments)
    {
        if (segments == null || segments.Count == 0)
            throw new ArgumentException("GuidedPath requires at least one segment.");

        _segments  = new Segment[segments.Count];
        _segStartT = new float[segments.Count];

        float total = 0f;
        for (int i = 0; i < segments.Count; i++)
        {
            _segments[i] = segments[i];
            total += segments[i].Duration;
        }
        TotalDuration = total;

        float cum = 0f;
        for (int i = 0; i < segments.Count; i++)
        {
            _segStartT[i] = cum / total;
            cum += segments[i].Duration;
        }

        _lastT = 0f;
    }

    // Convenience: single-segment path.
    public static GuidedPath Plan(Vector2 startPos, Vector2 startVel, Vector2 goalPos, Vector2 goalVel, float duration)
        => new GuidedPath(new[] { new Segment(startPos, startVel, goalPos, goalVel, duration) });

    public bool IsComplete(float t) => t >= 1f;

    public Vector2 Sample(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        ResolveSegment(t, out int idx, out float localT);
        return _segments[idx].Sample(localT);
    }

    public Vector2 SampleVelocity(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        ResolveSegment(t, out int idx, out float localT);
        return _segments[idx].SampleVelocity(localT);
    }

    private void ResolveSegment(float t, out int idx, out float localT)
    {
        // Last segment if t == 1, else the segment whose [start, next-start) range contains t.
        idx = _segments.Length - 1;
        for (int i = 0; i < _segments.Length - 1; i++)
        {
            if (t < _segStartT[i + 1]) { idx = i; break; }
        }
        float segStart = _segStartT[idx];
        float segEnd   = (idx + 1 < _segments.Length) ? _segStartT[idx + 1] : 1f;
        float segLen   = segEnd - segStart;
        localT = segLen > 1e-6f ? (t - segStart) / segLen : 0f;
    }

    public float ProjectOnto(Vector2 pos)
    {
        const int Samples = 24;
        float windowStart = MathF.Max(0f, _lastT - 0.05f);
        float windowEnd   = MathF.Min(1f, _lastT + 0.4f);
        if (windowEnd <= windowStart) windowEnd = MathHelper.Clamp(windowStart + 0.01f, 0f, 1f);

        float bestT = _lastT;
        float bestDistSq = (Sample(_lastT) - pos).LengthSquared();
        for (int i = 0; i <= Samples; i++)
        {
            float t = windowStart + (windowEnd - windowStart) * i / Samples;
            float d = (Sample(t) - pos).LengthSquared();
            if (d < bestDistSq) { bestDistSq = d; bestT = t; }
        }

        // Monotone: never go backwards
        if (bestT < _lastT) bestT = _lastT;
        _lastT = bestT;
        return bestT;
    }
}
