using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// Sampling for AnimAdditions, parallel to AnimationSampler for bones. Additions are
// matched across keyframes by Name and lerped; an addition present in only one bracketing
// keyframe holds that value (carry-forward). Pure data — coordinates stay in their authored
// (Parent/root-local) space; the caller transforms to world.
public static class AnimAdditionSampler
{
    // Effective additions at normalized time t (interpolated). Returns a fresh list
    // (clones), or null if the clip has no additions anywhere near t.
    public static List<AnimAddition> Sample(AnimationDocument doc, float t)
    {
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count == 0) return null;
        if (ks.Count == 1 || t <= ks[0].Time) return CloneList(ks[0].Additions);
        if (t >= ks[ks.Count - 1].Time)        return CloneList(ks[ks.Count - 1].Additions);

        int i = 0;
        while (i < ks.Count - 1 && ks[i + 1].Time < t) i++;
        float span = ks[i + 1].Time - ks[i].Time;
        float u = span <= 1e-6f ? 0f : (t - ks[i].Time) / span;
        return Lerp(ks[i].Additions, ks[i + 1].Additions, u);
    }

    // The additions "in effect" at or before t (the latest non-empty set), deep-cloned —
    // used so a newly sampled keyframe carries additions forward (cf. CloneContactsAt).
    public static List<AnimAddition> CloneEffectiveAt(AnimationDocument doc, float t)
    {
        var ks = doc?.Keyframes;
        if (ks == null) return null;
        List<AnimAddition> src = null;
        for (int i = 0; i < ks.Count; i++)
        {
            if (ks[i].Time > t) break;
            if (ks[i].Additions is { Count: > 0 }) src = ks[i].Additions;
        }
        return CloneList(src);
    }

    // C1 sample of a named ROOT-SPACE Point track at normalized time t. Unlike Sample
    // (adjacent-keyframe lerp + hold), this treats the point as a SPARSE channel: its keys
    // are only the keyframes that author it, so an unauthored keyframe between two authored
    // ones is bridged smoothly instead of held-then-snapped, and motion between authored
    // keys uses the same non-uniform Catmull-Rom the pose spline does (tangent = average of
    // adjacent secant slopes; zero tangents at the track's ends — no cyclic wrap: com/edref
    // arcs are one-shots, and a constant track is unaffected). Holds the nearest end value
    // outside the authored range. Allocation-free. False if nothing authors the point.
    public static bool SamplePoint(AnimationDocument doc, float t, string name, out Vector2 p)
    {
        p = default;
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count == 0) return false;

        // Collect the up-to-4 authored keys around t: (tL,pL) (t0,p0) | t | (t1,p1) (tR,pR).
        bool hasL = false, has0 = false, has1 = false, hasR = false;
        float tL = 0f, t0 = 0f, t1 = 0f, tR = 0f;
        Vector2 pL = default, p0 = default, p1 = default, pR = default;
        foreach (var k in ks)
        {
            if (!TryAuthoredPoint(k, name, out var v)) continue;
            if (k.Time <= t)
            {
                hasL = has0; tL = t0; pL = p0;
                has0 = true; t0 = k.Time; p0 = v;
            }
            else if (!has1) { has1 = true; t1 = k.Time; p1 = v; }
            else            { hasR = true; tR = k.Time; pR = v; break; }
        }

        if (!has0 && !has1) return false;
        if (!has1) { p = p0; return true; }   // past the last authored key → hold
        if (!has0) { p = p1; return true; }   // before the first authored key → hold

        float h = t1 - t0;
        if (h <= 1e-6f) { p = p1; return true; }
        float u = MathHelper.Clamp((t - t0) / h, 0f, 1f);
        Vector2 sC = (p1 - p0) / h;
        Vector2 m0 = hasL && t0 - tL > 1e-6f ? 0.5f * ((p0 - pL) / (t0 - tL) + sC) : Vector2.Zero;
        Vector2 m1 = hasR && tR - t1 > 1e-6f ? 0.5f * (sC + (pR - p1) / (tR - t1)) : Vector2.Zero;
        float u2 = u * u, u3 = u2 * u;
        float h00 = 2f * u3 - 3f * u2 + 1f, h10 = u3 - 2f * u2 + u;
        float h01 = -2f * u3 + 3f * u2,     h11 = u3 - u2;
        p = h00 * p0 + h * h10 * m0 + h01 * p1 + h * h11 * m1;
        return true;
    }

    private static bool TryAuthoredPoint(AnimationKeyframe k, string name, out Vector2 p)
    {
        p = default;
        if (k.Additions == null) return false;
        foreach (var a in k.Additions)
            if (a.Kind == AnimAdditionKind.Point && a.Name == name && a.Parent == null)
            { p = new Vector2(a.Px, a.Py); return true; }
        return false;
    }

    private static List<AnimAddition> Lerp(List<AnimAddition> a, List<AnimAddition> b, float u)
    {
        if ((a == null || a.Count == 0) && (b == null || b.Count == 0)) return null;
        var result = new List<AnimAddition>();
        var bByName = new Dictionary<string, AnimAddition>();
        if (b != null) foreach (var e in b) if (e.Name != null) bByName[e.Name] = e;

        var used = new HashSet<string>();
        if (a != null)
            foreach (var ea in a)
            {
                if (ea.Name == null) continue;
                used.Add(ea.Name);
                if (bByName.TryGetValue(ea.Name, out var eb))
                    result.Add(new AnimAddition
                    {
                        Name = ea.Name, Kind = ea.Kind, Parent = ea.Parent,
                        Px = MathHelper.Lerp(ea.Px, eb.Px, u),
                        Py = MathHelper.Lerp(ea.Py, eb.Py, u),
                        Dx = MathHelper.Lerp(ea.Dx, eb.Dx, u),
                        Dy = MathHelper.Lerp(ea.Dy, eb.Dy, u),
                    });
                else
                    result.Add(ea.Clone());      // present only in the earlier frame → hold
            }
        if (b != null)
            foreach (var eb in b)
                if (eb.Name != null && !used.Contains(eb.Name))
                    result.Add(eb.Clone());      // appears in the later frame → snap in

        return result.Count == 0 ? null : result;
    }

    private static List<AnimAddition> CloneList(List<AnimAddition> src)
    {
        if (src == null || src.Count == 0) return null;
        var copy = new List<AnimAddition>(src.Count);
        foreach (var e in src) copy.Add(e.Clone());
        return copy;
    }
}
