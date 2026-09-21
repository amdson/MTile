using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// LOOP CUT — derive a clip's runtime loop from its best SELF-transition instead of
// trusting the authored seam.
//
// A cycle clip is normally authored to loop perfectly: the pose at phase 1 equals the pose
// at phase 0. That is a chore for anything with real structure (a stair cycle authored as
// "step, step, and a bit"), and the alternative is the motion-graph trick for extracting a
// cycle from non-cyclic data: find the phase pair (entry, exit) inside the clip where the
// motion around `exit` most resembles the motion around `entry` (ClipTransitionGraph built
// from the clip to itself), then play [entry, exit) as the loop and let the seam blend
// from the exit back into the entry over the same window the match was scored on.
//
// The result is an ordinary looping AnimationDocument — the timing stage, the stride track,
// the step planner and the sampler all see a normal cycle, so nothing downstream learns a
// new concept. The derived document is runtime-only: the authored file is untouched, the
// editor still shows the whole clip, and re-authoring the clip re-derives the cut.
//
// How the cut is built (Cut):
//   keyframes  — a key at 0 sampled at `entry`; every authored key strictly inside
//                (entry, exit − blend) retimed onto [0, 1]; a key at the blend start sampled
//                at `exit − blend`; and a closing key at 1 whose BONES copy the key at 0, so
//                the sampler treats the clip as cyclic (AnimationSampler.IsCyclic needs
//                first == last). The final segment therefore blends from the pose at
//                `exit − blend` to the pose at `entry` — the two agree to within the
//                transition cost by construction, so the blend replaces an authored stretch
//                of near-identical motion. `blend` is the metric's window length.
//   additions  — sampled at the same authored phases as the bones, except at the closing
//                key: `com` (the root-local placement anchor) copies the key at 0 so the
//                anchor is continuous across the seam, and every other addition is sampled
//                at `exit` — in particular body_path, whose p(1) − p(0) must be the true
//                per-loop displacement (BodyPath.TryCycleDisplacement).
//   contacts   — each span intersected with [entry, exit) and retimed; a span reaching 1 and
//                a span starting at 0 on the same point merge into one seam-crossing span,
//                the way a stance across the seam is authored on a native loop.
//   the rest   — Name, Type, Skeleton, Region, Motion, Scene, Points, Arcs, ExtraBones are
//                shared by reference; Duration scales with the cut length.
//
// How the cut is chosen (TryFind): every (exit, entry) with exit − entry ≥ MinLoop, cheapest
// first (longer loop wins a tie). When the clip carries contact spans, a candidate is
// accepted only if its cut compiles to a stride track with every named point present, so
// the loop contains at least one stance per foot.
public sealed class LoopCutOptions
{
    public float MinLoop = 0.5f;                     // shortest loop, as a fraction of the authored timeline
    public float MaxCost = float.PositiveInfinity;   // refuse a cut whose transition costs more
    public TransitionOptions Metric;                 // null = TransitionOptions defaults
}

public static class ClipLoopCut
{
    // Search the clip against itself for the cheapest loop. `cut.FromPhase` is the exit,
    // `cut.ToPhase` the entry, both on the AUTHORED timeline.
    public static bool TryFind(AnimationDocument doc, Skeleton rig, LoopCutOptions options,
                               out ClipTransition cut, out string error)
    {
        cut = default; error = null;
        if (doc?.Keyframes == null || doc.Keyframes.Count < 2) { error = "clip has fewer than two keyframes"; return false; }
        var o = options ?? new LoopCutOptions();
        var metric = CopyMetric(o.Metric);
        metric.TreatAsNonLooping = true;
        metric.MaxCost = float.PositiveInfinity;

        var g = ClipTransitionGraph.Build(doc, doc, rig, metric);
        int n = g.Samples;
        int minLen = Math.Max(1, (int)MathF.Ceiling(o.MinLoop * n));

        var candidates = new List<(int exit, int entry, float cost)>();
        for (int i = minLen; i < n; i++)
            for (int j = 0; i - j >= minLen; j++)
                candidates.Add((i, j, g.Cost[i, j]));
        if (candidates.Count == 0) { error = $"no loop of at least {o.MinLoop:0.00} of the clip fits"; return false; }
        candidates.Sort((x, y) =>
        {
            int c = x.cost.CompareTo(y.cost);
            return c != 0 ? c : (y.exit - y.entry).CompareTo(x.exit - x.entry);   // longer loop first
        });

        bool needStride = doc.Contacts is { Count: > 0 };
        int points = needStride ? DistinctPoints(doc.Contacts) : 0;
        foreach (var (exit, entry, cost) in candidates)
        {
            if (cost > o.MaxCost) break;
            if (needStride)
            {
                var trial = Cut(doc, rig, g.PhaseOf(entry), g.PhaseOf(exit), metric);
                if (!ClipStrideTrack.TryCompile(trial, rig, out var track, out _) || track.Feet.Length != points)
                    continue;   // the loop dropped a foot's every stance
            }
            cut = new ClipTransition(g.PhaseOf(exit), g.PhaseOf(entry), cost);
            return true;
        }
        error = needStride ? "no loop under MaxCost keeps a stance for every contact point"
                           : $"cheapest loop costs {candidates[0].cost:0.000}, over MaxCost {o.MaxCost:0.000}";
        return false;
    }

