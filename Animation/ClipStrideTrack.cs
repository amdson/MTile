using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// STRIDE TRACKS — derived, cached data compiled from a clip's PlannedSupport contact
// labels (Plans/ANIMATION_STEP_PLANNER_PLAN.md / _IMPL.md, phase P1). Pure function of
// (AnimationDocument, Skeleton): when each opted-in foot should be planted (touchdown/
// liftoff phases), where the clip prefers it relative to the body, and the authored
// swing shape between stances. Consumed by StepPlanner; never hand-edited.
//
// PLACEMENT CONVENTION (must match the live solve-root, CharacterAnimator.Update step 2:
// root = T(BodyX, BodyY − com.Y·scale) · S(dir·scale, scale)): offsets here are stored in
// RIG UNITS at facing +1 with the per-phase authored `com` anchor's Y subtracted, so the
// runtime placement is exactly
//     world = bodyPos + (dir · scale · off.X, scale · off.Y).
// `com` is the authored placement anchor, not a measured center of mass; a clip without
// one falls back to com.Y = 0, mirroring the live comBaseY fallback.
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
// whole compile with a message and the clip stays on the legacy SelfPlant path.

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
}

public sealed class ClipStrideTrack
{
    public const int SwingSampleCount = 9;

    public FootStrideTrack[] Feet;   // one per opted-in node; empty compile = no opt-in (legal, legacy path)

    public FootStrideTrack ForBone(int bone)
    {
        foreach (var f in Feet) if (f.Bone == bone) return f;
        return null;
    }

    // Compile the clip's stride tracks. Returns false with `error` on any structural
    // violation; returns true with Feet.Length == 0 when the clip simply doesn't opt in.
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

        // Opted-in nodes: any PlannedSupport label anywhere in the clip. A node mixing
        // PlannedSupport with SelfPlant/External is ambiguous ownership — refused.
        var nodes = new List<string>();
        foreach (var k in ks)
        {
            if (k.Contacts == null) continue;
            foreach (var l in k.Contacts)
                if (l.Source == ContactSource.PlannedSupport && !nodes.Contains(l.Node))
                    nodes.Add(l.Node);
        }
        if (nodes.Count == 0) { track = new ClipStrideTrack { Feet = Array.Empty<FootStrideTrack>() }; return true; }
        foreach (var k in ks)
        {
            if (k.Contacts == null) continue;
            foreach (var l in k.Contacts)
                if (l.Source != ContactSource.PlannedSupport && nodes.Contains(l.Node))
                { error = $"node '{l.Node}' mixes PlannedSupport with {l.Source} labels"; return false; }
        }

        // FK scratch for offset sampling (compile-time only; allocation is fine here).
        var a = rig.CreatePose(); var b = rig.CreatePose(); var c = rig.CreatePose();
        var d = rig.CreatePose(); var dst = rig.CreatePose();
        Vector2 OffsetAt(int bone, float phase)
        {
            float p = phase - MathF.Floor(phase);
            AnimationSampler.SampleSmooth(doc, p, a, b, c, d, dst);
            var w = dst.ComputeWorld(Affine2.Identity);
            Vector2 tip = w[bone].Translation;
            if (AnimAdditionSampler.SamplePoint(doc, p, "com", out var com)) tip.Y -= com.Y;
            return tip;
        }

        var feet = new List<FootStrideTrack>();
        foreach (var node in nodes)
        {
            int bone = rig.IndexOf(node);
            if (bone < 0) { error = $"PlannedSupport node '{node}' is not a bone of rig '{rig.Name}'"; return false; }
            // Max authored extension: sample the foot offset around the whole cycle.
            float maxReach = 0f;
            for (int i = 0; i < 16; i++)
                maxReach = MathF.Max(maxReach, OffsetAt(bone, i / 16f).Length());
            maxReach *= 1.25f;

            // Per-ring-key: does this key label the node?
            Span<bool> on = ringCount <= 64 ? stackalloc bool[ringCount] : new bool[ringCount];
            for (int i = 0; i < ringCount; i++)
            {
                var labels = ks[i].Contacts;
                if (labels == null) continue;
                foreach (var l in labels) if (l.Node == node) { on[i] = true; break; }
            }

            // Maximal runs on the ring (wrapping when looping).
            var stances = new List<StrideStance>();
            bool all = true;
            for (int i = 0; i < ringCount; i++) if (!on[i]) { all = false; break; }
            if (all)
            {
                var off0 = OffsetAt(bone, ks[0].Time);
                stances.Add(new StrideStance { Touchdown = ks[0].Time, Liftoff = ks[0].Time + 1f,
                                               TdOffset = off0, LoOffset = off0, Persistent = true });
                feet.Add(new FootStrideTrack { Bone = bone, Node = node, MaxReachRig = maxReach,
                                               Stances = stances.ToArray(),
                                               Swings = Array.Empty<StrideSwing>() });
                continue;
            }

            float RingTime(int i) => ks[i % ringCount].Time + (i / ringCount) * 1f;
            int NextIdx(int i) => loop ? (i + 1) % ringCount : i + 1;
            for (int i = 0; i < ringCount; i++)
            {
                bool prevOn = loop ? on[(i - 1 + ringCount) % ringCount] : (i > 0 && on[i - 1]);
                if (!on[i] || prevOn) continue;                    // not a run start
                int j = i, len = 1;
                while (true)
                {
                    int n = NextIdx(j);
                    if (loop ? n == i : n >= ringCount) break;     // wrapped fully / hit clip end
                    if (!on[n]) break;
                    j = n; len++;
                    if (len > ringCount) break;                    // safety, unreachable
                }
                float td = ks[i].Time;
                // Liftoff = the key AFTER the run. Unwrap when the run crossed the seam
                // or the liftoff key wrapped past it. Non-loop runs ending at the last
                // key hold to the clip end (liftoff = 1).
                float lo;
                if (!loop && j == ringCount - 1) lo = 1f;
                else
                {
                    int after = NextIdx(j);
                    lo = ks[after].Time;
                    while (lo <= td) lo += 1f;
                }
                stances.Add(new StrideStance { Touchdown = td, Liftoff = lo,
                                               TdOffset = OffsetAt(bone, td),
                                               LoOffset = OffsetAt(bone, lo) });
            }
            if (stances.Count == 0)
            { error = $"node '{node}' has PlannedSupport labels but no stance run"; return false; }
            stances.Sort((x, y) => x.Touchdown.CompareTo(y.Touchdown));

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
        return true;
    }
}
