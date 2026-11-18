using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Roadmap §4.3 — ballistic projectile launched by LobbedAreaAction with a
// charge-time-derived budget, and (Plans/BLOCK_THROW_PLAN.md) the thrown clod of a
// block grab. It bursts on either of two fuses — touching a body it can hurt, or
// touching terrain — and the burst is the same either way.
//
// NO TERRAIN PHYSICS (2026-11-16). The clod is a sensor, not a collider: Body.IgnoreTiles
// is always on, and every frame it publishes a SENSOR hitbox shaped like the drawn orb (a 12-gon),
// swept over the travel it is about to make (Hitbox.Sensor). CombatSystem reports the
// first terrain or body it touches back through IContactSensor during Apply; a terrain
// touch pins the clod at the contact (before physics moves it on), and the next update
// bursts. A body touch leaves it flying, so it bursts inside the body it reached — the
// core hit's center. It used to collide through the solver on a 5 px disc and burst on the
// contact impulse, which had two visible faults: the drawn orb (up to ~11 px, a whole
// tile) sank half a tile into the face before the disc touched, and only the
// face-normal component of the hit counted, so a glancing throw skidded along a floor
// and burst wherever it slowed down. The burst itself:
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
// after release. While tracking: gravity off, no hurtbox, and the budget bleeds while
// the point is held. Terrain contact never bursts a HELD clod (it passes through what
// it is dragged across), but touching a body it can hurt does — the same burst a
// thrown one makes (the held fuse in Track). The tracker
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
public class LobbedAreaProjectile : Projectile, IContactSensor
{
    private const float LifeSeconds       = 5.0f;
    // Below this speed the core hit pops upward instead of along the (meaningless)
    // direction of travel.
    private const float StillSpeed        = 30f;
    private const float ExplosionRadius   = 48f;   // px, tile-size independent
    private const int   ExplosionSegments = 10;
    private const float SegmentHalfSize   = 9f;
    private const float ExplosionKnockback = 320f;
    // The core (the body actually struck) gets a real shove — 700 impulse is Δv 280 on
    // a player, vs the ring's 128 — and every burst hit a short fixed freeze (4f).
    // Derived hitstop has no small setting: zero under BigHitStrength, ≥ 8f over it.
    private const float CoreKnockback      = 700f;
    private const float BurstHitstopSeconds = CombatState.MinHitstopSeconds;
    // The core LAUNCHES (tumble, no dash-out, tech to escape) — GroundSlash3's decay:
    // Δv 280 bleeds out over ~112 px. The ring stays a plain shove.
    private const float CoreLaunchDecay    = 350f;
    // TILE damage of the burst (what it chips off terrain). Flat: a clod's terrain bite
    // doesn't grow with its size — the mound it deposits does.
    private const float ExplosionDamage   = TileDamage.TileMaxHP * 0.8f;
    // BODY damage scales with what's in the clod, and charge counts twice:
    //   (BodyDamagePerBlock · (plain + ChargedBlockWeight · charged)) · (1 + ChargedBodyMul · charged)
    // Additive — a charged block is worth ChargedBlockWeight plain ones in the sum —
    // and multiplicative — each one also scales the whole clod. The carry bleed eats
    // plain blocks, never charged ones. With the player's 5 HP:
    //   4 plain 0.8 · 9 plain 1.8 · 1c+4p 2.56 · 2c+4p 5.28 · 3c alone 6.72 ·
    //   3c+2p 7.84 · 3c+4p 8.96 · 4c+5p 14.3 · 6c+3p 25.0   (uncapped)
    // No guard special case: a perfect parry still absorbs it, but a held guard leaks
    // up to 75% (CombatState.ResolveGuard), so anything over ~6.7 kills through one —
    // which three charged blocks always clear.
    private const float BodyDamagePerBlock = 0.2f;
    private const float ChargedBlockWeight = 4f;
    private const float ChargedBodyMul     = 0.6f;
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
    private const float ChargedDamagePerBlock    = 1.5f;   // TILE damage: ×2.5 at one, ×4 at two
    private const float ChargedKnockbackPerBlock = 0.6f;
    private const float ChargedRadiusPerBlock    = 0.35f;
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
    // Sensor contact (OnSensorContact) awaiting its burst on the next update.
    private bool     _contactPending;
    private bool     _contactBody;
    private Vector2  _contactVel;

