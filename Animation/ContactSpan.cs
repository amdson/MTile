using System;

namespace MTile;

// Where a contact's world target comes from when the locomotion solver pins it.
//   SelfPlant      — captured from the rig itself: on the frame the contact's weight
//                    first goes nonzero, the point's current world position is captured
//                    and held (a planted foot that must not slip).
//   External       — a fixed world point supplied by the sim/level over a time window
//                    (e.g. the corner a ParkourState vault must keep a hand on).
//   PlannedSupport — opt-in for the step planner (Plans/ANIMATION_STEP_PLANNER_PLAN.md):
//                    the interval REQUESTS terrain support for this point and the runtime
//                    (StepPlanner) chooses the support point. Feet in a clip carrying any
//                    PlannedSupport span are planner-owned: RefreshContacts' SelfPlant
//                    capture/release lifecycle skips them.
public enum ContactSource
{
    SelfPlant,
    External,
    PlannedSupport,
}

// ONE CONTACT, AS AN EXPLICIT INTERVAL. A named point pinned over [Start, End) of the clip's
// phase, with its strength shaped across that interval by an AnimCurve.
//
// This replaces the per-keyframe contact label, where an interval was EMERGENT — a label held
// from its keyframe to the next one, so a plant could not start between keys and retiming a
// keyframe silently retimed every contact that keyed on it. A span says what it means, and the
// timeline can hand you its endpoints to drag.
//
// WEIGHT feathers plant/lift transitions so a foot swap is a smooth crossover rather than a
// discrete switch, and it scales the point's term in the solver's least-squares loss. A null
// curve is NOT a hard-edged constant: it reads as DefaultWeight, an ease up/hold/ease down over
// the span. That matters beyond looks — RefreshContacts reads the SIGN of dw/dφ to know a
// release has begun (the foot-swap deadlock), so a contact whose weight never changes would
// never signal one. Overlapping spans with eased ends ARE the crossover the old fixed-width
// feather approximated, and unlike that feather the shape is authored per contact.
//
// LOOP WRAP: End may exceed 1 on a cyclic clip, exactly as StrideStance.Liftoff does — a stance
// that crosses the seam is one span, not two. Covers() tests both φ and φ+1 for that reason.
public sealed class ContactSpan
{
    // The named point this contact pins (EndpointResolver / NamedPoint) — the clip's own, then
    // the rig's, then a bare bone name meaning that bone's End.
    public string        Point  { get; set; }
    public float         Start  { get; set; }                    // phase, [0,1)
    public float         End    { get; set; } = 1f;              // unwrapped: > Start, may exceed 1
    public ContactSource Source { get; set; } = ContactSource.SelfPlant;

    // Strength across the span, on the curve's normalized domain. Null = DefaultWeight.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AnimCurve     Weight { get; set; }

    // The shape an unauthored span takes. The ramp fraction is close to the old solver-wide
    // FeatherWidth (0.12 of a cycle) at the stance lengths locomotion actually authors, but it
    // is a fraction of THE SPAN now, so a short plant eases quickly and a long one slowly
    // instead of every contact sharing one absolute crossover width.
    public static readonly AnimCurve DefaultWeight = AnimCurve.Ramp(1f, 0.15f, 0.15f);

    [System.Text.Json.Serialization.JsonIgnore]
    public AnimCurve EffectiveWeight => Weight ?? DefaultWeight;

    // An editable curve of this span's own. DefaultWeight is a SHARED static: handing it
    // straight to an editor that mutates in place would retune every unauthored span in the
    // project at once, so the first edit takes a copy.
    public AnimCurve EnsureWeight() => Weight ??= DefaultWeight.Clone();

    // Does this span cover `phase`, and where in it (u ∈ [0,1])? Handles the loop wrap by
    // retrying at φ+1, so a span [0.8, 1.2) covers both 0.9 and 0.1.
    public bool Covers(float phase, out float u)
    {
        float w = End - Start;
        if (w > 1e-9f)
        {
            for (int lap = 0; lap < 2; lap++)
            {
                float p = phase + lap;
                if (p >= Start && p < End) { u = (p - Start) / w; return true; }
            }
        }
        u = 0f; return false;
    }

    public float WeightAt(float phase)
        => Covers(phase, out float u) ? AnimCurve.ValueAt(EffectiveWeight, u) : 0f;

    // dw/dφ — zero outside the span. Inside, the curve's own slope through the chain rule, so
    // it is analytic everywhere the span is active rather than a step function with corners.
    public float SlopeAt(float phase)
    {
        if (!Covers(phase, out float u)) return 0f;
        float w = End - Start;
        return w <= 1e-9f ? 0f : AnimCurve.SlopeAt(EffectiveWeight, u) / w;
    }

    public ContactSpan Clone() => new()
    {
        Point = Point, Start = Start, End = End, Source = Source, Weight = Weight?.Clone(),
    };
}
