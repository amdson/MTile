using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  ASPID — a Primal Aspid (Hollow Knight) for combat testing.
// ─────────────────────────────────────────────────────────────────────────────
//
// A slow, floaty flier that holds a stand-off distance diagonally above the
// player and spits fans of three slow fireballs. It never closes to melee; the
// fight is about getting UNDER its volley and into its face, while the fan
// covers the ground you'd dodge onto. Several of them spread out rather than
// stacking, so a group layers its fans from different angles.
//
// Test stage: "Stage": "aspid" in configs/game_config.json (Levels/aspid.json).
//
// The split follows TemplateEnemy.cs:
//
//   Brain  (AspidController)   — WHERE to hover: a point PreferredRange from the
//                                player at ElevationDeg above horizontal, on
//                                whichever side the aspid is already on. Steers
//                                there with an arrive falloff, plus separation
//                                from other enemies and a push off nearby rock.
//   Legs   (AspidHoverState)   — a low-acceleration flight state. Low accel is
//                                what makes it floaty: knockback plays out and
//                                the volley's recoil visibly shoves it back.
//   Arms   (AspidVolleyAction) — windup (aim tracks, then freezes) → three
//                                fireballs in a fan → a long recovery that IS
//                                the fire cooldown (controllers are stateless,
//                                so there is nowhere else to keep one).
//
// Unlike the Shrike, the hover keeps flying while an attack is committed: the
// aspid drifts and repositions through its whole volley cycle, and the action's
// recovery (its cooldown) would otherwise freeze it for ~2s at a time.
// ─────────────────────────────────────────────────────────────────────────────


// ── 1. THE BRAIN ────────────────────────────────────────────────────────────
public sealed class AspidController : EnemyController
{
    // Centre-to-centre stand-off. The volley's MaxRange must sit comfortably
    // outside this or an aspid holding station can't shoot.
    public float PreferredRange   { get; init; } = 110f;
    // Hover angle above the horizontal on the aspid's side of the player. 90 is
    // straight overhead; ~40 keeps it where the fan sweeps the floor in front of
    // the player rather than raining straight down on them.
    public float ElevationDeg     { get; init; } = 40f;
    // Inside this of the hover point, ease off (arrive) instead of overshooting.
    public float SlowRadius       { get; init; } = 40f;
    // Beyond this the aspid idles in place and holds fire.
    public float AlertRange       { get; init; } = 320f;

    // Other enemies closer than this (centre to centre) push the aspid away,
    // scaled linearly from 0 at the edge to SeparationWeight at contact.
    public float SeparationRadius { get; init; } = 52f;
    public float SeparationWeight { get; init; } = 1.6f;

    // Solid cells within this of the body centre push it off, the same way.
    // Keeps it from grinding along a wall or ceiling on the way to its spot.
    public float AvoidRadius      { get; init; } = 24f;
    public float AvoidWeight      { get; init; } = 1.2f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var   self   = ctx.Self.Body.Position;
        var   target = ctx.Target.Position;
        var   chunks = ctx.Spawner?.Chunks;
        float dist   = ctx.Dist;
        bool  alert  = dist <= AlertRange;

        Vector2 steer = Vector2.Zero;

        if (alert)
        {
            var anchor = PickAnchor(self, target, chunks);
            var to     = anchor - self;
            float d    = to.Length();
            if (d > 1e-3f)
                steer += to / d * MathF.Min(1f, d / MathF.Max(SlowRadius, 1e-3f));
        }

        steer += Separation(in ctx) * SeparationWeight;
        steer += TerrainPush(self, chunks) * AvoidWeight;

        if (steer.LengthSquared() > 1f) steer.Normalize();

        bool canSee = EnemyAim.HasLineOfSight(self, target, chunks, ctx.Self.Body.Bounds.Width * 0.5f + 2f);

