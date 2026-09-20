using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// CLIP-LOCAL REFERENCE ARCS (AnimationDocument.Arcs). A reference arc normally lives in
// ReferenceClips/<name>.json and is shared by everything that names it. An arc edited from
// inside the CLIP editor is forked into the clip instead, so shaping a maneuver's curve while
// authoring one clip can never move another clip that happened to name the same arc.
//
// The rule the editor enforces: editing from a clip always edits local (Fork on open), and the
// standalone `--ref <name>` editor is the only thing that writes the shared file. Resolution is
// explicit rather than by shadowing — a Scene overlay carries `Local`, so a clip can display
// the shared arc and its own fork at the same time and tell them apart.
//
// Arcs are authored in GAME PIXELS against their own entry/gate anchors, clip-local ones
// included: the same ClipMotion.ArcOffset mapping takes them to scene units, so a fork lands
// exactly where the shared arc did until it is edited.
public static class ClipArcs
{
    public static HermiteClipDocument FindLocal(AnimationDocument clip, string name)
    {
        if (clip?.Arcs == null || string.IsNullOrWhiteSpace(name)) return null;
        foreach (var a in clip.Arcs)
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    public static bool HasLocal(AnimationDocument clip, string name) => FindLocal(clip, name) != null;

    // A name no local arc of this clip is using. Locals may freely share a name with a SHARED
    // arc — the overlay's Local flag says which pool to look in — so only the clip's own list
    // is consulted here.
    public static string UniqueName(AnimationDocument clip, string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) stem = "arc";
        if (!HasLocal(clip, stem)) return stem;
        for (int n = 2; ; n++)
            if (!HasLocal(clip, $"{stem}{n}")) return $"{stem}{n}";
    }

    public static HermiteClipDocument Add(AnimationDocument clip, HermiteClipDocument arc)
    {
        if (clip == null || arc == null) return null;
        clip.Arcs ??= new List<HermiteClipDocument>();
        arc.Name = UniqueName(clip, arc.Name);
        clip.Arcs.Add(arc);
        return arc;
    }

    public static bool Remove(AnimationDocument clip, HermiteClipDocument arc)
    {
        if (clip?.Arcs == null || arc == null || !clip.Arcs.Remove(arc)) return false;
        if (clip.Arcs.Count == 0) clip.Arcs = null;   // absent, not an empty list
        return true;
    }

    // Fork a SHARED arc into the clip: a deep copy that records where it came from. The copy
    // keeps the shared arc's name when the clip has no local arc by that name, so an overlay
    // flipped to Local keeps reading the same way.
    public static HermiteClipDocument Fork(AnimationDocument clip, HermiteClipDocument shared)
    {
        if (clip == null || shared == null) return null;
        var copy = shared.Clone();
        copy.FromShared = shared.Name;
        return Add(clip, copy);
    }

    // A new clip-local arc. `seedFromPath` traces the clip's own body_path — the quickest way
    // to turn an authored path into an editable curve (px = rig · SkeletonScale, since arcs are
    // authored in game pixels); otherwise it is the default two-key arc.
    public static HermiteClipDocument NewLocal(AnimationDocument clip, string name, bool seedFromPath)
    {
        if (clip == null) return null;
        var arc = HermiteClipDocument.NewDefault(UniqueName(clip, name));
        arc.Duration = clip.Duration;
        if (seedFromPath && TraceBodyPath(clip, arc)) { /* seeded */ }
        return Add(clip, arc);
    }

    // Keys at every keyframe that authors a body_path point, in game pixels, with
    // Catmull-Rom style tangents and the anchors on the first/last point. False when the clip
    // has no path to trace (fewer than two authored points).
    private static bool TraceBodyPath(AnimationDocument clip, HermiteClipDocument arc)
    {
        var pts = new List<Vector2>();
        if (clip.Keyframes != null)
            foreach (var kf in clip.Keyframes)
                if (BodyPath.TrySample(clip, kf.Time, out var p)) pts.Add(p * Game1.SkeletonScale);
        if (pts.Count < 2) return false;

        arc.Keys.Clear();
        for (int i = 0; i < pts.Count; i++)
        {
            Vector2 prev = pts[Math.Max(0, i - 1)], next = pts[Math.Min(pts.Count - 1, i + 1)];
            Vector2 tan = (next - prev) * 0.5f;
            if (tan.LengthSquared() < 1e-6f) tan = new Vector2(1f, 0f);
            arc.Keys.Add(new HermiteClipKey { X = pts[i].X, Y = pts[i].Y, TX = tan.X, TY = tan.Y });
        }
        arc.RederiveT();
        arc.Entry = pts[0];
        arc.Gate  = pts[^1];
        return true;
    }
}
