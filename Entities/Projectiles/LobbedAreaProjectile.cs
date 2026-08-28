using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Roadmap §4.3 — ballistic projectile launched by LobbedAreaAction with a
// charge-time-derived budget, and (Plans/BLOCK_THROW_PLAN.md) the thrown clod of a
// block grab. On landing (velocity-halted) two things happen:
//   1. Its whole budget is injected into TileMassField at the landing cell, and the
//      spill cascade grows the "splash" mound outward from there. This used to call
//      EruptionPlanner.Plan with a single zero-velocity sample; the mass field reaches
//      the same shape by flowing instead of by scoring cells and top-K'ing them.
//   2. A one-shot area damage hitbox is published — radial segments, similar to
//      StickyGrenade, so anything caught in the splash takes a hit.
//
// TRACKING PHASE (block grab, §4.7). A grab's ball is born at rest where the blocks
// were, following its PullPointEntity with a critically damped, speed-capped tracker —
// the same code whether the point is still in the player's hand or already flying
// after release. While tracking: gravity off, tiles ON (a held clod is a physical
// object — it can't be dragged into the floor, it slides along a wall the cursor
// crosses), no hurtbox, and the budget bleeds while the point is held. The tracker
// writes only Body.Velocity; PhysicsWorld integrates the position. Once the point has
// been released the ball DETACHES when its velocity has converged to the point's (or
// after GrabChaseMaxSeconds, or if the point is gone): gravity on, and the ordinary
// land-and-erupt life above takes over with whatever budget survived the carry.
// Detach velocity ≈ the point's hand-off velocity, capped — the same number whether
// the clod was in hand or in the ground when the button came up.
//
// The deposit needs a ChunkMap reference, which Entity.Update doesn't normally surface.
// We grab it through ctx.Spawner.Chunks via an extra hook on IEntitySpawner — adding
// the property keeps the entity sandbox clean (no static ChunkMap reach-arounds).
public class LobbedAreaProjectile : Projectile
{
    private const float LifeSeconds       = 5.0f;
    private const float LandStopSpeed     = 30f;
    private const float ArmDelay          = 0.04f;
    private const float ExplosionRadius   = 3f * Chunk.TileSize;
    private const int   ExplosionSegments = 10;
    private const float SegmentHalfSize   = 9f;
    private const float ExplosionKnockback = 520f;
    private const float ExplosionDamage   = TileDamage.TileMaxHP * 0.8f;
    private const float BodyRadius        = 5f;
    // Drawn radius at full charge; scales with √(blocks remaining) like the old held orb.
    private const float OrbMaxRadius      = PlayerCharacter.Radius * 0.9f;
    private const float OrbMaxBlocks      = 9f;

    private readonly int _hitId;

    private int      _budget;
    private readonly TileType _tileType;
    private bool _detonated;

    // Tracking phase (block grab).
    private bool     _tracking;
    private EntityId _pointId;
    private int      _harvest;
    private float    _carryTime;
    private float    _chaseTime;

    public override EntityKind Kind => EntityKind.LobbedArea;

    public bool     Tracking      => _tracking;
    public EntityId PointId       => _pointId;
    public int      Budget        => _budget;
    public int      HarvestBlocks => _harvest;
    public TileType TileType      => _tileType;

