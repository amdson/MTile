namespace MTile;

// Value-typed, single-frame DEFENSIVE region. Each IHittable publishes one (or
// more) Hurtboxes each frame describing the world-space regions where it can be
// hit. CombatSystem walks (hitbox × hurtbox) pairs and dispatches OnHit through
// the Target back-pointer.
//
// Hurtboxes carry no damage/knockback data — that lives on the hitbox. They are
// pure "this is where I am vulnerable this frame" markers.
public readonly struct Hurtbox
{
    public readonly BoundingBox Region;   // AABB in world coords
    public readonly Faction     Owner;    // for self-damage filtering against Hitbox.Owner
    public readonly IHittable   Target;   // dispatch back-pointer for OnHit

    public Hurtbox(BoundingBox region, Faction owner, IHittable target)
    {
        Region = region;
        Owner  = owner;
        Target = target;
    }
}