        return new EnemyInput
        {
            MoveDir    = steer,
            Jump       = false,
            AimWorld   = target,
            WantAttack = alert && canSee,
        };
    }

    // The hover point: PreferredRange out at ElevationDeg on the aspid's current
    // side. If that spot is inside rock or can't see the player (player hugging
    // a wall, ducked under a ledge) fall back to the mirrored side, then to
    // straight overhead. Deterministic order, pure function of terrain.
    private Vector2 PickAnchor(Vector2 self, Vector2 target, ChunkMap chunks)
    {
        int   side = self.X >= target.X ? 1 : -1;
        float a    = MathHelper.ToRadians(ElevationDeg);
        var   off  = new Vector2(MathF.Cos(a), -MathF.Sin(a)) * PreferredRange;

        var same  = target + new Vector2(side * off.X, off.Y);
        if (Usable(same, target, chunks)) return same;
        var other = target + new Vector2(-side * off.X, off.Y);
        if (Usable(other, target, chunks)) return other;
        var over  = target + new Vector2(0f, -PreferredRange);
        if (Usable(over, target, chunks)) return over;
        return same;
    }

    private static bool Usable(Vector2 p, Vector2 target, ChunkMap chunks)
        => chunks == null
        || (!TileQuery.IsSolidAt(chunks, p.X, p.Y) && EnemyAim.HasLineOfSight(p, target, chunks, 0f));

    // Push away from every other live enemy inside SeparationRadius. Read off
    // this frame's hurtboxes (published in canonical spawn order before any
    // entity updates), so the sum is order-stable across rollback replays.
    private Vector2 Separation(in EnemyContext ctx)
    {
        var hurt = ctx.Spawner?.Hurtboxes;
        if (hurt == null) return Vector2.Zero;

        var   self = ctx.Self.Body.Position;
        float r    = SeparationRadius;
        Vector2 push = Vector2.Zero;
        foreach (var hb in hurt.All)
        {
            if (hb.Owner != Faction.Enemy || hb.Target == ctx.Self.Id) continue;
            if (ctx.Spawner.Resolve(hb.Target) is not EnemyEntity other || other.IsDead) continue;

            var   away = self - other.Body.Position;
            float d    = away.Length();
            if (d >= r) continue;
            // Exactly coincident: break the tie on id so two stacked spawns split
            // apart instead of cancelling out.
            if (d < 1e-3f) { away = new Vector2(ctx.Self.Id.Index < hb.Target.Index ? -1f : 1f, 0f); d = 1f; }
            push += away / d * (1f - d / r);
        }
        return push;
    }

    // Push away from solid cells whose centre is within AvoidRadius. Row-major
    // over a fixed window, so the sum is a pure function of terrain.
    private Vector2 TerrainPush(Vector2 self, ChunkMap chunks)
    {
        if (chunks == null) return Vector2.Zero;
        float r  = AvoidRadius;
        int   x0 = (int)MathF.Floor((self.X - r) / Chunk.TileSize);
        int   x1 = (int)MathF.Floor((self.X + r) / Chunk.TileSize);
        int   y0 = (int)MathF.Floor((self.Y - r) / Chunk.TileSize);
        int   y1 = (int)MathF.Floor((self.Y + r) / Chunk.TileSize);

        Vector2 push = Vector2.Zero;
        for (int gy = y0; gy <= y1; gy++)
        for (int gx = x0; gx <= x1; gx++)
        {
            if (chunks.GetCellState(gx, gy) != TileState.Solid) continue;
            var   c    = new Vector2((gx + 0.5f) * Chunk.TileSize, (gy + 0.5f) * Chunk.TileSize);
            var   away = self - c;
            float d    = away.Length();
            if (d >= r || d < 1e-3f) continue;
            push += away / d * (1f - d / r);
        }
        if (push.LengthSquared() > 1f) push.Normalize();
        return push;
    }
}


// ── 2. THE LEGS ─────────────────────────────────────────────────────────────
// EnemyFlyState with two changes: MoveDir's MAGNITUDE is honoured (so the
// brain's arrive falloff actually slows it), and it keeps flying while an
// attack is committed (see the file header).
public class AspidHoverState : EnemyFlyState
{
    // Slow and floaty. At 240 px/s² a 60 px/s cruise takes a quarter second to
    // reach, and a slash's knockback coasts for a good half second.
    protected override float MaxAcceleration => 240f;
    protected override float CruiseSpeed     => 60f;

    public override bool CheckPreConditions(in EnemyContext ctx) => true;
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v) => true;

    // A slow vertical bob layered on top, so a hovering aspid never reads as
    // pinned in place. Keyed off TimeInState, NOT ctx.Frame: EnemyEntity's frame
    // counter isn't snapshotted, while TimeInState is — and this state is active
    // from the aspid's first frame on, so it doubles as the aspid's age.
    protected virtual float BobSpeed  => 18f;     // px/s peak
    protected virtual float BobPeriod => 1.6f;

    protected override Vector2 DesiredVelocity(in EnemyContext ctx, in EnemyMovementVars v)
    {
        var move = ctx.Input.MoveDir;
        float len = move.Length();
        if (len > 1f) move /= len;
        var vel = move * CruiseSpeed;
        vel.Y += BobSpeed * MathF.Sin(v.TimeInState * MathHelper.TwoPi / BobPeriod);
        return vel;
    }
}


