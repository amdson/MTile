namespace MTile;

// Common surface for anything that can receive damage — player, future enemies,
// destructible non-tile props. Tiles aren't IHittables: they're addressed by cell
// coords via ChunkMap.DamageCell, not by world-space hurtbox bounds.
//
// CombatSystem dispatches OnHit per (HitId, Target) — see CombatSystem.Apply.
public interface IHittable
{
    Faction Faction { get; }
    void PublishHurtboxes(HurtboxWorld world);
    void OnHit(in Hitbox hit, in Hurtbox myHurtbox);

    // Stable identity for snapshot/restore. The combat dedupe table (HitId → set of
    // already-hit targets) is held by reference at runtime; to snapshot it we record
    // sets of these ids and resolve them back to live objects on restore (roadmap
    // goal 4 §H). Assigned by Simulation from its deterministic id counter — players
    // and entities draw from the same sequence so ids never collide.
    int HittableId { get; }
}
