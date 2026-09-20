using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MTile;

// A SCALAR CURVE over a normalized domain, the value type for annotation data that varies
// across an annotation's lifetime — contact weight first (Plans/ANIMATION_SCENE_AUTHORING_PLAN.md;
// spans replace the per-keyframe contact convention), later anything else an annotation
// wants to shape rather than hold constant.
//
// THE DOMAIN IS THE SPAN, NOT THE CLIP. Keys live on u ∈ [0,1]; a consumer maps its own
// [start, end] onto that (ValueOverSpan). So retiming a span STRETCHES the curve and its
// shape survives: drag a stance twice as long and its ramp-in takes twice as long, which is
// the behavior a foot plant wants — a slower step transfers weight more slowly. The cost is
// that a curve cannot express "40ms of ramp regardless of span"; nothing needs that yet, and
// the serialized shape leaves room to add a per-curve flag for it without a migration.
//
// The domain is CLOSED. u = 1 evaluates the last key, it does not wrap to the first — the
// span owns wrapping (a stance can cross a loop seam), the curve never does. Sampling that
// silently wrapped its endpoint has bitten this codebase before (AnimSceneTests pins
// BodyAt(1f) as the endpoint for the same reason).
//
// A NULL CURVE MEANS CONSTANT. Most annotations want one flat value, so every accessor is a
// static that takes a possibly-null curve and a fallback; nothing has to null-check at the
// call site, and AnimationStore's WhenWritingNull keeps a flat annotation's json clean.
public sealed class AnimCurveKey
{
    public float T { get; set; }              // position on the normalized domain, [0,1]
    public float V { get; set; }              // value here

    // dV/dT at this key. Null = derived from the neighbours (Catmull-Rom), which is what
    // makes a curve authorable by dropping points alone. Set it to break the tangent when a
    // specific ease matters — the same auto-then-override split HermiteClip's arcs use.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Tan { get; set; }

    public AnimCurveKey Clone() => new() { T = T, V = V, Tan = Tan };
}

public sealed class AnimCurve
{
    public List<AnimCurveKey> Keys { get; set; } = new();

    public AnimCurve Clone()
    {
        var c = new AnimCurve();
        foreach (var k in Keys) c.Keys.Add(k.Clone());
        return c;
    }

    // The default shape for a freshly-authored span: ease up over the first `rampIn` of the
    // span, hold, ease back down over the last `rampOut`. Zero at both ends, so a span's
    // value blends in and out instead of stepping — the behavior the old piecewise-linear
    // feather approximated with a corner at each end.
    public static AnimCurve Ramp(float peak = 1f, float rampIn = 0.15f, float rampOut = 0.15f)
    {
        rampIn  = Math.Clamp(rampIn,  0f, 0.5f);
        rampOut = Math.Clamp(rampOut, 0f, 0.5f);
        var c = new AnimCurve();
        c.Keys.Add(new AnimCurveKey { T = 0f, V = 0f, Tan = 0f });
        if (rampIn  > 1e-4f) c.Keys.Add(new AnimCurveKey { T = rampIn,      V = peak, Tan = 0f });
        if (rampOut > 1e-4f) c.Keys.Add(new AnimCurveKey { T = 1f - rampOut, V = peak, Tan = 0f });
        c.Keys.Add(new AnimCurveKey { T = 1f, V = 0f, Tan = 0f });
        return c;
    }

    public static AnimCurve Constant(float v)
    {
        var c = new AnimCurve();
        c.Keys.Add(new AnimCurveKey { T = 0f, V = v, Tan = 0f });
        return c;
    }

    // Insert a key at `u` WITHOUT moving the drawn line — "add a handle here to bend later".
    //
    // Taking the curve's current value there is not enough on its own: an auto tangent is a
    // secant through a key's NEIGHBOURS, so a new key silently re-derives the tangents either
    // side of it and the shape shifts. So the neighbours' effective tangents are frozen as
    // authored values first, and the new key takes the curve's actual slope.
    public int InsertPreservingShape(float u)
    {
        u = Math.Clamp(u, 1e-3f, 1f - 1e-3f);
        if (Keys.Count == 0) { Keys.Add(new AnimCurveKey { T = u, V = 0f, Tan = 0f }); return 0; }

        float v = ValueAt(this, u), slope = SlopeAt(this, u);
        int at = Keys.Count;
        for (int i = 0; i < Keys.Count; i++) if (Keys[i].T > u) { at = i; break; }
        if (at > 0)              Keys[at - 1].Tan ??= TangentAt(Keys, at - 1);
        if (at < Keys.Count)     Keys[at].Tan     ??= TangentAt(Keys, at);
        Keys.Insert(at, new AnimCurveKey { T = u, V = v, Tan = slope });
        return at;
    }

