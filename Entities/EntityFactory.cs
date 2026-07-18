using Microsoft.Xna.Framework;

namespace MTile;

// Factory constructors for the V1 entity zoo. Balloons + balls share the Entity
// class; they differ only in initial parameters (Mass, GravityScale, Health, Color).
// Add more constructors here as new entity flavors land.
public static class EntityFactory
{
    public static Entity Balloon(Vector2 pos) => new(
        new PhysicsBody(Polygon.CreateRegular(8f, 8), pos), health: 0.5f)
    {
        Mass         = 0.5f,
        GravityScale = 0f,
        Color        = Color.HotPink,
        Faction      = Faction.Neutral,
        Sprite       = Sprites.Balloon(8f),
    };

    public static Entity Ball(Vector2 pos) => MakeBall(pos, gravityScale: 1f, Color.SteelBlue);

    // Juggling drill target — see PracticeBall (breaks on tile contact, respawns).
    public static PracticeBall Practice(Vector2 spawn) => new(spawn);

    // First active NPC. Walks toward the player on the ground and commits to a
    // telegraph→lunge attack when in range. See StalkerEnemy for the AI.
    public static StalkerEnemy Stalker(Vector2 pos) => new(pos);

    // Stationary ranged enemy. See TurretEnemy for the charge → fire cycle.
    public static TurretEnemy Turret(Vector2 pos) => new(pos);

    // Same crasher config as Ball but weightless — for the impact-damage test
    // chamber, where the player slashes balls into a wall without gravity
    // dropping them to the floor first.
    public static Entity FloatingBall(Vector2 pos) => MakeBall(pos, gravityScale: 0f, Color.Coral);

    private static Entity MakeBall(Vector2 pos, float gravityScale, Color color)
    {
        var body = new PhysicsBody(Polygon.CreateRegular(6f, 8), pos)
        {
            // Crasher config: terminal-ish falls (~400-500 px/s) break Dirt,
            // harder throws break Stone. Tuning lives in impact_profiles.json
            // under the "ball" key.
            Impact = ImpactProfiles.Build(ImpactProfiles.Ball),
        };
        return new Entity(body, health: 1.0f)
        {
            Mass         = 1.5f,
            GravityScale = gravityScale,
            Color        = color,
            Faction      = Faction.Neutral,
            Sprite       = Sprites.Ball(6f),
        };
    }

    // Construct a fresh entity of the captured Kind from the snapshot components, for
    // restore of an entity that despawned since the snapshot frame (a live entity is
    // restored in place by RestoreState instead). The gameplay ctor runs (sprite,
    // immutable Impact/config); the caller then calls RestoreState to overwrite every
    // dynamic field from the components, so post-ctor transients don't leak. `hitIds`
    // is only consulted by Bullet (re-deflect allocator); its counter is restored
    // separately by SimSnapshot. Falls through to a Generic entity for any kind not
    // registered with EnemyFactory — the same path the old EntitySnapshot.Rehydrate took.
    public static Entity Rehydrate(in EntityData d, in BodyState body, HitIdAllocator hitIds)
        => d.Kind switch
        {
            EntityKind.Generic       => new Entity(new PhysicsBody(d.Polygon, body.Position) { Impact = d.Impact }, d.MaxHealth),
            EntityKind.Stalker       => new StalkerEnemy(body.Position),
            EntityKind.Turret        => new TurretEnemy(body.Position),
            EntityKind.Bullet        => new BulletProjectile(body.Position, body.Velocity, hitIds),
            EntityKind.EnergyBall    => new EnergyBallProjectile(body.Position, body.Velocity, d.HitId, d.Faction),
            EntityKind.StickyGrenade => new StickyGrenadeProjectile(body.Position, body.Velocity, d.HitId, d.Faction),
            EntityKind.LobbedArea    => new LobbedAreaProjectile(body.Position, body.Velocity, d.Budget, d.TileType, d.Mode, d.HitId, d.Faction),
            EntityKind.Brute         => new BruteEnemy(body.Position),
            // Spawn-point placeholder — RestoreState's ReadState overwrites it from
            // the snapshotted Aim slot right after construction.
            EntityKind.PracticeBall  => new PracticeBall(body.Position),
            _ when EnemyFactory.IsRegistered(d.Kind) => EnemyFactory.Create(d.Kind, body.Position),
            _                        => new Entity(new PhysicsBody(d.Polygon, body.Position) { Impact = d.Impact }, d.MaxHealth),
        };
}
