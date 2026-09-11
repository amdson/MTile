namespace MTile;

// Where a contact's world target comes from when the locomotion solver pins it.
//   SelfPlant      — captured from the rig itself: on the frame the contact's weight
//                    first goes nonzero, the node's current world position is captured
//                    and held (a planted foot that must not slip).
//   External       — a fixed world point supplied by the sim/level over a time window
//                    (e.g. the corner a ParkourState vault must keep a hand on).
//   PlannedSupport — opt-in for the step planner (Plans/ANIMATION_STEP_PLANNER_PLAN.md):
//                    the interval REQUESTS terrain support for this node and the runtime
//                    (StepPlanner) chooses the support point. Feet in a clip carrying any
//                    PlannedSupport label are planner-owned: RefreshContacts' SelfPlant
//                    capture/release lifecycle skips them. On clips that never opt in,
//                    behavior is unchanged.
public enum ContactSource
{
    SelfPlant,
    External,
    PlannedSupport,
}

// One contact annotation on an animation keyframe: a named skeleton node that should
// be pinned at this keyframe's instant. The contact point is the bone's tip
// (origin + Length along local +X). Absent/empty on a keyframe means no contact
// (airborne). Weight feathers plant/lift transitions so a foot swap is a smooth
// crossover rather than a discrete switch; it also scales the node's term in the
// solver's least-squares loss. See Plans/ANIMATION_LOCOMOTION_PLAN.md.
public sealed class ContactLabel
{
    public string        Node   { get; set; }                    // LEGACY: bone name; point = its tip
    // The named point this contact pins (EndpointResolver / NamedPoint) — the rig's or the
    // clip's. Optional during the migration off helper bones: a label may carry Node, Point,
    // or both when they agree; conflicting records are rejected by the resolver.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string        Point  { get; set; }
    public float         Weight { get; set; } = 1f;              // planted strength, [0,1]
    public ContactSource Source { get; set; } = ContactSource.SelfPlant;

    // The identity a consumer groups labels by: the point id, else the legacy node name.
    [System.Text.Json.Serialization.JsonIgnore] public string Key => Point ?? Node;
    public ContactLabel Clone() => new() { Node = Node, Point = Point, Weight = Weight, Source = Source };
}
