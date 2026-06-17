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