    public override EntityKind Kind => EntityKind.LobbedArea;

    public bool     Tracking      => _tracking;
    public EntityId PointId       => _pointId;
    public int      Budget        => _budget;
    public int      HarvestBlocks => _harvest;
    public int      ChargedBlocks => _charged;
    public TileType TileType      => _tileType;
    // Has this clod burst? Read-only, for tests and probes.
    public bool     Detonated     => _detonated;
    // Touched something and bursts on the next update (pinned at the face if terrain).
    public bool     ContactPending => _contactPending;

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
        s.ContactPending  = _contactPending;
        s.ContactBody     = _contactBody;
        s.ContactVelocity = _contactVel;
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
        _contactPending = s.ContactPending;
        _contactBody    = s.ContactBody;
        _contactVel     = s.ContactVelocity;
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
        // Never a terrain collider (see the header). Set every frame rather than once so
        // no rehydrate or body-reset path can quietly hand the solver back.
        Body.IgnoreTiles = true;
        if (_detonated) { Health = 0f; return; }

        // Last frame's sensor touched something: burst here. A terrain touch already
        // pinned the body at the face; the velocity it arrived with aims the core hit.
        // A tracking clod detaches first, so the point and the action see it leave the
        // hand this frame (the point dies, the action ends and hands off).
        if (_contactPending)
        {
            _contactPending = false;
            if (_tracking) Detach();
            Body.Velocity = _contactVel;
            Detonate(hitboxes, spawner, bodyHit: _contactBody);
            Body.Velocity = Vector2.Zero;
            return;
        }

