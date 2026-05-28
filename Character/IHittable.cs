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

    // Stable value identity. The combat dedupe table (HitId → set of already-hit
    // targets) keys on this, and it's what snapshots record so the table survives a
    // restore (entities may be rehydrated as fresh objects). Assigned by Simulation
    // from its deterministic id counter — players and entities draw from the same
    // sequence so ids never collide.
    EntityId Id { get; }
}
