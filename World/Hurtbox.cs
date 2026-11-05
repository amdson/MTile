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
    public readonly EntityId    Target;   // owning entity — CombatSystem resolves it to dispatch OnHit
    // The owner is launched, stunned or in hitstun this frame (a player publishes it from
    // its CombatState). A grab never latches a helpless body (GrabAction.PickVictim), so a
    // throw cannot be chained into a regrab. Entities leave it false.
    public readonly bool        Helpless;

    public Hurtbox(BoundingBox region, Faction owner, EntityId target, bool helpless = false)
    {
        Region   = region;
        Owner    = owner;
        Target   = target;
        Helpless = helpless;
    }
}
