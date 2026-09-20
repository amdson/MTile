using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// STRIDE TRACKS — derived, cached data compiled from a clip's contact spans
// (Plans/ANIMATION_STEP_PLANNER_PLAN.md / _IMPL.md, phase P1). Pure function of
// (AnimationDocument, Skeleton): when each foot should be planted (touchdown/liftoff
// phases), where the clip prefers it relative to the body, and the authored swing shape
// between stances. One track serves both the timing stage (GaitTiming reads the cycle
// displacement) and the step planner (which owns every foot in it while it runs).
// Never hand-edited.
//
// PLACEMENT CONVENTION (BodyPath's one contract — the live solve-root and the draw root:
// root = T(body + RootOffset(c)) · S(dir·scale, scale)): offsets here are stored in RIG
// UNITS at facing +1 with the per-phase authored `com` anchor c subtracted (both axes), so
// the runtime placement is exactly
//     world = bodyPos + (dir · scale · off.X, scale · off.Y).
// `com` is the authored placement anchor, not a measured center of mass; a clip without
// one falls back to c = 0, mirroring the live SolveRootAt fallback.
//
// STANCE SEMANTICS mirror WeightedContactsAtPhase: a keyframe's contacts hold over
// [t_k, t_{k+1}) — so a stance is a maximal run of consecutive keyframes labeling the
// foot, touchdown = the run's first key time, liftoff = the time of the first key after
// the run. Looping clips are treated as a key RING (a closing key at t≈1 duplicating the
// first is dropped), and runs wrap across the seam with phases unwrapped so
// Liftoff/Swing.End may exceed 1. A run covering the whole ring is a Persistent stance
// (idle-like: maintain support, don't invent strides) and has no swing.
//
// Structural validation only — no terrain or gait special cases. Any violation fails the
// whole compile with a message and the animator runs the clip on its own SelfPlant capture.

public struct StrideStance
{
    public float   Touchdown;   // phase in [0,1)
    public float   Liftoff;     // unwrapped: > Touchdown, may exceed 1 on a loop wrap
    public Vector2 TdOffset;    // preferred body-relative foot tip at touchdown (rig units)
    public Vector2 LoOffset;    // …at liftoff
    public bool    Persistent;  // stance spans the whole cycle — no stride events
}

public struct StrideSwing
{
    public float     Start;      // = the preceding stance's Liftoff (unwrapped)
    public float     End;        // = the following stance's Touchdown (unwrapped: > Start)
    public Vector2[] Residuals;  // SampleCount body-relative offsets MINUS the straight
                                 // chord between the swing's endpoint offsets, at uniform
                                 // u — additive shape, no chord normalization, so a
                                 // degenerate (coincident-endpoint) chord needs no
                                 // special case. Residuals[0] = Residuals[^1] = 0.
}

public sealed class FootStrideTrack
{
    public int            Bone;
    public string         Node;
    public StrideStance[] Stances;  // sorted by Touchdown; length ≥ 1
    public StrideSwing[]  Swings;   // Swings[i] follows Stances[i] (cyclically); empty iff Persistent
    public float          MaxReachRig;  // 1.25 × the clip's maximum authored |body→foot|
                                        // extension (rig units) — the reach bound the
                                        // planner gates candidates with. Derived from the
                                        // authored gait in the same com frame the offsets
                                        // live in (a raw bone-length sum measures from the
                                        // hip joint, not the com anchor, and undershoots).

    // The stance containing `phase` (index into Stances, u = progress inside it), or -1.
    public int StanceAt(float phase, out float u)
    {
        for (int i = 0; i < Stances.Length; i++)
        {
            var s = Stances[i];
            if (s.Persistent) { u = 0f; return i; }
            float span = s.Liftoff - s.Touchdown;
            float du = phase - s.Touchdown; du -= MathF.Floor(du);
            if (du < span) { u = du / span; return i; }
        }
        u = 0f; return -1;
    }