// ── 3. THE ARMS ─────────────────────────────────────────────────────────────
// Windup → three fireballs in a fan → recovery (the cooldown).
//
// The aim TRACKS the player through the first part of the windup, then freezes
// for the last AimLockSeconds. The tell therefore shows the real firing line
// for long enough to step out of it, while an aspid you've circled round
// doesn't fire at where you were half a second ago. LockedAim is snapshotted
// (EntityData.Aim), so the tracking is rollback-safe.
public class AspidVolleyAction : EnemyActionState
{
    protected virtual float Windup          => 0.70f;
    protected virtual float Active          => 0.05f;
    // The cooldown. With Windup this is one volley every ~2.6s.
    protected virtual float Recovery        => 1.85f;
    protected virtual float AimLockSeconds  => 0.25f;

    protected virtual float MinRange        => 36f;
    protected virtual float MaxRange        => 240f;

    protected virtual int   Count           => 3;
    protected virtual float SpreadDeg       => 17f;    // between adjacent shots
    protected virtual float ShotSpeed       => 105f;   // px/s — slow, readable
    protected virtual float MuzzleOffset    => 12f;
    // Kick back along -aim at the moment of firing.
    protected virtual float RecoilSpeed     => 70f;

    protected virtual Color ChargeColor     => new(255, 150, 50);

    public override int ActivePriority  => 28;   // Ranged band
    public override int PassivePriority => 22;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= MinRange && ctx.Dist <= MaxRange;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.LockedAim    = EnemyAim.AimAt(ctx.Input.AimWorld - ctx.Self.Body.Position, v.LockedFacing);
        // One HitId for the whole fan, so a player caught by two of the three
        // fireballs at once takes one hit, not two.
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prevT = v.TimeInState;
        v.TimeInState += ctx.Dt;

        if (v.TimeInState < v.WindupDuration - AimLockSeconds)
            v.LockedAim = EnemyAim.AimAt(ctx.Input.AimWorld - ctx.Self.Body.Position, v.LockedFacing);

        if (prevT < v.WindupDuration && v.TimeInState >= v.WindupDuration)
            Fire(in ctx, in v);
    }

    private void Fire(in EnemyContext ctx, in EnemyActionVars v)
    {
        var body   = ctx.Self.Body;
        var aim    = v.LockedAim;
        float step = MathHelper.ToRadians(SpreadDeg);
        float mid  = (Count - 1) * 0.5f;

        for (int i = 0; i < Count; i++)
        {
            var dir = Rotate(aim, (i - mid) * step);
            ctx.Spawner?.SpawnEntity(new AspidFireballProjectile(
                body.Position + dir * MuzzleOffset, dir * ShotSpeed, v.HitId, ctx.Self.Faction)
                { Team = ctx.Self.Team });
        }

        body.Velocity -= aim * RecoilSpeed;
    }

    private static Vector2 Rotate(Vector2 v, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    // Windup: an ember swelling at the muzzle and three faint guide rays along
    // the fan. The rays go solid when the aim locks — that's the "move now" cue.
    // Fire: a short flash that bleeds into recovery.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        const float FlashSeconds = 0.15f;
        float time = v.TimeInState;
        var   aim  = v.LockedAim.LengthSquared() > 1e-4f ? v.LockedAim : new Vector2(v.LockedFacing, 0f);
        var   muzzle = body.Position + aim * MuzzleOffset;

        if (time >= v.WindupDuration)
        {
            if (time < v.WindupDuration + FlashSeconds)
            {
                float fa = 1f - (time - v.WindupDuration) / FlashSeconds;
                t.Disc(muzzle, 3f + 4f * fa, new Color(255, 230, 150) * fa);
            }
            return;
        }

        float p      = time / MathF.Max(v.WindupDuration, 1e-3f);
        bool  locked = time >= v.WindupDuration - AimLockSeconds;

        t.Disc(muzzle, 1.5f + 3f * p, Color.Lerp(new Color(ChargeColor, 80), ChargeColor, p));
        t.Ring(muzzle, 7f - 4f * p, ChargeColor * (0.3f + 0.7f * p), 10, 1f);

        float step  = MathHelper.ToRadians(SpreadDeg);
        float mid   = (Count - 1) * 0.5f;
        float len   = 18f + 30f * p;
        var   ray   = locked ? ChargeColor * 0.75f : ChargeColor * (0.12f + 0.2f * p);
        for (int i = 0; i < Count; i++)
        {
            var dir = Rotate(aim, (i - mid) * step);
            t.Line(muzzle, muzzle + dir * len, ray, locked ? 1.5f : 1f);
        }
    }
}