    // ── evaluation ──────────────────────────────────────────────────────────────────
    // `curve` may be null (constant `fallback`) and may hold a single key (constant that
    // key's value) — both are ordinary states, not errors, so no consumer needs a guard.

    public static float ValueAt(AnimCurve curve, float u, float fallback = 1f)
    {
        var ks = curve?.Keys;
        if (ks == null || ks.Count == 0) return fallback;
        if (ks.Count == 1) return ks[0].V;
        if (u <= ks[0].T) return ks[0].V;
        if (u >= ks[^1].T) return ks[^1].V;

        int i = Segment(ks, u);
        float h = ks[i + 1].T - ks[i].T;
        if (h <= 1e-9f) return ks[i + 1].V;
        float s = (u - ks[i].T) / h, ss = s * s, sss = ss * s;
        float m0 = TangentAt(ks, i), m1 = TangentAt(ks, i + 1);
        return (2f * sss - 3f * ss + 1f) * ks[i].V
             + (sss - 2f * ss + s) * h * m0
             + (-2f * sss + 3f * ss) * ks[i + 1].V
             + (sss - ss) * h * m1;
    }

    // dV/du on the normalized domain. Flat outside the key range — a consumer that needs the
    // derivative with respect to CLIP PHASE wants SlopeOverSpan, which applies the chain rule.
    public static float SlopeAt(AnimCurve curve, float u)
    {
        var ks = curve?.Keys;
        if (ks == null || ks.Count < 2) return 0f;
        if (u <= ks[0].T || u >= ks[^1].T) return 0f;

        int i = Segment(ks, u);
        float h = ks[i + 1].T - ks[i].T;
        if (h <= 1e-9f) return 0f;
        float s = (u - ks[i].T) / h, ss = s * s;
        float m0 = TangentAt(ks, i), m1 = TangentAt(ks, i + 1);
        return ((6f * ss - 6f * s) * ks[i].V
              + (3f * ss - 4f * s + 1f) * h * m0
              + (-6f * ss + 6f * s) * ks[i + 1].V
              + (3f * ss - 2f * s) * h * m1) / h;
    }

    // Value at clip phase `t` for an annotation spanning [start, end]. Outside the span the
    // annotation is not active, so this reports the nearest endpoint's value rather than
    // inventing one; callers gate on the span themselves.
    public static float ValueOverSpan(AnimCurve curve, float t, float start, float end, float fallback = 1f)
        => ValueAt(curve, Normalize(t, start, end), fallback);

    // dV/dPHASE — the quantity a solver differentiating through the annotation needs. One
    // chain-rule factor over the normalized slope, and ZERO outside the span. The curve is C1
    // inside, so unlike the piecewise-linear feather this replaces there is no interior kink
    // an FD-vs-analytic check has to skip; only the span's own two edges remain.
    public static float SlopeOverSpan(AnimCurve curve, float t, float start, float end)
    {
        float w = end - start;
        if (w <= 1e-9f || t <= start || t >= end) return 0f;
        return SlopeAt(curve, (t - start) / w) / w;
    }

    private static float Normalize(float t, float start, float end)
    {
        float w = end - start;
        return w <= 1e-9f ? 0f : Math.Clamp((t - start) / w, 0f, 1f);
    }

    // Last key at or before `u`, given ks.Count >= 2 and ks[0].T < u < ks[^1].T. Linear scan:
    // a curve is a handful of keys, and a binary search would cost more than it saves.
    private static int Segment(List<AnimCurveKey> ks, float u)
    {
        for (int i = ks.Count - 2; i >= 0; i--) if (u >= ks[i].T) return i;
        return 0;
    }

    // The authored tangent, else Catmull-Rom from the neighbours (one-sided at the ends).
    // Secants, not slopes-per-index: uneven key spacing is normal, so the finite difference
    // has to divide by the actual parameter distance or a tight pair whips the curve.
    private static float TangentAt(List<AnimCurveKey> ks, int i)
    {
        if (ks[i].Tan.HasValue) return ks[i].Tan.Value;
        int a = Math.Max(0, i - 1), b = Math.Min(ks.Count - 1, i + 1);
        float dt = ks[b].T - ks[a].T;
        return dt <= 1e-9f ? 0f : (ks[b].V - ks[a].V) / dt;
    }
}