    // The swing containing `phase` (index into Swings, u = progress inside it), or -1.
    public int SwingAt(float phase, out float u)
    {
        for (int i = 0; i < Swings.Length; i++)
        {
            var w = Swings[i];
            float span = w.End - w.Start;
            float du = phase - w.Start; du -= MathF.Floor(du);
            if (du < span) { u = du / span; return i; }
        }
        u = 0f; return -1;
    }
}

public sealed class ClipStrideTrack
{
    public const int SwingSampleCount = 9;

    public FootStrideTrack[] Feet;   // one per named point; empty = the clip has no contact spans (legal)

    // AUTHORED BODY TRAVEL PER CYCLE (the timing stage, Plans/ANIMATION_TIMING_STAGE.md), rig
    // units at facing +1, signed (negative = the feet move forward under the body, a
    // backpedal). During a stance the foot's body-relative offset runs TdOffset → LoOffset,
    // so the body travels (Td.X − Lo.X) over Liftoff − Touchdown cycles; the mean rate
    // Σ travel / Σ span over every stance of every foot is the per-cycle distance — robust
    // to double support and flight, since one foot's stances cover only its own stance
    // fraction and the body moves at the same speed through flight. One constant rate per
    // cycle on purpose: a per-segment travel curve was measured (2026-09-10) and its within-
    // cycle variation came from the spline's easing between keys, not authored intent.
    // HasTravel is false when no foot has a non-persistent stance.
    //     Chunk 7: the displacement is a VECTOR — a stair cycle authors a rise as well as a
    //     run (Td.Y − Lo.Y < 0: the body climbs over the planted foot), and the timing stage
    //     projects the body's actual motion onto this direction (GaitTiming).
    public Vector2 CycleDisplacement;
    public bool    HasTravel;

    public FootStrideTrack ForBone(int bone)
    {
        foreach (var f in Feet) if (f.Bone == bone) return f;
        return null;
    }

