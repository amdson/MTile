namespace MTile;

// One cell of a block-peel group (PullPointEntity). Tether is the group→block bond
// (built by the paint kernel, worn by this block's share of the spring force; ≤0 drops
// the block from the group). GlueWear is accumulated damage to the block→world
// attachment — stored as wear rather than remaining glue because the glue's BASE value
// is recomputed live from material + outward edges, which change as neighbors join the
// group or get broken by someone else.
public struct PeelMember
{
    public int   Gtx, Gty;
    public float Tether;
    public float GlueWear;
}

// Fixed 25-slot inline buffer (C# 12 InlineArray): value semantics, so it snapshots
// with a plain struct copy. 25 is the design cap on group size — it bounds both the
// per-frame peel cost and the rollback state, and "can't paint past the cap" is itself
// a gameplay rule (no eviction; paint deliberately).
[System.Runtime.CompilerServices.InlineArray(Capacity)]
public struct PeelMemberBuffer
{
    public const int Capacity = 25;
    private PeelMember _element0;
}

// The peel group of a PullPointEntity, as a SPARSE snapshotted component: only point
// entities carry one, so the ~400 bytes of member buffer cost nothing on the rest of
// the zoo (EntityData is unioned across every entity kind and would otherwise grow by
// that much per entity per snapshot). The World captures it generically like any other
// value store; the entity marshals it in its CaptureState/RestoreState overrides.
// Only [0, Count) of Members is live; removal compacts order-preservingly so iteration
// order is identical on both sides of a rollback.
public struct PeelGroupComp
{
    public PeelMemberBuffer Members;
    public int   Count;
    public float Strain;    // spring load / snap cap, 0..1 — sim-written, read by the overlay draw
    public bool  Snapped;   // spring exceeded its cap: the attempt is dead
}