// ── The fireball ────────────────────────────────────────────────────────────
// Slow, straight, gravity-free. Pops on the first solid tile or player body it
// touches, and any player hit (slash, stab, …) swats it out of the air — its
// 0.1 HP goes on the first contact.
public class AspidFireballProjectile : Projectile, ITelegraphSource
{
    private const float Radius         = 4f;
    private const float LifeSeconds    = 5f;
    private const float Damage         = 1.0f;     // a fifth of a player's 5 HP
    private const float HitboxHalfSize = 4.5f;
    private const float Knockback      = 260f;
    // Stall backstop: the swept solver zeroes velocity on a head-on tile hit.
    private const float StopSpeed      = 20f;
    private const float ArmDelay       = 0.05f;

    private readonly int _hitId;

    public override EntityKind Kind => EntityKind.AspidFireball;

    public AspidFireballProjectile(Vector2 pos, Vector2 velocity, int hitId, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius, 6), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        _hitId        = hitId;
        Body.Velocity = velocity;
        Mass          = 0.3f;
        GravityScale  = 0f;
        Color         = new Color(255, 140, 40);
        Sprite        = Sprites.Fireball(Radius);
    }

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId = _hitId;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        var p = Body.Position;

        if ((Age >= ArmDelay && Body.Velocity.LengthSquared() < StopSpeed * StopSpeed)
            || TouchingSolid(spawner?.Chunks))
        {
            Health = 0f;
            return;
        }

        var region = new BoundingBox(p.X - HitboxHalfSize, p.Y - HitboxHalfSize,
                                     p.X + HitboxHalfSize, p.Y + HitboxHalfSize);
        var vel = Body.Velocity;
        var dir = vel.LengthSquared() > 0.01f ? Vector2.Normalize(vel) : Vector2.UnitX;

        hitboxes?.Publish(new Hitbox(
            region, _hitId, Damage, dir * Knockback,
            Faction, Id, Color,
            targets: HitTargets.EntitiesOnly,
            origin: p));

        // Burst on the body it hits instead of sailing through. This frame's
        // hitbox stays live for CombatSystem.Apply at the end of the tick.
        if (TouchingPlayer(spawner?.Hurtboxes, region)) Health = 0f;
    }

    private static bool TouchingPlayer(HurtboxWorld hurt, BoundingBox region)
    {
        if (hurt == null) return false;
        foreach (var hb in hurt.All)
            if (Factions.IsPlayer(hb.Owner) && HitboxWorld.Overlaps(hb.Region, region))
                return true;
        return false;
    }

    // Any solid cell under the body bounds, padded — a glancing contact slides
    // rather than stalls, so the velocity test alone would let it skate along
    // a wall.
    private bool TouchingSolid(ChunkMap chunks)
    {
        if (chunks == null) return false;
        const float Pad = 1f;
        var b = Body.Bounds;
        int x0 = (int)MathF.Floor((b.Left   - Pad) / Chunk.TileSize);
        int x1 = (int)MathF.Floor((b.Right  + Pad) / Chunk.TileSize);
        int y0 = (int)MathF.Floor((b.Top    - Pad) / Chunk.TileSize);
        int y1 = (int)MathF.Floor((b.Bottom + Pad) / Chunk.TileSize);
        for (int gy = y0; gy <= y1; gy++)
        for (int gx = x0; gx <= x1; gx++)
            if (chunks.GetCellState(gx, gy) == TileState.Solid) return true;
        return false;
    }

    // Render-only flicker halo.
    public void Telegraph(TelegraphList t)
    {
        float flicker = 0.5f + 0.5f * MathF.Sin(AgeSeconds * 40f);
        t.Ring(Body.Position, Radius + 2f + flicker, new Color(255, 200, 80) * (0.35f + 0.25f * flicker), 10, 1f);
    }
}


// ── 4. THE WIRING ───────────────────────────────────────────────────────────
public static class AspidEnemy
{
    public static EnemyBlueprint Blueprint => new()
    {
        Kind          = EntityKind.Aspid,

        // ── body ──
        Radius        = 9f,
        Sides         = 8,
        // Six stock slashes.
        Health        = 3f,
        // Light enough that a slash sends it tumbling back; with the hover's
        // low acceleration the knockback coasts rather than snapping back.
        Mass          = 0.9f,
        // Zero, like Bird / Shrike: the hover's budget goes on steering, not on
        // holding altitude.
        GravityScale  = 0f,
        FrictionScale = 0.05f,

        // ── looks ──
        Color         = new Color(215, 110, 40),
        Sprite        = Sprites.Aspid,

        Controller    = new AspidController(),

        // No EnemyStaggerState: stagger commits and would stop it flying, and at
        // GravityScale 0 it would hang frozen. Knockback + low accel reads the hit.
        // No EnemyAttackHoldState: it would brake the hover during the volley.
        Movement = () => new()
        {
            new EnemyIdleState(),        // 0 — fallback (never actually used: hover is always valid)
            new AspidHoverState(),
        },
        Actions = () => new()
        {
            new AspidVolleyAction(),
        },
    };
}
