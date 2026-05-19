using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Roadmap §4.3 — ballistic projectile launched by LobbedAreaAction with a
// charge-time-derived budget. On landing (velocity-halted) two things happen:
//   1. MassBallPlanner.Plan is invoked at the landing site with a single zero-
//      velocity sample, producing a wide-base "splash" mound of sprouts (same
//      shape the planner makes for a stationary release of BlockReadyAction).
//   2. A one-shot area damage hitbox is published — radial segments, similar to
//      StickyGrenade, so anything caught in the splash takes a hit.
//
// The MassBallPlanner integration needs a ChunkMap reference, which Entity.Update
// doesn't normally surface. We grab it through ctx.Spawner.Chunks via an extra
// hook on IEntitySpawner — adding the property keeps the entity sandbox clean
// (no static ChunkMap reach-arounds).
public class LobbedAreaProjectile : Projectile
{
    private const float LifeSeconds       = 5.0f;
    private const float LandStopSpeed     = 30f;
    private const float ArmDelay          = 0.04f;
    private const float ExplosionRadius   = 48f;
    private const int   ExplosionSegments = 10;
    private const float SegmentHalfSize   = 9f;
    private const float ExplosionKnockback = 520f;
    private const float ExplosionDamage   = TileDamage.TileMaxHP * 0.8f;

    private static int _nextHitId = 7_000_001;
    private readonly int _hitId = System.Threading.Interlocked.Increment(ref _nextHitId);

    private readonly int _budget;
    private readonly TileType _tileType;
    private bool _detonated;

    public LobbedAreaProjectile(Vector2 pos, Vector2 launchVelocity, int budget, TileType tileType)
        : base(new PhysicsBody(Polygon.CreateRegular(5f, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: Faction.Player)
    {
        Body.Velocity = launchVelocity;
        Mass          = 0.8f;
        GravityScale  = 1f;
        Color         = Color.Sienna;
        Sprite        = Sprites.Ball(5f);
        _budget       = budget;
        _tileType     = tileType;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (_detonated) { Health = 0f; return; }
        if (Age < ArmDelay) return;

        // Land detection — same velocity-halted heuristic the other projectiles
        // use. Once the chunk solver has stopped us, we've landed.
        if (Body.Velocity.LengthSquared() >= LandStopSpeed * LandStopSpeed) return;

        // 1) Eruption mound at the landing site. Chunks come from the spawner
        // (Game1 implements both IEntitySpawner and IChunkProvider).
        var chunks = (spawner as IChunkProvider)?.Chunks;
        if (chunks != null && _budget > 0)
        {
            var samples = new[] { new PathSample(Body.Position, Vector2.Zero) };
            // Force the planner's active type for this one call. The planner
            // statics are mutated globally each frame from Game1 anyway, so a
            // brief override + restore is safe.
            var savedMass  = MassBallPlanner.ActiveType;
            var savedField = EruptionPlanner.ActiveType;
            MassBallPlanner.ActiveType = _tileType;
            EruptionPlanner.ActiveType = _tileType;
            try   { EruptionPlanner.Plan(chunks, Body.Position, samples, _budget); }
            finally
            {
                MassBallPlanner.ActiveType = savedMass;
                EruptionPlanner.ActiveType = savedField;
            }
        }

        // 2) AOE damage segments — same radial-shove shape StickyGrenade uses.
        if (hitboxes != null)
        {
            var center = Body.Position;
            for (int i = 0; i < ExplosionSegments; i++)
            {
                float angle = i * MathHelper.TwoPi / ExplosionSegments;
                var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var segCenter = center + dir * ExplosionRadius;
                var region = new BoundingBox(
                    segCenter.X - SegmentHalfSize, segCenter.Y - SegmentHalfSize,
                    segCenter.X + SegmentHalfSize, segCenter.Y + SegmentHalfSize);
                hitboxes.Publish(new Hitbox(
                    region, _hitId, ExplosionDamage,
                    dir * ExplosionKnockback,
                    Faction.Player, this, Color.Goldenrod));
            }
        }
        _detonated = true;
    }
}

// Sidecar interface so a projectile can reach the chunk map through the same
// spawner reference it already has. Game1 implements this; tests can leave
// IEntitySpawner.Chunks null without consequence.
public interface IChunkProvider
{
    ChunkMap Chunks { get; }
}
