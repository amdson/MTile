namespace MTile;

// Where a contact's world target comes from when the locomotion solver pins it.
//   SelfPlant — captured from the rig itself: on the frame the contact's weight
//               first goes nonzero, the node's current world position is captured
//               and held (a planted foot that must not slip).
//   External  — a fixed world point supplied by the sim/level over a time window
//               (e.g. the corner a ParkourState vault must keep a hand on).
public enum ContactSource
{
    SelfPlant,
    External,
}

// One contact annotation on an animation keyframe: a named skeleton node that should
// be pinned at this keyframe's instant. The contact point is the bone's tip
// (origin + Length along local +X). Absent/empty on a keyframe means no contact
// (airborne). Weight feathers plant/lift transitions so a foot swap is a smooth
// crossover rather than a discrete switch; it also scales the node's term in the
// solver's least-squares loss. See Plans/ANIMATION_LOCOMOTION_PLAN.md.
public sealed class ContactLabel
{
    public string        Node   { get; set; }                    // bone name; point = its tip
    public float         Weight { get; set; } = 1f;              // planted strength, [0,1]
    public ContactSource Source { get; set; } = ContactSource.SelfPlant;
}
