using System;

namespace MTile;

// LOOP-BACK — where a cadence clip should loop, decided from the clip alone.
//
// A cycle clip normally wraps at phase 1 and the author is responsible for making the pose
// at 1 equal the pose at 0. This drops that responsibility: the clip is scored against
// itself (ClipTransitionGraph, windows clamped, the end pose sampled), and the cheapest jump
// from an exit in the clip's TAIL to an entry before the tail is compared with the authored
// seam's own cost — the jump from phase 1 to phase 0, which is just another scored pair.
// If the tail jump is cheaper, the runtime takes it (CharacterAnimator, before the timing
// stage): the clip plays its lead-in once, then loops [Entry, Exit). If the seam is at
// least as good, nothing changes: a perfectly looping clip scores its seam at ~0 and wraps
// as it always did. No annotation, no flag, no re-authoring.
//
// "Tail" and "shortest loop" are the two knobs (AnimSolverConfig.LoopBackRegion /
// LoopBackMinLoop): exits are taken from the last `region` of the clip so the clip runs a
// long stretch before it considers leaving, and an entry must be at least `minLoop` of the
// clip behind its exit so a jump never shortens the loop to a stutter. The match metric is
// TransitionOptions' default with Window = 0 — position plus velocity per sample, so the
// seam pair (end vs. start) and every interior pair are scored the same way with no window
// spilling past the clip's ends.
public readonly struct LoopBackPlan
{
    public readonly bool  HasCandidate;   // some exit/entry pair satisfied region + minLoop
    public readonly bool  Jumps;          // …and it beats the seam
    public readonly float Exit;           // authored phase to leave at
    public readonly float Entry;          // authored phase to land on
    public readonly float Cost;           // the tail jump's cost
    public readonly float SeamCost;       // the authored seam's cost (1 -> 0)

    public LoopBackPlan(bool hasCandidate, bool jumps, float exit, float entry, float cost, float seamCost)
    { HasCandidate = hasCandidate; Jumps = jumps; Exit = exit; Entry = entry; Cost = cost; SeamCost = seamCost; }

    public static LoopBackPlan None(float seamCost) => new(false, false, 1f, 0f, float.PositiveInfinity, seamCost);
}

public static class ClipLoopBack
{
    public static LoopBackPlan Plan(AnimationDocument doc, Skeleton rig, float region, float minLoop,
                                    TransitionOptions metric = null)
    {
        if (doc?.Keyframes == null || doc.Keyframes.Count < 2) return LoopBackPlan.None(0f);
        var m = metric ?? new TransitionOptions();
        m.Window = 0;
        m.TreatAsNonLooping = true;
        m.IncludeEndPhase = true;
        m.MaxCost = float.PositiveInfinity;

        var g = ClipTransitionGraph.Build(doc, doc, rig, m);
        // Only the KEYED range is motion: outside [first key, last key] a non-cyclic clip
        // holds a pose, and two held samples would match perfectly for no reason.
        var ks = doc.Keyframes;
        int first = g.IndexOf(ks[0].Time), last = g.IndexOf(ks[ks.Count - 1].Time);
        if (last - first < 2) return LoopBackPlan.None(0f);
        float seam = g.Cost[last, first];
        int span = last - first;

        region  = Math.Clamp(region, 0f, 1f);
        minLoop = Math.Clamp(minLoop, 0f, 1f);
        // Exits: inside the tail, strictly before the end (the end IS the seam). Entries:
        // before the tail and at least minLoop behind the exit.
        int tailStart = first + Math.Max(1, (int)MathF.Ceiling((1f - region) * span));
        int minLen    = Math.Max(1, (int)MathF.Ceiling(minLoop * span));

        bool any = false; int bestI = last, bestJ = first; float best = float.PositiveInfinity;
        for (int i = tailStart; i < last; i++)
            for (int j = first; j < tailStart && i - j >= minLen; j++)
            {
                float c = g.Cost[i, j];
                // Cheapest wins; a tie goes to the longer loop.
                if (c < best || (c == best && i - j > bestI - bestJ)) { best = c; bestI = i; bestJ = j; any = true; }
            }
        if (!any) return LoopBackPlan.None(seam);
        bool jumps = best < seam - 1e-4f;
        return new LoopBackPlan(true, jumps, g.PhaseOf(bestI), g.PhaseOf(bestJ), best, seam);
    }
}
