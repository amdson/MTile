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
}