    // hitId/tileType are immutable (ctor) — recorded so Rehydrate can reconstruct via
    // the ctor. Budget bleeds during a carry; the tracking fields are per-frame state.
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId         = _hitId;
        s.Budget        = _budget;
        s.TileType      = _tileType;
        s.Detonated     = _detonated;
        s.Tracking      = _tracking;
        s.LinkedId      = _pointId;
        s.HarvestBlocks = _harvest;
        s.CarryTime     = _carryTime;
        s.ChaseTime     = _chaseTime;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _budget    = s.Budget;
        _detonated = s.Detonated;
        _tracking  = s.Tracking;
        _pointId   = s.LinkedId;
        _harvest   = s.HarvestBlocks;
        _carryTime = s.CarryTime;
        _chaseTime = s.ChaseTime;
    }

    public LobbedAreaProjectile(Vector2 pos, Vector2 launchVelocity, int budget, TileType tileType, int hitId, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(BodyRadius, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        Body.Velocity = launchVelocity;
        Mass          = 0.8f;
        GravityScale  = 1f;
        Color         = Color.Sienna;
        Sprite        = Sprites.Ball(BodyRadius);
        _budget       = budget;
        _harvest      = budget;
        _tileType     = tileType;
        _hitId        = hitId;
    }

    // A block grab's ball: born at rest, weightless, following `point` until detach.
    public static LobbedAreaProjectile MakeTracking(Vector2 pos, int blocks, TileType tileType, int hitId, Faction owner, EntityId point)
    {
        var ball = new LobbedAreaProjectile(pos, Vector2.Zero, blocks, tileType, hitId, owner)
        {
            _tracking    = true,
            _pointId     = point,
            GravityScale = 0f,
        };
        return ball;
    }

    // Tracking balls aren't targets — whether an opponent can slash the clod out of
    // your hand is a design question, default no (plan §4.7).
    public override void PublishHurtboxes(HurtboxWorld world)
    {
        if (!_tracking) base.PublishHurtboxes(world);
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (_tracking) { Track(dt, spawner); return; }

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
            // Material was captured at launch, so the mound is made of whatever the
            // player had selected when they threw.
            int gtx = (int)MathF.Floor(Body.Position.X / Chunk.TileSize);
            int gty = (int)MathF.Floor(Body.Position.Y / Chunk.TileSize);
            chunks.Mass.Deposit(chunks, gtx, gty, _budget, _tileType);
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
                    Faction, Id, Color.Goldenrod,
                    origin: center));
            }
        }
        _detonated = true;
    }

    // One frame of following the pulling point. Velocity-matching (critically damped),
    // so the ball settles to the point's velocity rather than to Vmax or zero — which
    // is what makes the detach speed the swipe speed (plan §4.1).
    private void Track(float dt, IEntitySpawner spawner)
    {
        var cfg   = MovementConfig.Current;
        var point = spawner?.Resolve(_pointId) as PullPointEntity;
        if (point == null || point.IsDead) { Detach(); return; }

        float smoothTime;
        if (point.Driven)
        {
            // In hand: the budget bleeds with carry time; empty ⇒ the clod is gone.
            smoothTime = cfg.GrabBallSmoothTime;
            _carryTime += dt;
            float frac = 1f - _carryTime / MathF.Max(1e-3f, cfg.GrabDissipateSeconds);
            // Ceiling, not floor: keep the full count until the bleed has actually
            // consumed a block; reach zero exactly at GrabDissipateSeconds.
            _budget = frac <= 0f ? 0 : (int)MathF.Ceiling(_harvest * frac);
            if (_budget <= 0) { Health = 0f; return; }
        }
        else
        {
            smoothTime = cfg.GrabChaseSmoothTime;
            _chaseTime += dt;
        }

        // Tracker on a copy; only the velocity goes back — physics moves the body (and
        // resolves the tiles it slides along).
        var pos = Body.Position;
        var vel = Body.Velocity;
        SmoothPen.CriticallyDampedStep(ref pos, ref vel, point.Body.Position, smoothTime, dt);
        float speed = vel.Length();
        if (speed > cfg.GrabBallMaxSpeed) vel *= cfg.GrabBallMaxSpeed / speed;
        Body.Velocity = vel;

        if (point.Driven) return;
        if ((vel - point.Body.Velocity).Length() < cfg.GrabCatchSpeed
            || _chaseTime >= cfg.GrabChaseMaxSeconds)
            Detach();
    }

    // Let go of the point: a free ballistic lob from here, arming clock restarted so a
    // dropped clod isn't "landed" on its first free frame.
    private void Detach()
    {
        _tracking    = false;
        GravityScale = 1f;
        Age          = 0f;
    }

    // Drawn size tracks the blocks left, tinted by material — the held clod and the
    // thrown one are the same object, so they can't disagree.
    public override void SyncSprite()
    {
        base.SyncSprite();
        if (Sprite == null) return;
        float r = OrbMaxRadius * MathF.Sqrt(MathF.Min(1f, _budget / OrbMaxBlocks));
        Sprite.Scale = MathF.Max(0.4f, r / BodyRadius);
        Sprite.Tint  = TilePalette.BaseColor(_tileType);
    }
}

// Sidecar interface so a projectile can reach the chunk map through the same
// spawner reference it already has. Game1 implements this; tests can leave
// IEntitySpawner.Chunks null without consequence.
public interface IChunkProvider
{
    ChunkMap Chunks { get; }
}