    // Compile the clip's stride tracks. Returns false with `error` on any structural
    // violation; returns true with Feet.Length == 0 when the clip has no contact spans.
    public static bool TryCompile(AnimationDocument doc, Skeleton rig,
                                  out ClipStrideTrack track, out string error)
    {
        track = null; error = null;
        var ks = doc?.Keyframes;
        if (ks == null || ks.Count == 0) { error = "clip has no keyframes"; return false; }

        // The key ring: for looping clips, drop a closing key that duplicates the first
        // (t within epsilon of first + 1); the wrap segment supplies its interval.
        int ringCount = ks.Count;
        bool loop = doc.Loop && ks.Count >= 2;
        if (loop && MathF.Abs((ks[^1].Time - ks[0].Time) - 1f) < 1e-3f) ringCount--;
        if (ringCount < 1) { error = "clip has no usable keyframes"; return false; }

        // Every point the clip's spans name, whatever their source.
        var nodes = new List<string>();
        var spans = doc.Contacts;
        if (spans != null)
            foreach (var c0 in spans)
                if (c0.Point != null && !nodes.Contains(c0.Point)) nodes.Add(c0.Point);
        if (nodes.Count == 0) { track = new ClipStrideTrack { Feet = Array.Empty<FootStrideTrack>() }; return true; }

        // FK scratch for offset sampling (compile-time only; allocation is fine here).
        var a = rig.CreatePose(); var b = rig.CreatePose(); var c = rig.CreatePose();
        var d = rig.CreatePose(); var dst = rig.CreatePose();
        Vector2 OffsetAt(int bone, float phase)
        {
            float p = phase - MathF.Floor(phase);
            AnimationSampler.SampleSmooth(doc, p, a, b, c, d, dst);
            var w = dst.ComputeWorld(Affine2.Identity);
            Vector2 tip = w[bone].Translation;
            if (BodyPath.TrySampleAnchor(doc, p, out var com, out _)) tip -= com;
            return tip;
        }

        var feet = new List<FootStrideTrack>();
        foreach (var node in nodes)
        {
            if (!EndpointResolver.TryResolvePoint(rig, doc, node, out var rp) || !rp.IsExactTip)
            { error = $"contact '{node}' does not resolve to a bone tip of rig '{rig.Name}'"; return false; }
            int bone = rp.Bone;
            // Max authored extension: sample the foot offset around the whole cycle.
            float maxReach = 0f;
            for (int i = 0; i < 16; i++)
                maxReach = MathF.Max(maxReach, OffsetAt(bone, i / 16f).Length());
            maxReach *= 1.25f;

            // Stances ARE the authored spans. The ring walk this replaced existed only to
            // reconstruct intervals from per-keyframe labels: run detection, "liftoff = the key
            // AFTER the run", and the seam unwrap all reconstructed what a span now states.
            // A span covering the whole cycle is Persistent (idle-like: maintain support, don't
            // invent strides); OffsetAt wraps its own phase, so its two endpoints agree.
            var stances = new List<StrideStance>();
            foreach (var cs in spans)
            {
                if (cs.Point != node) continue;
                if (cs.End - cs.Start <= 1e-4f)
                { error = $"contact '{node}' has an empty span at {cs.Start:0.000}"; return false; }
                stances.Add(new StrideStance
                {
                    Touchdown  = cs.Start,
                    Liftoff    = cs.End,
                    TdOffset   = OffsetAt(bone, cs.Start),
                    LoOffset   = OffsetAt(bone, cs.End),
                    Persistent = cs.End - cs.Start >= 1f - 1e-4f,
                });
            }
            if (stances.Count == 0)
            { error = $"point '{node}' names no span"; return false; }
            stances.Sort((x, y) => x.Touchdown.CompareTo(y.Touchdown));

            // A persistent stance has no strides, so it has no swings either.
            if (stances.Count == 1 && stances[0].Persistent)
            {
                feet.Add(new FootStrideTrack { Bone = bone, Node = node, MaxReachRig = maxReach,
                                               Stances = stances.ToArray(),
                                               Swings = Array.Empty<StrideSwing>() });
                continue;
            }

            // Swings: from each stance's liftoff to the next stance's touchdown
            // (cyclically for loops; a non-loop's last stance has a swing only if
            // another stance follows).
            var swings = new List<StrideSwing>();
            for (int i = 0; i < stances.Count; i++)
            {
                bool last = i == stances.Count - 1;
                if (last && !loop) break;
                var cur = stances[i];
                var nxt = stances[(i + 1) % stances.Count];
                float start = cur.Liftoff;
                float end = nxt.Touchdown;
                while (end <= start) end += 1f;
                var res = new Vector2[SwingSampleCount];
                Vector2 p0 = OffsetAt(bone, start), p1 = OffsetAt(bone, end);
                for (int s = 0; s < SwingSampleCount; s++)
                {
                    float u = s / (float)(SwingSampleCount - 1);
                    res[s] = OffsetAt(bone, start + u * (end - start)) - Vector2.Lerp(p0, p1, u);
                }
                swings.Add(new StrideSwing { Start = start, End = end, Residuals = res });
            }

            feet.Add(new FootStrideTrack { Bone = bone, Node = node, MaxReachRig = maxReach,
                                           Stances = stances.ToArray(), Swings = swings.ToArray() });
        }

        track = new ClipStrideTrack { Feet = feet.ToArray() };
        Vector2 travel = Vector2.Zero; float span = 0f;
        foreach (var f in track.Feet)
            foreach (var st in f.Stances)
            {
                if (st.Persistent) continue;
                travel += st.TdOffset - st.LoOffset;
                span   += st.Liftoff - st.Touchdown;
            }
        if (span > 1e-4f) { track.CycleDisplacement = new Vector2(travel.X / span, travel.Y / span); track.HasTravel = true; }   // per-component: Vector2/float multiplies by a reciprocal
        return true;
    }
}