    // TryFind + Cut.
    public static bool TryApply(AnimationDocument doc, Skeleton rig, LoopCutOptions options,
                                out AnimationDocument loop, out ClipTransition cut, out string error)
    {
        loop = null;
        if (!TryFind(doc, rig, options, out cut, out error)) return false;
        var metric = CopyMetric(options?.Metric);
        loop = Cut(doc, rig, cut.ToPhase, cut.FromPhase, metric);
        return true;
    }

    // Build the looping document for [entry, exit) of `doc`'s authored timeline. `metric`
    // supplies the seam blend length (its window); null = defaults.
    public static AnimationDocument Cut(AnimationDocument doc, Skeleton rig, float entry, float exit,
                                        TransitionOptions metric = null)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        if (!(exit > entry)) throw new ArgumentException($"exit ({exit}) must follow entry ({entry})");
        float len = exit - entry;
        var m = metric ?? new TransitionOptions();
        // The seam blend, in authored units: the window the transition was scored on, but
        // never more than a third of the loop.
        float blend = MathF.Min((m.Window + 1) / (float)Math.Max(2, m.Samples), len / 3f);
        float blendStart = exit - blend;
        // An authored key this close (authored units) to a sampled boundary key is dropped
        // rather than kept as a near-duplicate that would kink the spline's tangents.
        float Eps = MathF.Max(1e-4f, 0.02f * len);

        var a = rig.CreatePose(); var b = rig.CreatePose(); var c = rig.CreatePose();
        var d = rig.CreatePose(); var dst = rig.CreatePose();
        AnimationKeyframe SampleKey(float authoredT, float cutT)
        {
            AnimationSampler.SampleSmooth(doc, authoredT, a, b, c, d, dst);
            return new AnimationKeyframe
            {
                Time = cutT,
                Bones = PoseData.Capture(dst),
                Additions = AnimAdditionSampler.Sample(doc, authoredT),
            };
        }

        var keys = new List<AnimationKeyframe>();
        var first = SampleKey(entry, 0f);
        keys.Add(first);
        foreach (var k in doc.Keyframes)
        {
            if (k.Time <= entry + Eps || k.Time >= blendStart - Eps) continue;
            keys.Add(new AnimationKeyframe
            {
                Time = (k.Time - entry) / len,
                Bones = CloneBones(k.Bones),
                Additions = CloneAdditions(k.Additions),
            });
        }
        if (blendStart > entry + Eps) keys.Add(SampleKey(blendStart, (blendStart - entry) / len));

        // The closing key: the entry's bones (a cyclic clip), the exit's additions (a true
        // per-loop displacement), except the anchor, which follows the bones.
        var atExit = AnimAdditionSampler.Sample(doc, exit);
        var closing = new AnimationKeyframe { Time = 1f, Bones = CloneBones(first.Bones), Additions = atExit };
        if (atExit != null && first.Additions != null)
            for (int i = 0; i < atExit.Count; i++)
                if (atExit[i].Name == BodyPath.AnchorName)
                {
                    var anchor0 = first.Additions.Find(x => x.Name == BodyPath.AnchorName);
                    if (anchor0 != null) atExit[i] = anchor0.Clone();
                }
        keys.Add(closing);

