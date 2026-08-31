using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Roadmap §4.3 — ballistic projectile launched by LobbedAreaAction with a
// charge-time-derived budget, and (Plans/BLOCK_THROW_PLAN.md) the thrown clod of a
// block grab. It bursts on either of two fuses — touching a body it can hurt, or
// touching terrain (the solver's contact impulse, NOT a settled-to-a-stop test: the
// clod breaks where it strikes, and a lob can't self-detonate at its apex) — and the
// burst is the same either way:
//   1. Its whole budget is injected into TileMassField at the burst cell, and the
//      spill cascade grows the "splash" mound outward from there. This used to call
//      EruptionPlanner.Plan with a single zero-velocity sample; the mass field reaches
//      the same shape by flowing instead of by scoring cells and top-K'ing them.
//      In mid-air there is nothing to support a sprout, so the mass seeks terrain or
//      dies out — a clod that bursts on a player leaves no mound hanging in the sky.
//   2. One-shot area damage is published — a core hit at the center (the body actually
//      struck) plus radial segments around it, similar to StickyGrenade, so anything
//      caught in the splash takes a hit too.
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
// burst-on-contact life above takes over with whatever budget survived the carry. The contact fuse is live through the chase as well, so a point-blank throw
// bursts on its target instead of phasing through it while still catching up.
// Detach velocity ≈ the point's hand-off velocity, capped — the same number whether
// the clod was in hand or in the ground when the button came up.
//
// The deposit needs a ChunkMap reference, which Entity.Update doesn't normally surface.
// We grab it through ctx.Spawner.Chunks via an extra hook on IEntitySpawner — adding
// the property keeps the entity sandbox clean (no static ChunkMap reach-arounds).
public class LobbedAreaProjectile : Projectile
{
    private const float LifeSeconds       = 5.0f;
    // Float-noise rejection: the solver leaves LastImpulseMagnitude at exactly 0 on a
    // step with no terrain contact at all.
    private const float ContactImpulseEps = 0.01f;
    // A contact this hard is a STRIKE, not weight. A clod merely resting or sliding on
    // terrain absorbs one frame of gravity per step — 600·(1/60) = 10 px/s, measured —
    // while a thrown one arrives carrying its whole throw speed, a few hundred. Sitting
    // safely above the resting figure is what lets the fuse fire on a wall the clod was
    // already scraping along without firing on the ground it was lifted off.
    private const float StrikeImpulse     = 45f;
    // "Effectively stopped": a clod that touched down gently never reaches StrikeImpulse,
    // so this is the second way to burst — it has arrived rather than been hit. Also
    // picks the core hit's knockback direction.
    private const float StillSpeed        = 30f;
    private const float ArmDelay          = 0.04f;
    private const float ExplosionRadius   = 3f * Chunk.TileSize;
    private const int   ExplosionSegments = 10;
    private const float SegmentHalfSize   = 9f;
    private const float ExplosionKnockback = 320f;
    private const float ExplosionDamage   = TileDamage.TileMaxHP * 0.8f;
    // The core hit, at the blast's center — the radial segments ring the center at
    // ExplosionRadius, so without this the body actually struck (which sits AT the
    // center) is the one thing the explosion misses. Half-size tracks the drawn orb so
    // the contact fuse and the damage it deals cover the same region, floored so a
    // nearly-crumbled clod still connects.
    private const float CoreMinHalfSize   = 10f;
    // ── Charged clods (World/TileCharge.cs) ──────────────────────────────────────
    // A charged tile costs a whole avalanche meter — two seconds of committed holding —
    // so a clod with even one in it is a deliberately expensive object and the payoff
    // has to read as such. Damage leads, knockback and reach follow more gently: the
    // point is that a charged clod BREAKS things, not that it launches them to orbit.
    private const float ChargedDamagePerBlock    = 1.5f;   // ×2.5 at one, ×4 at two
    private const float ChargedKnockbackPerBlock = 0.6f;
    private const float ChargedRadiusPerBlock    = 0.35f;
    // Cap on how many charged tiles one clod can cash in. A grab that happened to peel
    // a whole charged wall shouldn't produce a screen-clearing blast — the fantasy is a
    // demolition charge, not a nuke.
    private const int   ChargedMax               = 4;
    // How far a charged clod's orb is pulled toward white. Same language as the tile
    // tint in ChunkRenderer, so "this thing is charged" reads identically on the ground
    // and in the air.
    private const float ChargedTint              = 0.7f;
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
    // Charged tiles that went into this clod (grab path only). Fixed at break-out —
    // the bleed eats Budget, never this, so a clod carried until it is nearly gone
    // still detonates like the charge it was packed with.
    private int      _charged;
    private float    _carryTime;
    private float    _chaseTime;

    public override EntityKind Kind => EntityKind.LobbedArea;

