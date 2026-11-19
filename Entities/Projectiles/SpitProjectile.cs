using Microsoft.Xna.Framework;

namespace MTile;

// The spitter plant's shot: an EnergyBallProjectile that hits like ground slash 1 and ENDS
// on impact. Differences from the stock ball:
//   * Hit model: Collision mode along the shot's travel direction at S1's strike speed
//     (PokeStrikeSpeed, not the ball's own slow flight), S1's strike mass / restitution /
//     min launch, S1's authored knockback and hitstun - all read from SlashLikeAction /
//     GroundSlash1, never copied. Body damage stays the ball's damage (PlantTuning); it
//     is entities-only, so it never digs terrain, and it clanks nothing, so a held guard
//     blocks it like any poke.
//   * It dies the frame it touches a body it can hurt. The touch is detected in its own
//     update against this frame's published hurtboxes (same overlap + faction gate that
//     CombatSystem applies), the hit is published that same frame, and Health goes to 0 -
//     CombatSystem.Apply runs before dead entities are swept, so the hit still resolves.
//     A guarded or parried hit is still a hit, so it ends the shot too.
//   * It bursts (render-only cosmetic, via IEntitySpawner.NotifyShotBurst) on a body hit
//     and when terrain stops it. Lifetime expiry just fizzles.
// No new state: the kind alone carries the behaviour through a rollback rehydrate.
public sealed class SpitProjectile : EnergyBallProjectile
{
    public override EntityKind Kind => EntityKind.SpitShot;

    public SpitProjectile(Vector2 pos, Vector2 dir, int hitId, Faction owner,
                          float speed = PlantTuning.SpitterShotSpeed,
                          float damage = PlantTuning.SpitterShotDamage,
                          float lifetime = 0f)
        : base(pos, dir, hitId, owner, speed, damage, lifetime, pierce: false) { }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (Age >= ArmDelay && Body.Velocity.LengthSquared() < CollisionStopSpeed * CollisionStopSpeed)
        {
            Health = 0f;
            spawner?.NotifyShotBurst(Id, Body.Position, Team);
            return;
        }

        var p = Body.Position;
        var region = new BoundingBox(
            p.X - HitboxHalfSize, p.Y - HitboxHalfSize,
            p.X + HitboxHalfSize, p.Y + HitboxHalfSize);

        Vector2 vel = Body.Velocity;
        Vector2 dir = vel.LengthSquared() > 0.01f ? Vector2.Normalize(vel) : Vector2.UnitX;
        hitboxes?.Publish(new Hitbox(
            region, _hitId, _damage,
            dir * GroundSlash1.KnockbackPx,
            Faction, Id, Color,
            hitstunSecondsOverride: GroundSlash1.HitstunSeconds,
            targets: HitTargets.EntitiesOnly,
            mode: KnockbackMode.Collision,
            strikeDir: dir,
            strikeVelocity: dir * SlashLikeAction.PokeStrikeSpeed,
            strikeMass: SlashLikeAction.SlashStrikeMass,
            restitution: SlashLikeAction.SlashRestitution,
            minLaunch: SlashLikeAction.SlashMinLaunch,
            origin: p));

        if (TouchesHurtableBody(region, spawner))
        {
            Health = 0f;
            spawner.NotifyShotBurst(Id, p, Team);
        }
    }

    // CombatSystem's entity-hit gate, restated against the published hurtboxes: a body of
    // the shooter's own faction is skipped (enemy factions still hit across teams), as is
    // the co-op partner.
    private bool TouchesHurtableBody(BoundingBox region, IEntitySpawner spawner)
    {
        var hurtboxes = spawner?.Hurtboxes;
        if (hurtboxes == null) return false;
        foreach (var hb in hurtboxes.All)
        {
            if (hb.Target == Id) continue;
            if (hb.Owner == Faction)
            {
                bool acrossTeams = Faction == Faction.Enemy
                                   && spawner.Resolve(hb.Target) is Entity e && e.Team != Team;
                if (!acrossTeams) continue;
            }
            if (spawner.Coop && Factions.IsPlayer(Faction) && Factions.IsPlayer(hb.Owner)) continue;
            if (!HitboxWorld.Overlaps(hb.Region, region)) continue;
            return true;
        }
        return false;
    }
}
