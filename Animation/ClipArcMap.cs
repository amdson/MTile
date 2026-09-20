using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// MAP COM TO ARC — write a clip's body path from a ReferenceClips arc: at every keyframe, the
// com anchor's scene position becomes the arc's position at the matching point along it.
//
// This replaced "riding" an arc. A clip used to be able to name a ReferenceArc and have its
// placement resolved from that file every time it was sampled, which meant a clip's path lived
// in another document, one clip could be placed three different ways depending on precedence,
// and nothing at runtime ever read any of it. Now there is exactly one placement channel — the
// clip's own `body_path` track (BodyPath) — and an arc is a REFERENCE you can map onto it on
// demand. The clip ends up self-contained; the arc stays an overlay you can keep looking at.
//
// The sparse C1 track only carries the arc's shape as well as the keyframes sample it, so the
// result reports the worst gap between the two (add a keyframe where it matters).
public static class ClipArcMap
{
    public sealed class MapResult
    {
        public int     Keys;                // body_path points written (one per keyframe)
        public int     Overwritten;         // of those, keys that already carried a point
        public float   WorstError;          // largest gap, rig units, between the track and the arc
        public float   WorstAt;             // clip phase where that gap occurs
        public Vector2 CycleDisplacement;   // p(1) − p(0) after the map — what paces the clip
    }

    // Clip phase → position along the arc.
    //   stretch = false: the AUTHORED pacing — clip seconds over arc seconds, capped at 2×
    //     (ClipMotion.ArcProgress). This is the mapping a clip riding the arc used, so mapping
    //     such a clip reproduces exactly where it already sat.
    //   stretch = true: fit the arc's whole parameter across the clip, so t = 1 lands on the
    //     arc's end whatever the two durations say.
    public static float ArcParam(AnimationDocument clip, HermiteClipDocument arc, float t, bool stretch)
        => stretch ? MathHelper.Clamp(t, 0f, 1f)
                   : ClipMotion.ArcProgress(t, clip?.Duration ?? 1f, arc?.Duration ?? 1f);

    public static bool TryMap(AnimationDocument clip, HermiteClipDocument arc, bool stretch,
                              out MapResult result, out string error)
    {
        result = null;
        error = null;
        if (clip == null) { error = "no clip"; return false; }
        if (arc == null) { error = "no arc"; return false; }
        if (clip.Keyframes == null || clip.Keyframes.Count == 0) { error = $"{clip.Name}: no keyframes to write"; return false; }

        var r = new MapResult();
        foreach (var kf in clip.Keyframes)
        {
            Vector2 p = ClipMotion.ArcOffset(arc, ArcParam(clip, arc, kf.Time, stretch));
            kf.Additions ??= new List<AnimAddition>();
            var add = kf.Additions.Find(x => x.Kind == AnimAdditionKind.Point
                                          && x.Name == BodyPath.ChannelName && x.Parent == null);
            if (add == null) kf.Additions.Add(add = new AnimAddition { Name = BodyPath.ChannelName, Kind = AnimAdditionKind.Point });
            else r.Overwritten++;
            add.Px = p.X; add.Py = p.Y;
            r.Keys++;
        }

        // What the sparse track costs between keys, against the arc it was taken from.
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f;
            if (!BodyPath.TrySample(clip, t, out var q)) continue;
            float e = Vector2.Distance(q, ClipMotion.ArcOffset(arc, ArcParam(clip, arc, t, stretch)));
            if (e > r.WorstError) { r.WorstError = e; r.WorstAt = t; }
        }

        BodyPath.TryCycleDisplacement(clip, out var d);
        r.CycleDisplacement = d;
        clip.Motion = MotionSource.Track;   // the clip owns its path from here on
        result = r;
        return true;
    }

    public static string Describe(AnimationDocument clip, HermiteClipDocument arc, in MapResult r, bool stretch)
        => $"{clip.Name}: com mapped onto the arc at {r.Keys} keys"
         + (r.Overwritten > 0 ? $" ({r.Overwritten} already had a body_path point — overwritten)" : "")
         + $"; pacing {(stretch ? "stretched to the clip" : $"{clip.Duration:0.00}s clip / {arc.Duration:0.00}s arc")}"
         + $"; D = ({r.CycleDisplacement.X:0.0}, {r.CycleDisplacement.Y:0.0}) rig"
         + $"; worst track-vs-arc gap {r.WorstError:0.00} rig at t={r.WorstAt:0.00}"
         + (r.WorstError > 1f ? " — add a keyframe there if that matters" : "");
}