    public bool     Tracking      => _tracking;
    public EntityId PointId       => _pointId;
    public int      Budget        => _budget;
    public int      HarvestBlocks => _harvest;
    public int      ChargedBlocks => _charged;
    // Charged tiles this clod will actually cash in — the raw count, capped.
    public int      EffectiveCharge => Math.Min(_charged, ChargedMax);
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
        s.ChargedBlocks = _charged;
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
        _charged   = s.ChargedBlocks;
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
        Sprite        = new MassOrbSprite(tileType, Sprites.Ball(BodyRadius).Pose, BodyRadius);
        _budget       = budget;
        _harvest      = budget;
        _tileType     = tileType;
        _hitId        = hitId;
    }

    // A block grab's ball: born at rest, weightless, following `point` until detach.
    // `charged` is how many of the harvested tiles were charged — the peel counts them
    // before it breaks the cells, since BreakCell clears the flag.
    public static LobbedAreaProjectile MakeTracking(Vector2 pos, int blocks, TileType tileType, int hitId, Faction owner, EntityId point, int charged = 0)
    {
        var ball = new LobbedAreaProjectile(pos, Vector2.Zero, blocks, tileType, hitId, owner)
        {
            _tracking    = true,
            _pointId     = point,
            _charged     = charged,
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
        if (_tracking) { Track(dt, hitboxes, spawner); return; }

        if (_detonated) { Health = 0f; return; }
        if (Age < ArmDelay) return;

        // Fuse 1 — contact. A thrown clod bursts on the first body it touches instead
        // of sailing through it to land somewhere behind. Hurtboxes are published
        // before the entity pass, so this is the current frame's set (Simulation.Step).
        if (TouchesBody(spawner?.Hurtboxes)) { Detonate(hitboxes, spawner); return; }

        // Fuse 2 — terrain. In free flight a clod bursts on a hard contact OR once it
        // has come to rest against terrain (a gentle set-down never strikes hard).
        if (TerrainStruck(settleCounts: true)) Detonate(hitboxes, spawner);
    }

    // Is any body we're allowed to hurt inside the clod right now? Measured on the
    // DRAWN radius, not the small collision disc, so the burst fires when the orb
    // visibly meets the target. Own faction is excluded, so a thrower can't set off
    // their own throw (and two clods of one player pass through each other).
    private bool TouchesBody(HurtboxWorld hurtboxes)
    {
        if (hurtboxes == null) return false;
        float r = DrawRadius;
        var p = Body.Position;
        var region = new BoundingBox(p.X - r, p.Y - r, p.X + r, p.Y + r);
        foreach (var hb in hurtboxes.Overlapping(region, exclude: Faction))
            if (hb.Target != Id) return true;
        return false;
    }

    // Did terrain just hit us, and hard enough to break the clod? PhysicsWorld reports
    // the normal velocity it had to absorb at every contact during the last StepSwept,
    // so this reads both "was I touching something solid" and "how hard". Entities
    // update before the physics step, so it is the previous step's contact: one frame
    // of lag, and still far earlier than waiting for the thing to stop moving.
    //
    // Both paths require an ACTUAL contact, which is why this can't misfire the way the
    // velocity-halt heuristic it replaced did — that one also read "landed" at the apex
    // of a high lob, where speed passes through zero in open air.
    //
    // `settleCounts` is off during the post-release chase: a clod there is slow because
    // it hasn't been accelerated up to throw speed yet, not because it has arrived, and
    // one grabbed off the floor is often still touching the floor at that moment. Only
    // a real strike breaks it mid-chase.
    private bool TerrainStruck(bool settleCounts)
    {
        float impulse = Body.LastImpulseMagnitude;
        if (impulse <= ContactImpulseEps) return false;
        return impulse >= StrikeImpulse
            || (settleCounts && Body.Velocity.LengthSquared() < StillSpeed * StillSpeed);
    }

    // The burst, shared by both fuses so a clod that hits a player reads the same as
    // one that hits the ground: the mound goes in at the site, the core hit lands on
    // whatever is at the center, and the radial segments catch everything around it.
    private void Detonate(HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        // Charged tiles in the clod scale the whole burst. One is a big hit; the cap
        // keeps a lucky harvest of a charged wall from clearing the screen.
        int   charged = EffectiveCharge;
        float dmgMul  = 1f + ChargedDamagePerBlock    * charged;
        float kbMul   = 1f + ChargedKnockbackPerBlock * charged;
        float radMul  = 1f + ChargedRadiusPerBlock    * charged;

        // 1) Eruption mound at the burst site — but ONLY for an ordinary clod. Chunks
        // come from the spawner (Game1 implements both IEntitySpawner and
        // IChunkProvider). Mid-air, the mass field has nothing to support a sprout, so
        // it seeks terrain or dies out — a body hit doesn't leave a mound hanging in
        // the sky.
        //
        // A charged clod spends its material on the blast instead: it detonates rather
        // than splatting, so it leaves a crater where a plain one leaves a mound. That
        // is the trade the player buys with the meter, and it is the one rule that
        // makes "charged" legible at the moment of impact — the same object either
        // builds or destroys depending on what went into it.
        var chunks = (spawner as IChunkProvider)?.Chunks;
        if (chunks != null && _budget > 0 && charged == 0)
        {
            // Material was captured at launch, so the mound is made of whatever the
            // player had selected when they threw.
            int gtx = (int)MathF.Floor(Body.Position.X / Chunk.TileSize);
            int gty = (int)MathF.Floor(Body.Position.Y / Chunk.TileSize);
            chunks.Mass.Deposit(chunks, gtx, gty, _budget, _tileType);
        }

        // 2) AOE damage — a core hit plus the radial-shove segments StickyGrenade uses.
        // All of it shares _hitId, so CombatSystem's (HitId, Target) dedupe gives each
        // victim exactly one hit: the body at the center takes the core, everyone else
        // takes their segment.
        if (hitboxes != null)
        {
            var center = Body.Position;
            var vel    = Body.Velocity;
            // Core knockback follows the throw — a clod caught in the chest shoves you
            // the way it was travelling. A clod that stopped (the landing fuse) pops
            // upward instead of nowhere.
            var coreDir = vel.LengthSquared() > StillSpeed * StillSpeed
                ? Vector2.Normalize(vel)
                : new Vector2(0f, -1f);
            float core = MathF.Max(CoreMinHalfSize, DrawRadius);
            // A plain clod's core is EntitiesOnly: the segments already carry the tile
            // damage, and the center cell is where the mound above is being deposited —
            // chipping it here would just fight the deposit. A charged clod deposits
            // nothing, so that reason is gone and the core cuts terrain too, which is
            // what actually punches the crater at the point of impact.
            hitboxes.Publish(new Hitbox(
                new BoundingBox(center.X - core, center.Y - core, center.X + core, center.Y + core),
                _hitId, ExplosionDamage * dmgMul,
                coreDir * (ExplosionKnockback * kbMul),
                Faction, Id, Color.Goldenrod,
                targets: charged > 0 ? HitTargets.All : HitTargets.EntitiesOnly,
                origin: center));

            // The ring grows with the charge, and the segments grow with it — the boxes
            // are spaced 2πR/N apart, so scaling R alone would just open gaps in the
            // blast and let bodies stand between the segments of a bigger explosion.
            float ringRadius = ExplosionRadius * radMul;
            float segHalf    = SegmentHalfSize * radMul;
            for (int i = 0; i < ExplosionSegments; i++)
            {
                float angle = i * MathHelper.TwoPi / ExplosionSegments;
                var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var segCenter = center + dir * ringRadius;
                var region = new BoundingBox(
                    segCenter.X - segHalf, segCenter.Y - segHalf,
                    segCenter.X + segHalf, segCenter.Y + segHalf);
                hitboxes.Publish(new Hitbox(
                    region, _hitId, ExplosionDamage * dmgMul,
                    dir * (ExplosionKnockback * kbMul),
                    Faction, Id, Color.Goldenrod,
                    origin: center));
            }
        }
        _detonated = true;
        // Presentation: the burst splash (particles/audio) is edge-triggered, so it
        // goes out as a sim event keyed by this entity's id — the render shell dedupes
        // it against rollback replays (Presentation/PresentationEvents.cs).
        spawner?.NotifyMassLanded(Id, Body.Position, _tileType, _budget);
    }

    // One frame of following the pulling point. Velocity-matching (critically damped),
    // so the ball settles to the point's velocity rather than to Vmax or zero — which
    // is what makes the detach speed the swipe speed (plan §4.1).
    private void Track(float dt, HitboxWorld hitboxes, IEntitySpawner spawner)
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

            // Already thrown, just not yet up to speed — so both fuses are live here
            // too. Without this a clod thrown at a wall or a body within the chase
            // window (a quarter second: point-blank range) neither bursts nor detaches
            // — the tracker just presses it against the wall until the chase times
            // out, and it drops to the floor. Checked BEFORE the tracker runs, so the
            // velocity the core hit reads is the one the impact left, not the one the
            // tracker is about to write back toward the point.
            if (TouchesBody(spawner?.Hurtboxes) || TerrainStruck(settleCounts: false))
            {
                Detach();
                Detonate(hitboxes, spawner);
                return;
            }
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

    // Drawn radius, for the sprite and the trail: tracks the blocks left.
    public float DrawRadius => MathF.Max(2f, OrbMaxRadius * MathF.Sqrt(MathF.Min(1f, _budget / OrbMaxBlocks)));

    // Drawn size tracks the blocks left, tinted by material, rolling with its travel —
    // the held clod and the thrown one are the same object, so they can't disagree.
    public override void SyncSprite()
    {
        base.SyncSprite();
        if (Sprite is not MassOrbSprite orb) return;
        orb.Radius = DrawRadius;
        orb.Tint   = _charged > 0
            ? Color.Lerp(TilePalette.BaseColor(_tileType), Color.White, ChargedTint)
            : TilePalette.BaseColor(_tileType);
        orb.Spin   = Body.Velocity.X / orb.Radius;   // rolling: ω = v/r, sign from direction
    }
}

// Sidecar interface so a projectile can reach the chunk map through the same
// spawner reference it already has. Game1 implements this; tests can leave
// IEntitySpawner.Chunks null without consequence.
public interface IChunkProvider
{
    ChunkMap Chunks { get; }
}