        var cut = new AnimationDocument
        {
            Name = doc.Name, Type = doc.Type, Skeleton = doc.Skeleton,
            Duration = doc.Duration * len, Loop = true,
            Region = doc.Region, OffRegionWeight = doc.OffRegionWeight, SettleShare = doc.SettleShare,
            Motion = doc.Motion, Arcs = doc.Arcs, Scene = doc.Scene, Points = doc.Points,
            ExtraBones = doc.ExtraBones,
            Keyframes = keys,
            Contacts = CutContacts(doc.Contacts, entry, exit),
            Attachments = CutAttachments(doc.Attachments, entry, exit),
        };
        return cut;
    }

    // ---- pieces ----------------------------------------------------------------

    private static List<ContactSpan> CutContacts(List<ContactSpan> spans, float entry, float exit)
    {
        if (spans == null || spans.Count == 0) return null;
        float len = exit - entry;
        const float Eps = 1e-4f;
        var outSpans = new List<ContactSpan>();
        foreach (var s in spans)
        {
            // A span unwrapped past 1 on an authored loop is also the interval one cycle
            // earlier; both views are intersected so a seam-crossing authored stance is kept.
            for (int lap = 0; lap < 2; lap++)
            {
                float s0 = s.Start - lap, e0 = s.End - lap;
                float cs = MathF.Max(s0, entry), ce = MathF.Min(e0, exit);
                if (ce - cs <= Eps) continue;
                bool whole = cs - s0 <= Eps && e0 - ce <= Eps;
                outSpans.Add(new ContactSpan
                {
                    Point = s.Point, Source = s.Source,
                    Start = (cs - entry) / len, End = (ce - entry) / len,
                    Weight = whole ? s.Weight?.Clone() : null,   // a cropped span takes the default shape
                });
            }
        }
        // Merge a span ending at the seam with one starting at it, per point.
        for (int i = outSpans.Count - 1; i >= 0; i--)
        {
            var tail = outSpans[i];
            if (tail.End < 1f - Eps) continue;
            for (int j = 0; j < outSpans.Count; j++)
            {
                var head = outSpans[j];
                if (j == i || head.Point != tail.Point || head.Start > Eps) continue;
                tail.End = 1f + head.End;
                tail.Weight = null;
                outSpans.RemoveAt(j);
                if (j < i) i--;
                break;
            }
        }
        outSpans.Sort((x, y) => x.Start.CompareTo(y.Start));
        return outSpans;
    }

    private static List<AnimAttachment> CutAttachments(List<AnimAttachment> list, float entry, float exit)
    {
        if (list == null || list.Count == 0) return null;
        float len = exit - entry;
        var result = new List<AnimAttachment>();
        foreach (var at in list)
        {
            float s = MathF.Max(0f, (at.Start - entry) / len), e = MathF.Min(1f, (at.End - entry) / len);
            if (e - s <= 1e-4f) continue;
            List<float> frames = null;
            if (at.FrameTimes != null)
            {
                frames = new List<float>();
                foreach (float ft in at.FrameTimes)
                {
                    float t = (ft - entry) / len;
                    if (t >= 0f && t <= 1f) frames.Add(t);
                }
            }
            result.Add(new AnimAttachment
            {
                Point = at.Point, Effect = at.Effect, Start = s, End = e,
                Scale = at.Scale, Rotation = at.Rotation,
                FrameTimes = frames is { Count: > 0 } ? frames.ToArray() : null,
            });
        }
        return result.Count > 0 ? result : null;
    }

    private static List<PoseBoneEntry> CloneBones(List<PoseBoneEntry> bones)
    {
        if (bones == null) return new List<PoseBoneEntry>();
        var list = new List<PoseBoneEntry>(bones.Count);
        foreach (var e in bones) list.Add(new PoseBoneEntry { Bone = e.Bone, Rotation = e.Rotation, Stretch = e.Stretch });
        return list;
    }

    private static List<AnimAddition> CloneAdditions(List<AnimAddition> adds)
    {
        if (adds == null) return null;
        var list = new List<AnimAddition>(adds.Count);
        foreach (var a in adds) list.Add(a.Clone());
        return list;
    }

    private static int DistinctPoints(List<ContactSpan> spans)
    {
        var seen = new List<string>();
        foreach (var s in spans) if (s.Point != null && !seen.Contains(s.Point)) seen.Add(s.Point);
        return seen.Count;
    }

    private static TransitionOptions CopyMetric(TransitionOptions src)
    {
        var m = new TransitionOptions();
        if (src == null) return m;
        m.Samples = src.Samples; m.Window = src.Window;
        m.AngleWeight = src.AngleWeight; m.FootPositionWeight = src.FootPositionWeight;
        m.FootVelocityWeight = src.FootVelocityWeight; m.ContactMismatch = src.ContactMismatch;
        m.MaxCost = src.MaxCost; m.FootRole = src.FootRole; m.TreatAsNonLooping = src.TreatAsNonLooping;
        return m;
    }
}