        bool held = false;
        if (_tracking)
        {
            held = Track(dt, spawner);
            if (Health <= 0f) return;
        }
        PublishSensor(dt, hitboxes, held);
    }

    // The fuse: a sensor shaped like the drawn orb, swept over the travel the physics
    // step is about to make (so even a small, fast clod can't skip a tile or a body).
    // CombatSystem applies the faction / team / co-op gates, so a thrower never sets off
    // their own throw and a clod passes a co-op teammate. A HELD clod senses bodies only
    // — it scrapes through terrain while the player is still aiming it; thrown (chasing
    // or free) it senses both.
    private void PublishSensor(float dt, HitboxWorld hitboxes, bool held)
    {
        if (hitboxes == null) return;
        float r = DrawRadius;
        var   p = Body.Position;
        // The orb's outline: a regular polygon on the drawn radius (vertices at the
        // compass points, so it reaches exactly r straight down/up/sideways). Rebuilt
        // only when the radius changes; derived from the budget, so not snapshot state.
        if (_sensorShape == null || _sensorShapeRadius != r)
        {
            _sensorShape       = Polygon.CreateRegular(r, SensorSides);
            _sensorShapeRadius = r;
        }
        hitboxes.Publish(new Hitbox(
            new BoundingBox(p.X - r, p.Y - r, p.X + r, p.Y + r),
            _hitId, 0f, Vector2.Zero, Faction, Id, Color.Sienna,
            targets:  held ? HitTargets.EntitiesOnly : HitTargets.All,
            shape:    _sensorShape,
            shapePos: p,
            sweep:    Body.Velocity * dt,
            sensor:   true));
    }

    private const int SensorSides = 12;
    private Polygon _sensorShape;
    private float   _sensorShapeRadius;

    // CombatSystem, mid-Apply. Only state changes here; the burst publishes its hitboxes
    // on the next update. Terrain pins the clod at the contact so it never shows past
    // the face; a body touch keeps it flying into the body it reached.
    public void OnSensorContact(in Hitbox sensor, in SensorContact contact)
    {
        if (_detonated || _contactPending) return;
        _contactPending = true;
        _contactBody    = !contact.IsTerrain;
        _contactVel     = Body.Velocity;
        if (contact.IsTerrain)
        {
            Body.Position = contact.Center;
            Body.Velocity = Vector2.Zero;
        }
    }

    // The burst, shared by both fuses so a clod that hits a player reads the same as
    // one that hits the ground: the mound goes in at the site, the core hit lands on
    // whatever is at the center, and the radial segments catch everything around it.
    private void Detonate(HitboxWorld hitboxes, IEntitySpawner spawner, bool bodyHit)
    {
        // Charged tiles in the clod scale the whole burst, uncapped: a clod packed
        // with a whole charged wall is a nuke, and the player paid for every block.
        int   charged = _charged;
        float dmgMul  = 1f + ChargedDamagePerBlock    * charged;
        float kbMul   = 1f + ChargedKnockbackPerBlock * charged;
        float radMul  = 1f + ChargedRadiusPerBlock    * charged;
        // Only a grabbed clod (HarvestBlocks > 0) scales; the charge-lob's budget runs to
        // 50 and keeps the old flat hit.
        float bodyDmg = _harvest > 0 ? BodyDamage(_budget, charged) : ExplosionDamage * dmgMul;

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
            // The mound belongs to the thrower (an enemy lob's faction maps to nobody).
            chunks.Mass.Deposit(chunks, gtx, gty, _budget, _tileType,
                                owner: BlockOwnership.OwnerFor(Faction));
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
                coreDir * (CoreKnockback * kbMul),
                Faction, Id, Color.Goldenrod,
                targets: charged > 0 ? HitTargets.All : HitTargets.EntitiesOnly,
                bodyDamage: bodyDmg,
                origin: center,
                launchDecay: CoreLaunchDecay,
                hitstopSecondsOverride: BurstHitstopSeconds));

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
                    bodyDamage: bodyDmg,
                    origin: center,
                    hitstopSecondsOverride: BurstHitstopSeconds));
            }
        }
        _detonated = true;
        // Presentation: the burst splash (particles/audio) is edge-triggered, so it
        // goes out as a sim event keyed by this entity's id — the render shell dedupes
        // it against rollback replays (Presentation/PresentationEvents.cs).
        spawner?.NotifyMassLanded(Id, Body.Position, _tileType, _budget, bodyHit);
    }

    // Body damage of a burst holding `blocks` blocks (the live budget, after any carry
    // bleed), `charged` of them charged (capped, never bled). Charged blocks are part
    // of the harvest, so they're moved from the plain count to the weighted one; a
    // carry that bled the budget below the charge still pays the charge in full. See
    // the constants for the curve.
    public static float BodyDamage(int blocks, int charged)
    {
        int plain = Math.Max(0, blocks - charged);
        return BodyDamagePerBlock * (plain + ChargedBlockWeight * charged) * (1f + ChargedBodyMul * charged);
    }

    // One frame of following the pulling point. Velocity-matching (critically damped),
    // so the ball settles to the point's velocity rather than to Vmax or zero — which
    // is what makes the detach speed the swipe speed (plan §4.1).
    // Returns whether the point is still in the player's hand (the held fuse).
    private bool Track(float dt, IEntitySpawner spawner)
    {
        var cfg   = MovementConfig.Current;
        var point = spawner?.Resolve(_pointId) as PullPointEntity;
        if (point == null || point.IsDead) { Detach(); return false; }

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
            if (_budget <= 0) { Health = 0f; return true; }

            // Held fuse: terrain never sets a held clod off (it scrapes and slides —
            // the player is still aiming it), but swinging it into someone who can be
            // hurt does, through exactly the thrown clod's burst (PublishSensor senses
            // bodies only while held).
        }
        else
        {
            // Already thrown, just not yet up to speed — both fuses are live here too:
            // a clod thrown at a wall or a body within the chase window (a quarter
            // second: point-blank range) must burst there, not phase through.
            smoothTime = cfg.GrabChaseSmoothTime;
            _chaseTime += dt;
        }

        // Tracker on a copy; only the velocity goes back — physics moves the body.
        var pos = Body.Position;
        var vel = Body.Velocity;
        SmoothPen.CriticallyDampedStep(ref pos, ref vel, point.Body.Position, smoothTime, dt);
        float speed = vel.Length();
        if (speed > cfg.GrabBallMaxSpeed) vel *= cfg.GrabBallMaxSpeed / speed;
        Body.Velocity = vel;

        if (point.Driven) return true;

        if ((vel - point.Body.Velocity).Length() < cfg.GrabCatchSpeed
            || _chaseTime >= cfg.GrabChaseMaxSeconds)
            Detach();
        return false;
    }

    // Let go of the point: a free ballistic lob from here.
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
