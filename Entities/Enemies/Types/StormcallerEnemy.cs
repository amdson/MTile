using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  STORMCALLER — a rooted boss at the far end of a hall full of its own weather.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it: set "Stage": "stormcaller" in game_config.json.
//
// It stands at the right-hand end and never walks. The fight is the approach:
//
// The fight alternates two phases on a fixed clock (StormWind):
//
//   SHELTER (15 s) — the gale blows down the hall: moderate, intense, moderate, intense.
//     Everything hittable that is not on its side is pushed downwind and, on a beat,
//     hit by a GUST. The wind runs in lanes and TILES BLOCK IT — the lee of a solid tile
//     is out of the push, the gust and the rain — but every gust wears at the tile each
//     lane breaks on, so a windbreak is borrowed time. The end of a moderate blow
//     flickers white before the intense one. Two things keep you from just sealing
//     yourself in: as each intense blow begins an ICE FIELD forms around you (circles
//     over ~70 % of the screen that ignore terrain, with two clear gaps to get to), and
//     now and then a three-tile COLUMN of stronger wind comes down a line through you.
//   RUN (10 s) — dead calm. No gale, so you can cross open ground; instead BEAMS of
//     wind cut across the stage at odd angles (0–2 at a time, one usually through you),
//     which drill straight through dirt, sand and foam and hit you behind it — only
//     ROCK stops one, and barely notices it — and the bombs are three times the size. The end of the calm flickers the lanes the gale will come back on;
//     the heading swings a few degrees each cycle.
//
//   * THE BOMBS. At range it lobs storm bombs on a high, slow arc at where you stand;
//     a dashed line shows the rest of each one's flight. A bomb bursts on terrain or on
//     you; any hit of yours swats it out of the air.
//   * UP CLOSE it has two telegraphed strikes whose whole job is to throw you back
//     down the hall into the wind: the GALE PALM (a long slab in front, launches you
//     horizontally) and the UPDRAFT (a ring around it, for a player above or behind).
//
// It is fragile — 4 HP — and both strikes have long recoveries, so once you are in
// you win quickly. No stagger state on purpose: a boss whose attacks are cancelled by
// any hit would never get to throw you back.
//
// Built from the TemplateEnemy pattern (brain / legs / arms); see that file for the
// rules every piece here follows. The brain is the stock StationaryAimController —
// the four actions sort themselves out by precondition and priority.

// ── Arms 1: Gale Palm ───────────────────────────────────────────────────────
// A long slab straight ahead. Slow windup, short active, long recovery.
public class StormcallerPalmAction : EnemyActionState
{
    protected virtual float   Windup        => 0.70f;
    protected virtual float   Active        => 0.12f;
    protected virtual float   Recovery      => 0.95f;

    // Reach ordering (widest → narrowest): slab far edge Inset + Reach = 78, trigger 64.
    protected virtual float   Range         => 64f;
    protected virtual float   VerticalSlack => 24f;
    protected virtual float   Inset         => 8f;
    protected virtual float   Reach         => 70f;
    protected virtual float   HalfHeight    => 20f;

    protected virtual float   Damage        => 0.6f;
    // Δv 300 on the player's 2.5 mass, as a launch — you are thrown, not nudged.
    protected virtual Vector2 Knockback     => new(750f, -260f);
    protected virtual float   LaunchDecay   => 350f;

    protected virtual Color   TelegraphColor => new(150, 205, 255);
    protected virtual Color   StrikeColor    => new(235, 245, 255);

    public override int ActivePriority  => 40;
    public override int PassivePriority => 35;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        var to = ctx.ToPlayer;
        return to.X * ctx.Facing > 0f && MathF.Abs(to.X) < Range && MathF.Abs(to.Y) < VerticalSlack;
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? -1 : ctx.Facing;
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

    private BoundingBox StrikeRegion(Vector2 pos, int facing)
    {
        float cx = pos.X + facing * (Inset + Reach * 0.5f);
        return new BoundingBox(cx - Reach * 0.5f, pos.Y - HalfHeight,
                               cx + Reach * 0.5f, pos.Y + HalfHeight);
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration) return;

        ctx.Hitboxes?.Publish(new Hitbox(
            StrikeRegion(ctx.Self.Body.Position, v.LockedFacing), v.HitId, Damage,
            new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
            Faction.Enemy, ctx.Self.Id, StrikeColor,
            targets: HitTargets.EntitiesOnly,
            origin: ctx.Self.Body.Position,
            launchDecay: LaunchDecay));
    }

    // Windup: the exact slab, outlined, filling from the boss outward. Active: solid.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        var   r    = StrikeRegion(body.Position, v.LockedFacing);

        if (v.WindupDuration > 0f && time < v.WindupDuration)
        {
            float p = time / v.WindupDuration;
            t.Line(new Vector2(r.Left,  r.Top),    new Vector2(r.Right, r.Top),    TelegraphColor);
            t.Line(new Vector2(r.Left,  r.Bottom), new Vector2(r.Right, r.Bottom), TelegraphColor);
            t.Line(new Vector2(r.Left,  r.Top),    new Vector2(r.Left,  r.Bottom), TelegraphColor);
            t.Line(new Vector2(r.Right, r.Top),    new Vector2(r.Right, r.Bottom), TelegraphColor);
            float w = r.Width * p;
            float x = v.LockedFacing > 0 ? r.Left : r.Right - w;
            t.Box(x, r.Top, w, r.Height, TelegraphColor * (0.15f + 0.3f * p));
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            t.Box(r.Left, r.Top, r.Width, r.Height, StrikeColor * 0.7f);
        }
    }
}

// ── Arms 2: Updraft ─────────────────────────────────────────────────────────
// A burst all round the body, for a player standing on it, above it or behind it —
// everywhere the palm cannot point. Throws outward and up.
public class StormcallerUpdraftAction : EnemyActionState
{
    protected virtual float Windup    => 0.55f;
    protected virtual float Active    => 0.10f;
    protected virtual float Recovery  => 0.80f;

    protected virtual float Range     => 44f;    // trigger, centre to centre
    protected virtual float Radius    => 52f;    // burst reach
    protected virtual int   Segments  => 8;
    protected virtual float Damage    => 0.5f;
    protected virtual float Knockback => 650f;   // Δv 260 on the player
    protected virtual float Lift      => 0.6f;   // upward bias added to the outward direction
    protected virtual float LaunchDecay => 350f;

    protected virtual Color TelegraphColor => new(150, 205, 255);
    protected virtual Color StrikeColor    => new(235, 245, 255);

    public override int ActivePriority  => 40;
    public override int PassivePriority => 30;   // under the palm: in front and level, the palm goes

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Dist < Range;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? -1 : ctx.Facing;
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
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration || ctx.Hitboxes == null) return;

        // One HitId across the core and the ring, so a body is thrown once — by whichever
        // box it overlaps first in publish order (core, then the ring clockwise from +x).
        var   c    = ctx.Self.Body.Position;
        float half = Radius * 0.42f;
        ctx.Hitboxes.Publish(new Hitbox(
            new BoundingBox(c.X - half, c.Y - half, c.X + half, c.Y + half), v.HitId, Damage,
            new Vector2(-v.LockedFacing * 0.35f, -1f) * Knockback,
            Faction.Enemy, ctx.Self.Id, StrikeColor,
            targets: HitTargets.EntitiesOnly, origin: c, launchDecay: LaunchDecay));
        for (int i = 0; i < Segments; i++)
        {
            float a   = i * MathHelper.TwoPi / Segments;
            var   dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var   sc  = c + dir * (Radius - half);
            var   kb  = dir + new Vector2(0f, -Lift);
            kb.Normalize();
            ctx.Hitboxes.Publish(new Hitbox(
                new BoundingBox(sc.X - half, sc.Y - half, sc.X + half, sc.Y + half), v.HitId, Damage,
                kb * Knockback,
                Faction.Enemy, ctx.Self.Id, StrikeColor,
                targets: HitTargets.EntitiesOnly, origin: c, launchDecay: LaunchDecay));
        }
    }

    // Windup: the burst's outer ring, with a second ring closing in on the body. Active:
    // a filled flash.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        if (v.WindupDuration > 0f && time < v.WindupDuration)
        {
            float p = time / v.WindupDuration;
            t.Ring(body.Position, Radius, TelegraphColor * (0.35f + 0.5f * p), 24, 1f);
            t.Ring(body.Position, Radius * (1f - 0.75f * p), TelegraphColor * (0.25f + 0.5f * p), 20, 1f);
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            t.Disc(body.Position, Radius, StrikeColor * 0.55f);
        }
    }
}

// ── Arms 3: Lob ─────────────────────────────────────────────────────────────
// One storm bomb on a high slow arc at where the target stands when it leaves the hand.
// The recovery is the cadence.
public class StormcallerLobAction : EnemyActionState
{
    protected virtual float Windup   => 0.60f;
    protected virtual float Recovery => 1.50f;

    protected virtual float MinRange => 70f;
    protected virtual float MaxRange => 1700f;

    // Flight time grows with distance so a long lob is no flatter than a short one:
    // apex rise above a level chord is g·T²/8 — 100 px at 2.1 s, 215 px at 3.1 s.
    protected virtual float MinFlight      => 2.1f;
    protected virtual float MaxFlight      => 3.1f;
    protected virtual float FlightPerPx    => 1f / 900f;

    protected virtual Color ChargeColor => new(170, 215, 255);

    public const int   RunBombScale = 3;
    public const float MuzzleRise = 26f;   // above the body centre: over its own head

    public override int ActivePriority  => 40;
    public override int PassivePriority => 20;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= MinRange && ctx.Dist <= MaxRange;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? -1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = 0f;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float before = v.TimeInState;
        v.TimeInState += ctx.Dt;
        // The one frame the clock crosses the windup: no fired flag to snapshot.
        if (before >= v.WindupDuration || v.TimeInState < v.WindupDuration) return;

        var muzzle = ctx.Self.Body.Position + new Vector2(0f, -MuzzleRise);
        var target = ctx.Target.Position;
        // In the calm (the run phase) the bombs are three times the size.
        int scale = StormWind.At(ctx.Frame).Blowing ? 1 : RunBombScale;
        ctx.Spawner.SpawnEntity(new StormBombProjectile(
            muzzle, LaunchVelocity(muzzle, target), target, v.HitId, ctx.Self.Faction, scale));
    }

    // The velocity that carries a bomb from `from` to `to` in the flight time this
    // distance earns, under the bomb's own (reduced) gravity.
    public Vector2 LaunchVelocity(Vector2 from, Vector2 to)
    {
        var   d = to - from;
        float T = MathHelper.Clamp(MinFlight + MathF.Abs(d.X) * FlightPerPx, MinFlight, MaxFlight);
        float g = Simulation.Gravity.Y * StormBombProjectile.BombGravityScale;
        return new Vector2(d.X / T, d.Y / T - 0.5f * g * T);
    }

    // A charge gathering over its head.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f || v.TimeInState >= v.WindupDuration) return;
        float p      = v.TimeInState / v.WindupDuration;
        var   muzzle = body.Position + new Vector2(0f, -MuzzleRise);
        t.Disc(muzzle, 1.5f + 3.5f * p, ChargeColor * (0.4f + 0.6f * p));
        t.Ring(muzzle, 10f - 5f * p, ChargeColor * (0.3f + 0.6f * p), 12, 1f);
    }
}

// ── The bomb ────────────────────────────────────────────────────────────────
// Ballistic under a third of world gravity, which is what makes the arc high AND slow.
// Bursts on the first terrain it strikes or the first body it can hurt; a hit of the
// player's swats it (0.1 HP) and it dies without bursting. The burst is a core box plus
// a ring of segments, terrain-occluded from the centre, and it chips tiles — so a wall
// you hide behind is cover for one bomb, not for the fight.
public class StormBombProjectile : Projectile, ITelegraphSource
{
    public const float BombGravityScale = 0.3f;

    private const float Radius           = 5f;
    private const float LifeSeconds      = 7f;
    private const float ArmDelay         = 0.10f;
    private const float ContactImpulseEps = 0.01f;

    public  const float BlastRadius      = 40f;
    private const int   BlastSegments    = 10;
    private const float SegmentHalfSize  = 10f;
    private const float CoreHalfSize     = 13f;
    private const float BodyDamage       = 0.8f;
    private const float TileBlastDamage  = TileDamage.TileMaxHP * 1.2f;   // pops sand, cracks dirt, chips stone
    private const float BlastKnockback   = 420f;   // Δv 168 on the player — a shove, under the stun line
    private const float ContactHalfSize  = 7f;

    private static readonly Color ShellColor = new(60, 80, 120);
    private static readonly Color GlowColor  = new(170, 215, 255);

    private readonly int     _hitId;
    private readonly Vector2 _target;      // where it was thrown at
    private readonly int     _scale;       // 1, or 3 for a run-phase bomb: blast reach and size
    private bool _exploded;

    public override EntityKind Kind => EntityKind.StormBomb;
    public bool    Exploded => _exploded;
    public Vector2 Target   => _target;
    public int     Scale    => _scale;

    public StormBombProjectile(Vector2 pos, Vector2 velocity, Vector2 target, int hitId, Faction owner, int scale = 1)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius * (scale > 1 ? 2f : 1f), 8), pos), health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        _scale        = Math.Max(1, scale);
        _hitId        = hitId;
        _target       = target;
        Body.Velocity = velocity;
        Mass          = 0.6f;
        GravityScale  = BombGravityScale;
        Color         = ShellColor;
        Sprite        = Sprites.Ball(Radius * (scale > 1 ? 2f : 1f));
    }

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId    = _hitId;
        s.Aim      = _target;
        s.Budget   = _scale;
        s.Exploded = _exploded;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _exploded = s.Exploded;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        // The burst's hitboxes lived through last frame's CombatSystem.Apply; now go.
        if (_exploded) { Health = 0f; return; }
        if (Age < ArmDelay) return;

        var  p     = Body.Position;
        bool burst = Body.LastImpulseMagnitude > ContactImpulseEps      // struck terrain
                  || Age >= Lifetime - 2f * dt                          // never landed: go off anyway
                  || TouchingVictim(spawner?.Hurtboxes, p);
        if (!burst) return;

        if (hitboxes != null)
        {
            // Core first: the body actually struck sits at the centre, inside the ring.
            // Every length scales with the bomb; the damage does not.
            float core = CoreHalfSize * _scale, seg = SegmentHalfSize * _scale;
            hitboxes.Publish(new Hitbox(
                new BoundingBox(p.X - core, p.Y - core, p.X + core, p.Y + core),
                _hitId, TileBlastDamage, new Vector2(0f, -BlastKnockback),
                Faction, Id, GlowColor, bodyDamage: BodyDamage, origin: p));
            for (int i = 0; i < BlastSegments; i++)
            {
                float a   = i * MathHelper.TwoPi / BlastSegments;
                var   dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                var   sc  = p + dir * (BlastRadius * _scale - seg);
                hitboxes.Publish(new Hitbox(
                    new BoundingBox(sc.X - seg, sc.Y - seg, sc.X + seg, sc.Y + seg),
                    _hitId, TileBlastDamage, dir * BlastKnockback,
                    Faction, Id, GlowColor, bodyDamage: BodyDamage, origin: p));
            }
        }
        spawner?.NotifyChargedBlast(Id, p, BlastRadius * _scale);   // cosmetic: particles + sound
        _exploded     = true;
        Body.Velocity = Vector2.Zero;
        GravityScale  = 0f;
    }

    private bool TouchingVictim(HurtboxWorld hurt, Vector2 p)
    {
        if (hurt == null) return false;
        float h = ContactHalfSize * (_scale > 1 ? 2f : 1f);
        var region = new BoundingBox(p.X - h, p.Y - h, p.X + h, p.Y + h);
        foreach (var hb in hurt.All)
            if (hb.Owner != Faction && hb.Target != Id && HitboxWorld.Overlaps(hb.Region, region))
                return true;
        return false;
    }

    // Render-only: a pulsing halo on the bomb, and the rest of its flight as a dashed
    // line — the arc it will actually fly from here, under its own gravity — ending in a
    // small cross where it was thrown at.
    public void Telegraph(TelegraphList t)
    {
        if (_exploded) { t.Disc(Body.Position, BlastRadius * _scale, GlowColor * 0.45f); return; }
        float pulse = 0.5f + 0.5f * MathF.Sin(AgeSeconds * 18f);
        t.Ring(Body.Position, Radius * (_scale > 1 ? 2f : 1f) + 2f + pulse, GlowColor * (0.4f + 0.3f * pulse), 10, 1f);

        const float h = 1f / 30f;
        float g   = Simulation.Gravity.Y * BombGravityScale;
        var   p   = Body.Position;
        var   vel = Body.Velocity;
        var   end = p;
        for (int i = 0; i < 150; i++)
        {
            vel.Y += g * h;
            var next = p + vel * h;
            if (i % 2 == 1) t.Line(p, next, GlowColor * 0.6f, 1f);
            p = end = next;
            if (vel.Y > 0f && p.Y >= _target.Y) break;      // back down to where it was aimed
        }
        t.Line(end + new Vector2(-4f, -4f), end + new Vector2(4f, 4f), GlowColor * 0.8f, 1f);
        t.Line(end + new Vector2(-4f, 4f),  end + new Vector2(4f, -4f), GlowColor * 0.8f, 1f);
    }
}

// ── Arms 4: Wind column ─────────────────────────────────────────────────────
// A narrow, violent jet aimed at where the target stood when the windup began. This
// class is only the clock and the numbers: the jet itself — the lanes, the push, the
// hits, the erosion and the drawing — lives on the body (StormcallerEnemy.UpdateJet),
// next to the rest of the weather, because it needs the terrain scan every frame and
// an action's Telegraph has no terrain to scan.
//
// Semi-rare, and stateless about it: it may only start inside a short window that comes
// round every PeriodSeconds on the boss's own clock (out of step with the wind cycle, so
// it lands in every phase over a fight). The window is shorter than the action.
public class StormcallerJetAction : EnemyActionState
{
    public const float Windup         = 1.10f;   // the outline: get out of it
    public const float Active         = 1.30f;
    public const float Recovery       = 0.70f;
    public const float PeriodSeconds  = 11f;
    public const float WindowSeconds  = 2.5f;
    public const float MinDistance    = 110f;    // nearer than this, it uses its hands

    // Geometry: three tile-wide lanes side by side, coming down at a fixed 10° — inside
    // the range the ambient wind swings through — on a line through the aim point.
    public const int   HalfLanes      = 1;
    public const float AngleDeg       = 10f;
    public const float ReachPast      = 500f;    // how far the jet runs on beyond the aim point
    public static readonly Vector2 Dir = new(-MathF.Cos(MathHelper.ToRadians(AngleDeg)),
                                              MathF.Sin(MathHelper.ToRadians(AngleDeg)));

    // Strength. 2.5× gravity of push; a hit every quarter second (0.3 HP, Δv 200 on the
    // player); and it takes most of a dirt tile off whatever each lane ends on per hit —
    // five hits over the active window chews through two stone tiles.
    public const float Accel          = 1500f;
    public const float TickSeconds    = 0.25f;
    public const float Damage         = 0.30f;
    public const float Knockback      = 500f;
    public const float Erode          = TileDamage.TileMaxHP * 0.7f;

    public override int ActivePriority  => 40;
    public override int PassivePriority => 25;   // over the lob, under both melee strikes

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        int period = SimFrames.FromSeconds(PeriodSeconds, Simulation.FixedDt);
        int window = SimFrames.FromSeconds(WindowSeconds, Simulation.FixedDt);
        // Half a period of offset: the first one comes ~5 s into the fight, not at once.
        if ((ctx.Frame + period / 2) % period >= window) return false;
        if (!StormWind.At(ctx.Frame).Blowing) return false;    // the calm has its own beams
        return ctx.ToPlayer.X < -MinDistance;    // downwind of it, and not in its face
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? -1 : ctx.Facing;
        v.LockedAim    = ctx.Target.Position;    // a POINT here, not a direction
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

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v) => v.TimeInState += ctx.Dt;
}

// ── Run-phase beam ──────────────────────────────────────────────────────────
// A band of wind far stronger than the gale, on a line through an aim point at an
// arbitrary angle. Loose material does not stop it: it hits bodies behind dirt, sand and
// foam, and on every tick tears at every such tile in the band (dirt goes in one tick),
// so it drills straight through an ordinary barricade. ROCK does stop it — stone or
// harder ends the beam like a wall ends the gale, shelters what is behind it, and loses
// only a sliver per tick (a dozen or so direct beams to break one stone tile). So a small
// stone fort is close to permanent against beams, and worth what stone costs.
//
// Three lanes abreast, born OriginBack upwind of the aim point (off-screen for a beam
// aimed at you; in the wall or ceiling if that is where the line starts) and running
// until rock. Spawned on a schedule by the Stormcaller during the calm
// (StormcallerEnemy.UpdateSchedule); it then runs itself: outline, blast, gone.
public class StormBeam : Projectile, ITelegraphSource
{
    public const float TelegraphSeconds = 0.90f;
    public const float ActiveSeconds    = 1.05f;
    public const int   HalfLanes        = 1;                       // three tiles across
    public const float OriginBack       = 260f;
    public const float Reach            = 1300f;                   // "the rest of the stage"

    private const float Accel       = 1500f;
    private const float TickSeconds = 0.25f;
    private const float Damage      = 0.30f;
    private const float Knockback   = 500f;                        // Δv 200 on the player
    private const float TileBite    = TileDamage.TileMaxHP * 1.2f; // loose tiles in the band
    private const float RockBite    = TileDamage.TileMaxHP * 0.04f;// the rock each lane ends on
    // What counts as rock: stone's strength or more.
    private static float RockHP => TileDamage.MaxHPFor(TileType.Stone);

    private const int LaneCount = 2 * HalfLanes + 1;
    private static readonly Color EdgeColor = new(170, 215, 255);
    private static readonly Color CoreColor = new(235, 245, 255);

    private readonly float   _angle;     // radians; the way it blows
    private readonly float   _power;     // scales push, damage and tile bite (it grows through a calm)
    private readonly Vector2 _dir;
    // Transient scan, rebuilt every update (see the note on StormcallerEnemy's lanes).
    private readonly StormWind.Lane[] _lanes = new StormWind.Lane[LaneCount];
    private bool _scanned;

    public override EntityKind Kind => EntityKind.StormBeam;
    public bool    Blowing   => Age >= TelegraphSeconds;
    public Vector2 Direction => _dir;

    public StormBeam(Vector2 aim, float angle, float power, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(2f, 4), aim), health: 1f,
               lifetime: TelegraphSeconds + ActiveSeconds, owner: owner)
    {
        _angle = angle;
        _power = power;
        _dir   = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        Rooted = true;                      // a marker, not a body: nothing moves it
        GravityScale = 0f;
        Color  = Color.Transparent;
        Sprite = null;
    }

    public override void PublishHurtboxes(HurtboxWorld world) { }   // nothing to hit

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.StateTime  = _angle;
        s.ProjDamage = _power;
    }

    public bool Covers(in BoundingBox r)
    {
        if (!_scanned) return false;
        for (int k = 0; k < LaneCount; k++)
            if (StormWind.Touches(in _lanes[k], _dir, in r, Chunk.TileSize * 0.5f)) return true;
        return false;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        var chunks = spawner?.Chunks;
        _scanned = chunks != null;
        if (!_scanned) return;
        const float ts = Chunk.TileSize;
        var norm = new Vector2(-_dir.Y, _dir.X);
        for (int k = 0; k < LaneCount; k++)
            _lanes[k] = StormWind.March(chunks, Body.Position - _dir * OriginBack + norm * ((k - HalfLanes) * ts),
                                        _dir, OriginBack + Reach, skipLeadingSolid: true, minHP: RockHP);

        float ta = Age - TelegraphSeconds;
        if (ta < 0f) return;
        bool tick = hitboxes != null && MathF.Floor(ta / TickSeconds) != MathF.Floor((ta - dt) / TickSeconds);
        int  id   = tick ? spawner.HitIds.Next() : 0;
        var  hurt = spawner.Hurtboxes;
        if (hurt != null)
            foreach (var hb in hurt.All)
            {
                if (hb.Owner == Faction || !Covers(hb.Region)) continue;
                var body = spawner.ResolveBody(hb.Target);
                if (body != null) body.AppliedForce += _dir * (Accel * _power);
                if (tick)
                    hitboxes.Publish(new Hitbox(
                        hb.Region, id, Damage * _power, _dir * Knockback,
                        Faction, Id, CoreColor,
                        targets: HitTargets.EntitiesOnly,
                        hitstopSecondsOverride: 0f));
            }
        if (!tick) return;

        // Terrain. Each lane's live stretch scours everything softer than rock in it (a
        // soft hit: no origin, so nothing loose is shielded by what is in front of it)...
        int soft = spawner.HitIds.Next(), hard = spawner.HitIds.Next();
        for (int k = 0; k < LaneCount; k++)
        {
            ref readonly var l = ref _lanes[k];
            if (!l.Live) continue;
            float len = l.End - l.Start;
            var   mid = l.Origin + _dir * ((l.Start + l.End) * 0.5f);
            var   strip = new Polygon(new[]
            {
                new Vector2(-len * 0.5f, -ts * 0.5f), new Vector2(len * 0.5f, -ts * 0.5f),
                new Vector2(len * 0.5f,  ts * 0.5f),  new Vector2(-len * 0.5f, ts * 0.5f),
            });
            hitboxes.Publish(new Hitbox(
                strip.GetBoundingBox(mid, _angle), soft, TileBite * _power, Vector2.Zero,
                Faction, Id, CoreColor,
                targets: HitTargets.TilesOnly,
                shape: strip, shapePos: mid, shapeRotation: _angle,
                tileMaxMaterialHP: RockHP - 0.01f));
            // ...and the rock it ends on loses a sliver.
            if (!l.Blocked) continue;
            float bx = l.BlockX * ts, by = l.BlockY * ts;
            hitboxes.Publish(new Hitbox(
                new BoundingBox(bx + 2f, by + 2f, bx + ts - 2f, by + ts - 2f),
                hard, RockBite * _power, Vector2.Zero, Faction, Id, CoreColor,
                targets: HitTargets.TilesOnly));
        }
    }

    // Telegraph: the outline of exactly where it will blow — both long edges and a cap
    // across each lane's end, so it shows which rock will stop it — brightening over a
    // faint flickering fill, with chevrons marching the way it will blow.
    // Active: a white band with streaks tearing along it, and spray where it breaks on rock.
    public void Telegraph(TelegraphList t)
    {
        if (!_scanned) return;
        const float ts = Chunk.TileSize;
        var   norm = new Vector2(-_dir.Y, _dir.X);
        bool  blow = Blowing;
        float p    = blow ? 1f : Age / TelegraphSeconds;
        var   fill = blow ? CoreColor * (0.45f + 0.15f * MathF.Sin(Age * 50f))
                          : CoreColor * (0.04f + 0.14f * p * (0.6f + 0.4f * MathF.Sin(Age * (10f + 30f * p))));
        var   edge = Color.Lerp(EdgeColor, CoreColor, p) * (0.5f + 0.5f * p);

        for (int k = 0; k < LaneCount; k++)
        {
            ref readonly var l = ref _lanes[k];
            if (!l.Live) continue;
            t.RotatedRect(l.Origin + _dir * ((l.Start + l.End) * 0.5f), new Vector2(l.End - l.Start, ts + 0.5f), _angle, fill);
            var end = l.Origin + _dir * l.End;
            t.Line(end - norm * (ts * 0.5f), end + norm * (ts * 0.5f), blow && l.Blocked ? CoreColor : edge, blow ? 3f : 1.5f);
            if (k == 0 || k == LaneCount - 1)
            {
                var off = norm * ((k == 0 ? -0.5f : 0.5f) * ts);
                t.Line(l.Origin + off + _dir * l.Start, end + off, edge, 1.5f);
            }
            if (blow && l.Blocked)
                for (int j = 0; j < 3; j++)
                {
                    float ph = Frac(Age * (5f + 1.3f * j) + k * 0.37f + j * 0.5f);
                    t.Rect(end - _dir * (2f + ph * 16f) + norm * ((Frac(k * 0.61f + j * 0.43f) - 0.5f) * ts), 2f, CoreColor * (1f - ph));
                }
        }

        ref readonly var mid = ref _lanes[HalfLanes];
        if (!mid.Live) return;
        float span = mid.End - mid.Start;
        if (!blow)
        {
            for (float u = (Age * 160f) % 55f; u < span; u += 55f)
            {
                var a = mid.Origin + _dir * (mid.Start + u);
                t.Line(a - _dir * 5f + norm * 5f, a, edge * 0.7f, 1f);
                t.Line(a - _dir * 5f - norm * 5f, a, edge * 0.7f, 1f);
            }
            return;
        }
        for (int i = 0; i < 70; i++)
        {
            ref readonly var l = ref _lanes[i % LaneCount];
            if (!l.Live) continue;
            float ls = l.End - l.Start;
            float u  = l.Start + Frac(i * 0.6180339f + Age * 1400f / MathF.Max(ls, 1f) * (0.8f + 0.4f * Frac(i * 0.57f))) * ls;
            var   a  = l.Origin + _dir * u + norm * ((Frac(i * 0.7548777f) - 0.5f) * ts);
            t.Line(a, a + _dir * MathF.Min(26f, l.End - u), CoreColor * 0.9f, 1.5f);
        }
    }

    private static float Frac(float v) => v - MathF.Floor(v);
}

// ── Ice field ───────────────────────────────────────────────────────────────
// The answer to walling yourself in. A screen-sized patch of slowly forming ice
// circles — about 70 % of the area, with two large clear gaps placed on purpose —
// centred on where the target stood when it was cast. Circles ignore terrain: when
// they set, everything inside one is hit hard and frozen, wherever it is hiding. One is
// always on the cast point, so standing still is never the answer; a gap is always a
// short run away at about the target's own height, so there always is one.
// Cast by the Stormcaller as each intense blow begins.
public class StormIceField : Projectile, ITelegraphSource
{
    public const float FormSeconds   = 2.4f;     // outlines closing in: the time you have
    public const float LingerSeconds = 0.3f;
    public const float RegionW       = 440f;     // a little more than a screen at the game camera
    public const float RegionH       = 230f;
    public const float GapRadius     = 40f;
    public const float Coverage      = 0.70f;
    private const int   MaxCircles   = 90;
    private const float MinRadius    = 20f, MaxRadius = 34f;

    private const float Damage       = 1.5f;     // of the player's 5
    private const float StunSeconds  = 0.8f;     // frozen

    private static readonly Color IceColor  = new(150, 225, 255);
    private static readonly Color SetColor  = new(235, 250, 255);

    private readonly int _seed;
    private bool _set;
    private List<Vector3> _circles;              // x, y, radius — a pure function of seed + position
    private Vector2[] _gaps;

    public override EntityKind Kind => EntityKind.StormIce;
    public bool HasSet => _set;

    public StormIceField(Vector2 centre, int seed, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(2f, 4), centre), health: 1f,
               lifetime: FormSeconds + LingerSeconds, owner: owner)
    {
        _seed  = seed;
        Rooted = true;
        GravityScale = 0f;
        Color  = Color.Transparent;
        Sprite = null;
    }

    public override void PublishHurtboxes(HurtboxWorld world) { }

    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.Budget   = _seed;
        s.Exploded = _set;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _set = s.Exploded;
    }

    // The pattern: gaps first, then circles thrown at random and kept unless they would
    // touch a gap, until the covered share of the region reaches Coverage.
    public static List<Vector3> Layout(int seed, Vector2 centre, out Vector2[] gaps)
    {
        uint state = (uint)seed * 2654435761u + 0x9E3779B9u;
        float Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state & 0xFFFFFF) / (float)0x1000000; }

        float left = centre.X - RegionW * 0.5f, top = centre.Y - RegionH * 0.6f;   // most of it above: the floor is below
        // Gap 0: a short run to one side, near the target's own height. Gap 1: anywhere
        // else along the region, a jump's height at most above.
        float side = Next() < 0.5f ? -1f : 1f;
        gaps = new Vector2[2];
        gaps[0] = centre + new Vector2(side * (85f + 60f * Next()), -22f * Next());
        do gaps[1] = new Vector2(left + GapRadius + (RegionW - 2f * GapRadius) * Next(), centre.Y - 55f * Next());
        while (MathF.Abs(gaps[1].X - gaps[0].X) < 2.2f * GapRadius || MathF.Abs(gaps[1].X - centre.X) < 78f);

        const int gw = 44, gh = 23;                      // coverage grid, 10 px cells
        var covered = new bool[gw * gh];
        int filled = 0;
        var circles = new List<Vector3>(MaxCircles) { new(centre.X, centre.Y - 6f, 30f) };
        void Mark(Vector3 c)
        {
            for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                if (covered[j * gw + i]) continue;
                float dx = left + (i + 0.5f) * (RegionW / gw) - c.X, dy = top + (j + 0.5f) * (RegionH / gh) - c.Y;
                if (dx * dx + dy * dy <= c.Z * c.Z) { covered[j * gw + i] = true; filled++; }
            }
        }
        Mark(circles[0]);
        for (int tries = 0; tries < 600 && circles.Count < MaxCircles && filled < Coverage * gw * gh; tries++)
        {
            var c = new Vector3(left + RegionW * Next(), top + RegionH * Next(), MinRadius + (MaxRadius - MinRadius) * Next());
            bool clear = true;
            foreach (var g in gaps)
                if (Vector2.DistanceSquared(g, new Vector2(c.X, c.Y)) < (GapRadius + c.Z) * (GapRadius + c.Z)) { clear = false; break; }
            if (!clear) continue;
            circles.Add(c);
            Mark(c);
        }
        return circles;
    }

    private void EnsureLayout() => _circles ??= Layout(_seed, Body.Position, out _gaps);

    public bool Covers(Vector2 p)
    {
        EnsureLayout();
        foreach (var c in _circles)
            if (Vector2.DistanceSquared(p, new Vector2(c.X, c.Y)) <= c.Z * c.Z) return true;
        return false;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (_set || Age < FormSeconds) return;
        _set = true;
        var hurt = spawner?.Hurtboxes;
        if (hurt == null || hitboxes == null) return;
        int id = spawner.HitIds.Next();
        foreach (var hb in hurt.All)
        {
            if (hb.Owner == Faction || !Covers(new Vector2(hb.Region.CenterX, hb.Region.CenterY))) continue;
            hitboxes.Publish(new Hitbox(
                hb.Region, id, Damage, new Vector2(0f, -120f),
                Faction, Id, SetColor,
                targets: HitTargets.EntitiesOnly,
                stunSeconds: StunSeconds));
        }
    }

    // Forming: every circle's rim, with a second rim closing in from it and a fill that
    // thickens — the gaps are simply where nothing is drawn. Set: a white flash, fading.
    public void Telegraph(TelegraphList t)
    {
        EnsureLayout();
        if (_set)
        {
            float fade = 1f - MathHelper.Clamp((Age - FormSeconds) / LingerSeconds, 0f, 1f);
            foreach (var c in _circles) t.Disc(new Vector2(c.X, c.Y), c.Z, SetColor * (0.75f * fade));
            return;
        }
        float p     = Age / FormSeconds;
        float blink = p > 0.75f ? 0.6f + 0.4f * MathF.Sin(Age * 40f) : 1f;
        foreach (var c in _circles)
        {
            var at = new Vector2(c.X, c.Y);
            t.Disc(at, c.Z, IceColor * ((0.10f + 0.22f * p) * blink));
            t.Ring(at, c.Z, IceColor * (0.45f + 0.4f * p), 20, 1f);
            t.Ring(at, c.Z * (1f - p), SetColor * (0.3f + 0.6f * p), 16, 1f);
        }
        foreach (var g in _gaps) t.Ring(g, GapRadius - 3f, new Color(120, 255, 170) * (0.55f + 0.25f * MathF.Sin(Age * 6f)), 24, 1f);
    }
}

// ── The weather ─────────────────────────────────────────────────────────────
// The gale's schedule and its geometry, as pure functions — of the boss's frame count
// (snapshotted) and of the terrain — so nothing here is state.
public enum StormPhase : byte { Moderate, Calm, Intense }

public static class StormWind
{
    // One cycle is two phases. SHELTER (15 s): the gale blows, moderate → intense →
    // moderate → intense; you want to be behind something. RUN (10 s): dead calm — the
    // gale stops, and the boss fills the quiet with beams and big bombs instead.
    public const float Moderate1Seconds = 5f;
    public const float Intense1Seconds  = 3f;
    public const float Moderate2Seconds = 4f;
    public const float Intense2Seconds  = 3f;
    public const float RunSeconds       = 10f;
    // The tail of whatever comes before a stronger blow shows it coming: the end of the
    // calm flickers the lanes the gale will use; the end of a moderate blow flickers white.
    public const float WarnSeconds      = 1.2f;

    // The wind's heading swings a little each time it drops: degrees below horizontal,
    // always leftward and never upward (rain falls). Stepped through in this order.
    private static readonly float[] AnglesDeg = { 4f, 10f, 2f, 13f, 7f };

    public readonly struct State
    {
        public readonly StormPhase Phase, Next;
        public readonly int   FrameIn;     // frames into this stretch
        public readonly int   Frames;      // its whole length
        public readonly float TimeLeft;    // seconds until it ends
        public readonly int   Heading;     // index of the wind's heading; changes as each calm begins
        public readonly int   Cycle;
        public State(StormPhase phase, StormPhase next, int frameIn, int frames, int heading, int cycle)
        {
            Phase = phase; Next = next; FrameIn = frameIn; Frames = frames;
            TimeLeft = (frames - frameIn) * Simulation.FixedDt; Heading = heading; Cycle = cycle;
        }
        public bool Blowing => Phase != StormPhase.Calm;
        // Something stronger is about to start.
        public bool Warning => TimeLeft <= WarnSeconds
                            && (Phase == StormPhase.Calm || (Phase == StormPhase.Moderate && Next == StormPhase.Intense));
    }

    public static State At(int frame)
    {
        int a = F(Moderate1Seconds), b = F(Intense1Seconds), c = F(Moderate2Seconds), d = F(Intense2Seconds), r = F(RunSeconds);
        int total = a + b + c + d + r;
        int cycle = frame / total, k = frame % total;
        if (k < a) return new State(StormPhase.Moderate, StormPhase.Intense,  k, a, cycle, cycle);
        k -= a;
        if (k < b) return new State(StormPhase.Intense,  StormPhase.Moderate, k, b, cycle, cycle);
        k -= b;
        if (k < c) return new State(StormPhase.Moderate, StormPhase.Intense,  k, c, cycle, cycle);
        k -= c;
        if (k < d) return new State(StormPhase.Intense,  StormPhase.Calm,     k, d, cycle, cycle);
        k -= d;
        return            new State(StormPhase.Calm,     StormPhase.Moderate, k, r, cycle + 1, cycle);
    }

    // A cheap deterministic 0..1 from three integers, for the scheduled attacks' dice.
    public static float Roll(int a, int b, int c)
    {
        uint h = (uint)a * 374761393u + (uint)b * 668265263u + (uint)c * 2246822519u + 0x9E3779B9u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    private static int F(float seconds) => SimFrames.FromSeconds(seconds, Simulation.FixedDt);

    // Unit vector the wind blows along for a heading index.
    public static Vector2 Direction(int heading)
    {
        float a = MathHelper.ToRadians(AnglesDeg[heading % AnglesDeg.Length]);
        return new Vector2(-MathF.Cos(a), MathF.Sin(a));
    }

    // ── Strength by phase ──
    // Moderate: a quarter of gravity; once a second 0.15 HP and Δv 110; a one-thick dirt
    //   wall lasts ~7 gusts.
    // Intense: nearly gravity; twice a second 0.25 HP and Δv 190 (still under the creature
    //   stun line); 0.45 off the tile per gust, so over its 3 s (6 gusts) one-thick stone
    //   just fails and dirt wants to be two thick. You do not stand in this.
    public static float Accel(StormPhase p)      => p == StormPhase.Intense ? 560f : p == StormPhase.Moderate ? 140f : 0f;
    public static int   GustFrames(StormPhase p) => F(p == StormPhase.Intense ? 0.5f : 1.0f);
    public static float GustDamage(StormPhase p) => p == StormPhase.Intense ? 0.25f : 0.15f;
    public static float GustImpulse(StormPhase p) => p == StormPhase.Intense ? 475f : 275f;
    public static float Erode(StormPhase p)      => TileDamage.TileMaxHP * (p == StormPhase.Intense ? 0.45f : 0.15f);

    // ── Lanes ──
    // A lane is a ray of wind: it runs from Origin along the wind's direction, live
    // between Start and End (distances along it), and ends on a tile when Blocked.
    public struct Lane
    {
        public Vector2 Origin;
        public float   Start, End;
        public bool    Blocked;
        public int     BlockX, BlockY;
        public readonly bool Live => End > Start;
    }

    // `minHP` > 0: only tiles at least that tough count (a beam that only rock stops).
    private static bool Solid(ChunkMap chunks, Vector2 p, float minHP, out int gx, out int gy)
    {
        gx = (int)MathF.Floor(p.X / Chunk.TileSize);
        gy = (int)MathF.Floor(p.Y / Chunk.TileSize);
        return chunks.GetCellState(gx, gy) == TileState.Solid
            && (minHP <= 0f || TileDamage.MaxHPFor(chunks.GetCellType(gx, gy)) >= minHP);
    }

    // Walk a lane until the first solid tile. `skipLeadingSolid`: a lane born inside
    // rock (the jet, coming down through a ceiling) starts where the rock ends; without
    // it a lane born in rock carries nothing (the ambient lanes under the floor).
    public static Lane March(ChunkMap chunks, Vector2 origin, Vector2 dir, float maxLen, bool skipLeadingSolid, float minHP = 0f)
    {
        const float step = Chunk.TileSize;
        var lane = new Lane { Origin = origin };
        float s = 0f;
        if (Solid(chunks, origin, minHP, out _, out _))
        {
            if (!skipLeadingSolid) return lane;
            while (s < maxLen && Solid(chunks, origin + dir * s, minHP, out _, out _)) s += step;
            if (s >= maxLen) return lane;
        }
        lane.Start = s;
        lane.End   = maxLen;
        for (s += step; s < maxLen; s += step)
        {
            if (!Solid(chunks, origin + dir * s, minHP, out _, out _)) continue;
            // Back up in quarter steps to the face it actually struck.
            float hit = s;
            for (int q = 3; q >= 1; q--)
            {
                float b = s - step * q * 0.25f;
                if (b > lane.Start && Solid(chunks, origin + dir * b, minHP, out _, out _)) { hit = b; break; }
            }
            Solid(chunks, origin + dir * hit, minHP, out lane.BlockX, out lane.BlockY);
            lane.Blocked = true;
            lane.End     = hit;
            break;
        }
        return lane;
    }

    // Does the live stretch of `lane`, widened to `halfThick` each side, reach box `r`?
    public static bool Touches(in Lane lane, Vector2 dir, in BoundingBox r, float halfThick)
    {
        if (!lane.Live) return false;
        float hw = r.Width * 0.5f, hh = r.Height * 0.5f;
        var   rel   = new Vector2(r.CenterX, r.CenterY) - lane.Origin;
        float along = rel.X * dir.X + rel.Y * dir.Y;
        float perp  = rel.X * dir.Y - rel.Y * dir.X;
        float ra = hw * MathF.Abs(dir.X) + hh * MathF.Abs(dir.Y);
        float rp = hw * MathF.Abs(dir.Y) + hh * MathF.Abs(dir.X);
        return along + ra > lane.Start && along - ra < lane.End && MathF.Abs(perp) < halfThick + rp;
    }
}

// ── Body ────────────────────────────────────────────────────────────────────
public sealed class StormcallerEnemy : EnemyEntity
{
    public const float HalfHeight = 20f;
    private const float WidthScale = 0.55f;   // 2·20·cos30°·0.55 ≈ 19 px wide
    public const float MaxHp      = 4f;

    // ── The gale ──
    // The wind is a fan of parallel lanes, one per tile row of the boss's own column,
    // each running downwind (StormWind.Direction) until the first solid tile. Everything
    // past that tile is in its lee: no push, no gust, no rain. A lane born in rock (the
    // rows under the floor it stands on) carries nothing. Rescanned every frame, so a
    // wall you raise shelters you the frame it exists and a hole the wind eats lets it
    // through. Strength and heading follow StormWind's cycle.
    //
    // The push scales with how much of the body the wind reaches; the gust lands if ANY
    // lane reaches it — crouching behind a wall that leaves your head out is not shelter.
    // A token flinch, and no freeze: this lands every beat for the whole fight.
    private const float GustHitstun = 0.08f;
    private const float GustLift    = 0.15f;   // upward share of the gust's shove
    // The lanes: this many rows above / below the boss's own row, and this far downwind.
    private const int   WindRowsUp   = 36;
    private const int   WindRowsDown = 6;
    private const int   WindRows     = WindRowsUp + WindRowsDown + 1;
    public  const float WindReach    = 150 * Chunk.TileSize;

    // The scans. Transient: rebuilt from the terrain every Update they are needed, never
    // snapshotted — after a restore the next Update rebuilds them before anything sim-side
    // reads them (only Telegraph, render-only, can see a frame-stale copy).
    private readonly StormWind.Lane[] _lanes = new StormWind.Lane[WindRows];
    private bool    _lanesValid;
    private Vector2 _windDir = new(-1f, 0f);
    private const int JetLanes = 2 * StormcallerJetAction.HalfLanes + 1;
    private readonly StormWind.Lane[] _jet = new StormWind.Lane[JetLanes];
    private bool    _jetValid;

    private static readonly Color BodyColor = new(95, 125, 175);
    private static readonly Color OpenColor = new(200, 225, 255);
    private static readonly Color RainColor = new(150, 190, 235);

    public override EntityKind Kind      => EntityKind.Stormcaller;
    public override string     BossTitle => "Stormcaller";

    private const int PalmIdx = 0, UpdraftIdx = 1, LobIdx = 2, JetIdx = 3;

    public StormcallerEnemy(Vector2 pos)
        : base(new PhysicsBody(CreateBodyPolygon(), pos),
               health: MaxHp,
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),        // 0 — it never walks
                   new EnemyAttackHoldState(),  // 1 — plant during a strike
               },
               actions: new List<EnemyActionState>
               {
                   new StormcallerPalmAction(),      // 0
                   new StormcallerUpdraftAction(),   // 1
                   new StormcallerLobAction(),       // 2
                   new StormcallerJetAction(),       // 3
               },
               controller: new StationaryAimController { AlertRange = 1700f })
    {
        Mass   = 6f;             // hard to shove off its spot
        Color  = BodyColor;
        Sprite = null;           // drawn as its body outline in Color
    }

    private static Polygon CreateBodyPolygon()
    {
        var verts = Polygon.CreateRegular(HalfHeight, 6).GetVertices(Vector2.Zero);
        for (int i = 0; i < verts.Length; i++) verts[i].X *= WidthScale;
        return new Polygon(verts);
    }

    // The vertical line the weather comes off: the right edge of the boss's own column.
    private float SourceX => (MathF.Floor(Body.Position.X / Chunk.TileSize) + 1f) * Chunk.TileSize;

    private void ScanLanes(ChunkMap chunks, Vector2 dir)
    {
        const float ts = Chunk.TileSize;
        float x   = SourceX - ts * 0.5f;
        int   top = (int)MathF.Floor(Body.Position.Y / ts) - WindRowsUp;
        for (int i = 0; i < WindRows; i++)
            _lanes[i] = StormWind.March(chunks, new Vector2(x, (top + i + 0.5f) * ts), dir, WindReach, skipLeadingSolid: false);
        _windDir    = dir;
        _lanesValid = true;
    }

    // 0..1: the share of box `r` the ambient wind reaches (0 in a calm or in a lee).
    public float Exposure(in BoundingBox r) => Exposure(r, out _);

    private float Exposure(in BoundingBox r, out bool touched)
    {
        touched = false;
        if (!_lanesValid) return 0f;
        const float ts = Chunk.TileSize;
        int through = 0;
        for (int i = 0; i < WindRows; i++)
        {
            if (StormWind.Touches(in _lanes[i], _windDir, in r, 0f)) through++;
            else if (!StormWind.Touches(in _lanes[i], _windDir, in r, ts * 0.5f - 1f)) continue;
            touched = true;
        }
        float span = r.Width * MathF.Abs(_windDir.Y) + r.Height * MathF.Abs(_windDir.X);
        return touched ? MathHelper.Clamp(through * ts / MathF.Max(span, ts), 0.15f, 1f) : 0f;
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        base.Update(dt, player, hitboxes, spawner);
        _lanesValid = _jetValid = false;
        if (IsDead) return;
        Color = IsActionCommitted ? OpenColor : BodyColor;
        var chunks = spawner?.Chunks;
        if (chunks == null) return;

        UpdateGale(hitboxes, spawner, chunks);
        UpdateJet(dt, hitboxes, spawner, chunks);
        UpdateSchedule(player, spawner);
    }

    private void UpdateGale(HitboxWorld hitboxes, IEntitySpawner spawner, ChunkMap chunks)
    {
        var w = StormWind.At(FrameCount);
        if (!w.Blowing && !w.Warning) return;        // dead calm: nothing to scan or draw
        var dir = StormWind.Direction(w.Heading);
        ScanLanes(chunks, dir);                       // through a warning too: the tell is drawn on them
        if (!w.Blowing) return;

        // The push goes straight onto each body's force accumulator, as a field's would —
        // hurtboxes are all published before any entity updates and forces integrate
        // after, so this is order-independent. On a gust beat every body the wind touches
        // also takes a one-frame hit of its own (one HitId for the beat).
        bool gust   = w.FrameIn % StormWind.GustFrames(w.Phase) == 0 && hitboxes != null;
        int  gustId = gust ? spawner.HitIds.Next() : 0;
        var  kb     = new Vector2(dir.X, dir.Y - GustLift) * StormWind.GustImpulse(w.Phase);
        var  hurt   = spawner.Hurtboxes;
        if (hurt != null)
            foreach (var hb in hurt.All)
            {
                if (hb.Owner == Faction || hb.Target == Id) continue;
                float e = Exposure(hb.Region, out bool touched);
                if (!touched) continue;
                var body = spawner.ResolveBody(hb.Target);
                if (body != null) body.AppliedForce += dir * (StormWind.Accel(w.Phase) * e);
                if (gust)
                    hitboxes.Publish(new Hitbox(
                        hb.Region, gustId, StormWind.GustDamage(w.Phase), kb,
                        Faction, Id, RainColor,
                        targets: HitTargets.EntitiesOnly,
                        hitstunSecondsOverride: GustHitstun,
                        hitstopSecondsOverride: 0f));
            }

        // ...and on the same beat the wind wears at the tile each lane ends on.
        if (gust) Erode(hitboxes, spawner, _lanes, StormWind.Erode(w.Phase));
    }

    // The attacks that run on the weather's clock rather than the action FSM's, so they
    // overlap everything else: an ice field as each intense blow begins, and beams
    // through the calm. Both are entities that run themselves once spawned; all of the
    // dice are StormWind.Roll on the frame, so a replay throws the same ones.
    //
    // Beams: two slots on different periods, each beam alive TelegraphSeconds +
    // ActiveSeconds. The two are out of step, so through a 10 s calm there are 0 blowing
    // about 40 % of the time, otherwise 1 or 2. Slot 0 usually goes through the target;
    // slot 1 goes through a point near it. Each is stronger than the last through a calm.
    private const float IceDelaySeconds   = 0.2f;
    private const float Beam0Period       = 2.2f, Beam0Offset = 0.6f;
    private const float Beam1Period       = 3.1f, Beam1Offset = 1.6f;
    private const float BeamTargetChance  = 0.8f;
    private const float BeamPowerGain     = 0.6f;     // +60 % by the end of the calm

    private void UpdateSchedule(PlayerCharacter player, IEntitySpawner spawner)
    {
        var w = StormWind.At(FrameCount);
        var target = spawner.ResolveBody(CurrentTargetId)?.Position ?? player?.Body.Position ?? Body.Position;

        if (w.Phase == StormPhase.Intense && w.FrameIn == SimFrames.FromSeconds(IceDelaySeconds, Simulation.FixedDt))
            spawner.SpawnEntity(new StormIceField(target, FrameCount, Faction));

        if (w.Phase != StormPhase.Calm) return;
        int life = SimFrames.FromSeconds(StormBeam.TelegraphSeconds + StormBeam.ActiveSeconds, Simulation.FixedDt);
        for (int slot = 0; slot < 2; slot++)
        {
            int period = SimFrames.FromSeconds(slot == 0 ? Beam0Period : Beam1Period, Simulation.FixedDt);
            int k      = w.FrameIn - SimFrames.FromSeconds(slot == 0 ? Beam0Offset : Beam1Offset, Simulation.FixedDt);
            if (k < 0 || k % period != 0 || w.FrameIn + life > w.Frames) continue;   // none that would outlive the calm
            int n = k / period, key = w.Cycle * 64 + n * 2 + slot;

            // Semi-random heading: mostly down-left like the weather, anywhere from nearly
            // flat to nearly straight down, and one time in four mirrored to blow rightward.
            float a = MathHelper.ToRadians(8f + 74f * StormWind.Roll(key, 1, FrameCount));
            var   d = new Vector2(-MathF.Cos(a), MathF.Sin(a));
            if (StormWind.Roll(key, 2, FrameCount) < 0.25f) d.X = -d.X;
            var aim = target;
            if (slot == 1 || StormWind.Roll(key, 3, FrameCount) >= BeamTargetChance)
                aim += new Vector2((StormWind.Roll(key, 4, FrameCount) - 0.5f) * 360f,
                                   -StormWind.Roll(key, 5, FrameCount) * 60f);
            float power = 1f + BeamPowerGain * w.FrameIn / w.Frames;
            spawner.SpawnEntity(new StormBeam(aim, MathF.Atan2(d.Y, d.X), power, Faction));
        }
    }

    private void Erode(HitboxWorld hitboxes, IEntitySpawner spawner, StormWind.Lane[] lanes, float damage)
    {
        const float ts = Chunk.TileSize;
        int id = spawner.HitIds.Next();
        for (int i = 0; i < lanes.Length; i++)
        {
            if (!lanes[i].Live || !lanes[i].Blocked) continue;
            float bx = lanes[i].BlockX * ts, by = lanes[i].BlockY * ts;
            hitboxes.Publish(new Hitbox(
                new BoundingBox(bx + 2f, by + 2f, bx + ts - 2f, by + ts - 2f),
                id, damage, Vector2.Zero, Faction, Id, RainColor,
                targets: HitTargets.TilesOnly));
        }
    }

    // The wind column (StormcallerJetAction is its clock). Three lanes abreast on a line
    // through the locked aim point, born on the boss's source line — in the ceiling, if
    // that is where the line starts — and blocked by tiles like any other wind.
    private void UpdateJet(float dt, HitboxWorld hitboxes, IEntitySpawner spawner, ChunkMap chunks)
    {
        if (CurrentActionIndex != JetIdx) return;
        ref readonly var v = ref CurrentActionVars;
        var   dir  = StormcallerJetAction.Dir;
        var   aim  = v.LockedAim;
        float back = MathF.Max(0f, (aim.X - SourceX) / dir.X);       // distance upwind to the source line
        var   norm = new Vector2(-dir.Y, dir.X);
        for (int k = 0; k < JetLanes; k++)
            _jet[k] = StormWind.March(chunks,
                aim - dir * back + norm * ((k - StormcallerJetAction.HalfLanes) * Chunk.TileSize),
                dir, back + StormcallerJetAction.ReachPast, skipLeadingSolid: true);
        _jetValid = true;

        float ta = v.TimeInState - v.WindupDuration;                  // seconds into the active window
        if (ta < 0f || ta >= v.ActiveDuration) return;
        bool tick = hitboxes != null
                 && MathF.Floor(ta / StormcallerJetAction.TickSeconds) != MathF.Floor((ta - dt) / StormcallerJetAction.TickSeconds);
        int  id   = tick ? spawner.HitIds.Next() : 0;
        var  hurt = spawner.Hurtboxes;
        if (hurt != null)
            foreach (var hb in hurt.All)
            {
                if (hb.Owner == Faction || hb.Target == Id || !InJet(hb.Region)) continue;
                var body = spawner.ResolveBody(hb.Target);
                if (body != null) body.AppliedForce += dir * StormcallerJetAction.Accel;
                if (tick)
                    hitboxes.Publish(new Hitbox(
                        hb.Region, id, StormcallerJetAction.Damage, dir * StormcallerJetAction.Knockback,
                        Faction, Id, BoltColor,
                        targets: HitTargets.EntitiesOnly,
                        hitstopSecondsOverride: 0f));
            }
        if (tick) Erode(hitboxes, spawner, _jet, StormcallerJetAction.Erode);
    }

    private bool InJet(in BoundingBox r)
    {
        for (int k = 0; k < JetLanes; k++)
            if (StormWind.Touches(in _jet[k], StormcallerJetAction.Dir, in r, Chunk.TileSize * 0.5f)) return true;
        return false;
    }

    // ── Drawing (render-only; a pure function of the frame count and the scans) ──
    //   blowing   a faint wash over every live stretch of lane, rain driven along it, and
    //             spray on the face of each tile a lane breaks on — so the lee of a wall
    //             is a plainly darker, dry, still pocket. Intense is the same, louder:
    //             brighter wash, twice the rain, longer and faster and nearly white.
    //   warning   the last stretch of a calm flickers the lanes the next blow will use,
    //             at its heading: slow and blue for moderate, fast and white for intense.
    //   jet       windup: the column's outline, filling; active: a white torrent.
    private const int   RainStreaks = 900;
    private const float RainSpan    = 1500f;

    public override void Telegraph(TelegraphList t)
    {
        if (IsDead) return;
        float time  = FrameCount * Simulation.FixedDt;
        var   w     = StormWind.At(FrameCount);
        bool  hard  = w.Phase == StormPhase.Intense;
        // 0..1 swell into each gust while blowing; 0..1 ramp through a warning.
        int   beat  = StormWind.GustFrames(w.Phase);
        float swell = w.Blowing ? MathHelper.Clamp(1f - (beat - w.FrameIn % beat) * Simulation.FixedDt / 0.25f, 0f, 1f) : 0f;
        float warn  = w.Warning ? 1f - w.TimeLeft / StormWind.WarnSeconds : 0f;

        DrawFigure(t, time, swell, in w, warn);
        base.Telegraph(t);          // strike tells go over the figure
        if (_lanesValid) DrawGale(t, time, in w, hard, swell, warn);
        if (_jetValid)   DrawJet(t, time);
    }

    private void DrawGale(TelegraphList t, float time, in StormWind.State w, bool hard, float swell, float warn)
    {
        const float ts = Chunk.TileSize;
        var   dir  = _windDir;
        float rot  = MathF.Atan2(dir.Y, dir.X);
        var   tint = hard ? Color.Lerp(RainColor, BoltColor, 0.7f) : RainColor;

        // Wash. Blowing: steady, breathing with the gust. Warning: a flicker that
        // quickens and brightens as the blow arrives.
        float alpha = w.Blowing
            ? (hard ? 0.17f : 0.07f) + 0.06f * swell
            : 0.08f * warn * (0.5f + 0.5f * MathF.Sin(time * 14f));
        var wash = tint * alpha;
        // The intense blow announcing itself over a moderate one: a fast white flicker.
        var omen = w.Blowing && warn > 0f ? BoltColor * (0.16f * warn * (0.5f + 0.5f * MathF.Sin(time * 38f))) : Color.Transparent;
        for (int i = 0; i < WindRows; i++)
        {
            ref readonly var l = ref _lanes[i];
            if (!l.Live) continue;
            t.RotatedRect(l.Origin + dir * ((l.Start + l.End) * 0.5f), new Vector2(l.End - l.Start, ts + 0.5f), rot, wash);
            if (omen.A > 0) t.RotatedRect(l.Origin + dir * ((l.Start + l.End) * 0.5f), new Vector2(l.End - l.Start, ts + 0.5f), rot, omen);
        }

        // Rain: each streak rides one lane, drifting downwind and wrapping; it is drawn
        // only on the live stretch and cut off at the face. In a warning a thin vanguard
        // of it fades in.
        int   count = w.Blowing ? (hard ? RainStreaks : RainStreaks / 2) : (int)(RainStreaks * 0.12f * warn);
        float speed = hard ? 900f : 340f;
        float len   = (hard ? 20f : 8f) + 10f * swell;
        var   color = tint * (w.Blowing ? (hard ? 0.6f : 0.3f) + 0.35f * swell : 0.5f * warn);
        var   norm  = new Vector2(-dir.Y, dir.X);
        for (int i = 0; i < count; i++)
        {
            ref readonly var l = ref _lanes[i % WindRows];
            if (!l.Live) continue;
            float sp = 0.75f + 0.5f * Frac(i * 0.5698403f);
            float u  = Wrap(Frac(i * 0.6180339f) * RainSpan + speed * sp * time, RainSpan);
            if (u < l.Start || u >= l.End) continue;                 // in the lee: no rain
            var a = l.Origin + dir * u + norm * ((Frac(i * 0.7548777f) - 0.5f) * ts);
            t.Line(a, a + dir * MathF.Min(len, l.End - u), color, hard ? 1.5f : 1f);
        }
        if (!w.Blowing) return;

        // Spray where each lane breaks: a bright edge on the struck face and droplets
        // kicked back upwind off it.
        var spray = Color.Lerp(RainColor, Color.White, 0.5f) * (hard ? 1f : 0.55f + 0.45f * swell);
        for (int i = 0; i < WindRows; i++)
        {
            ref readonly var l = ref _lanes[i];
            if (!l.Live || !l.Blocked) continue;
            var face = l.Origin + dir * l.End;
            t.Line(face + new Vector2(0.5f, -ts * 0.5f), face + new Vector2(0.5f, ts * 0.5f), spray, hard ? 3.5f : 2.5f);
            for (int k = 0; k < (hard ? 4 : 2); k++)
            {
                float ph = Frac(time * (2.3f + 0.7f * k) * (hard ? 2f : 1f) + i * 0.37f + k * 0.5f);
                var   d  = face - dir * (1f + ph * (hard ? 14f : 6f))
                         + new Vector2(0f, (Frac(i * 0.61f + k * 0.43f) - 0.5f) * ts - ph * 4f);
                t.Rect(d, 1.5f, spray * (1f - ph));
            }
        }
    }

    private void DrawJet(TelegraphList t, float time)
    {
        const float ts = Chunk.TileSize;
        ref readonly var v = ref CurrentActionVars;
        var   dir  = StormcallerJetAction.Dir;
        var   norm = new Vector2(-dir.Y, dir.X);
        float rot  = MathF.Atan2(dir.Y, dir.X);
        float at   = v.TimeInState;
        bool  windup = at < v.WindupDuration;
        bool  active = !windup && at < v.WindupDuration + v.ActiveDuration;
        if (!windup && !active) return;
        float p = windup ? at / v.WindupDuration : 1f;

        // Fill: each lane's live stretch. Faint and rising through the windup, then solid.
        var fill = BoltColor * (windup ? 0.05f + 0.2f * p * (0.6f + 0.4f * MathF.Sin(time * (10f + 30f * p)))
                                       : 0.5f + 0.15f * MathF.Sin(time * 50f));
        for (int k = 0; k < JetLanes; k++)
        {
            ref readonly var l = ref _jet[k];
            if (!l.Live) continue;
            t.RotatedRect(l.Origin + dir * ((l.Start + l.End) * 0.5f), new Vector2(l.End - l.Start, ts + 0.5f), rot, fill);
        }

        // Outline of the area it will fill: both long edges, and a cap across every lane's
        // far end, so the outline shows exactly which tiles will stop it.
        var edge = Color.Lerp(RainColor, BoltColor, p) * (0.55f + 0.45f * p);
        ref readonly var lo = ref _jet[0];
        ref readonly var hi = ref _jet[JetLanes - 1];
        if (lo.Live) t.Line(lo.Origin - norm * (ts * 0.5f) + dir * lo.Start, lo.Origin - norm * (ts * 0.5f) + dir * lo.End, edge, 1.5f);
        if (hi.Live) t.Line(hi.Origin + norm * (ts * 0.5f) + dir * hi.Start, hi.Origin + norm * (ts * 0.5f) + dir * hi.End, edge, 1.5f);
        for (int k = 0; k < JetLanes; k++)
        {
            ref readonly var l = ref _jet[k];
            if (!l.Live) continue;
            var end = l.Origin + dir * l.End;
            t.Line(end - norm * (ts * 0.5f), end + norm * (ts * 0.5f), edge, 1.5f);
        }
        if (!active) return;

        // The torrent: long white streaks tearing down the column.
        for (int i = 0; i < 60; i++)
        {
            ref readonly var l = ref _jet[i % JetLanes];
            if (!l.Live) continue;
            float span = l.End - l.Start;
            float u = l.Start + Wrap(Frac(i * 0.6180339f) * span + 1400f * (0.8f + 0.4f * Frac(i * 0.57f)) * time, span);
            var a = l.Origin + dir * u + norm * ((Frac(i * 0.7548777f) - 0.5f) * ts);
            t.Line(a, a + dir * MathF.Min(26f, l.End - u), BoltColor * 0.9f, 1.5f);
        }
    }

    // ── The figure ──────────────────────────────────────────────────────────
    // Built from overlay primitives over the body outline and posed from the running
    // action's clock and the weather: a robed shape under a crown of storm cloud, one
    // eye, two arms, cloak tails that stream the way the wind blows, and motes circling.
    //   calm     tails hang limp; in a warning the cloud flickers (white before intense)
    //   blowing  tails stream downwind and whip on each gust — long and stiff in intense
    //   palm     the front arm draws back through the windup, then snaps out flat
    //   updraft  both arms pull in to the chest, shaking, then fling up and out; tails lift
    //   lob      the front arm rises overhead under the charge; the cloud darkens and
    //            throws a bolt into the hand as the bomb leaves, then the arm follows through
    //   jet      both arms rise through the windup, then drive down and forward and hold,
    //            shaking, for as long as the column runs
    private static readonly Color RobeColor  = new(45, 60, 95);
    private static readonly Color CloudColor = new(78, 92, 120);
    private static readonly Color BoltColor  = new(235, 245, 255);

    private void DrawFigure(TelegraphList t, float time, float swell, in StormWind.State w, float warn)
    {
        var   c = Body.Position;
        int   f = Facing == 0 ? -1 : Facing;
        int   act = CurrentActionIndex;
        ref readonly var v = ref CurrentActionVars;
        float at = v.TimeInState;
        float wp = act >= 0 && v.WindupDuration > 0f ? MathHelper.Clamp(at / v.WindupDuration, 0f, 1f) : 0f;
        bool  inWindup = act >= 0 && at < v.WindupDuration;
        bool  inActive = act >= 0 && !inWindup && at < v.WindupDuration + v.ActiveDuration;
        float rec = act >= 0 && v.RecoveryDuration > 0f
            ? MathHelper.Clamp((at - v.WindupDuration - v.ActiveDuration) / v.RecoveryDuration, 0f, 1f) : 0f;
        // How hard it is blowing, for the cloth: 0 calm, 1 moderate, 2 intense.
        float blow = w.Phase == StormPhase.Intense ? 2f : w.Phase == StormPhase.Moderate ? 1f : 0f;
        var   wind = StormWind.Direction(w.Heading);

        // Robe: a filled core inside the outline, and a hem that flares with the wind.
        t.Rect(c + new Vector2(0f, 2f), new Vector2(11f, 30f), RobeColor);
        t.RotatedRect(c + new Vector2(-2f * blow - 2f * swell, 15f), new Vector2(15f + 2f * blow + 3f * swell, 5f),
                      0.12f * blow * MathF.Sin(time * 5f), RobeColor);

        // Cloak tails: limp in a calm, streaming downwind otherwise. Lifted by the updraft.
        bool  lifted = act == UpdraftIdx && !inWindup && (inActive || rec < 0.35f);
        float amp    = 0.6f + 1.8f * blow + 3.5f * swell, reach = 4f + 1.6f * blow + 2f * swell;
        var   flow   = blow > 0f ? wind : new Vector2(-0.25f, 0.97f);
        for (int k = 0; k < 3; k++)
        {
            var root = c + new Vector2(-4f, -8f + 9f * k);
            var prev = root;
            for (int j = 1; j <= 5; j++)
            {
                float wave = MathF.Sin(time * (4f + 3f * blow) - j * 0.9f + k * 1.7f) * amp * j / 5f;
                var next = lifted ? root + new Vector2(-2f * j + wave, -reach * j)
                                  : root + flow * (reach * j) + new Vector2(-flow.Y, flow.X) * wave + new Vector2(0f, 0.3f * j * j);
                t.Line(prev, next, Color.Lerp(BodyColor, RobeColor, j / 5f), 2.5f - 0.3f * j);
                prev = next;
            }
        }

        // Arms. Hand offsets from the shoulder, x forward (facing-relative), y down.
        var idleFront = new Vector2(1.5f + MathF.Sin(time * 2.4f), 12f);
        var idleBack  = new Vector2(-1.5f + MathF.Sin(time * 2.4f + 1.3f) - 0.8f * blow - 1.5f * swell, 12f);
        Vector2 front = idleFront, back = idleBack;
        if (act == PalmIdx)
        {
            var drawn = new Vector2(-10f, -5f); var thrust = new Vector2(22f, 0f);
            front = inWindup ? Vector2.Lerp(idleFront, drawn, Ease(wp))
                  : inActive ? thrust
                  : Vector2.Lerp(thrust, idleFront, Ease(rec));
        }
        else if (act == UpdraftIdx)
        {
            var shake = new Vector2(MathF.Sin(time * 60f), MathF.Cos(time * 47f)) * (1.2f * wp);
            var chestF = new Vector2(1f, 3f) + shake; var chestB = new Vector2(3f, 5f) - shake;
            var outF = new Vector2(13f, -13f); var outB = new Vector2(-13f, -13f);
            front = inWindup ? Vector2.Lerp(idleFront, chestF, Ease(wp)) : inActive ? outF : Vector2.Lerp(outF, idleFront, Ease(rec));
            back  = inWindup ? Vector2.Lerp(idleBack,  chestB, Ease(wp)) : inActive ? outB : Vector2.Lerp(outB, idleBack,  Ease(rec));
        }
        else if (act == LobIdx)
        {
            var up = new Vector2(-4f, -StormcallerLobAction.MuzzleRise + 7f); var follow = new Vector2(14f, 3f);
            front = inWindup ? Vector2.Lerp(idleFront, up, Ease(wp))
                  : rec < 0.2f ? Vector2.Lerp(up, follow, Ease(rec / 0.2f))
                  : Vector2.Lerp(follow, idleFront, Ease((rec - 0.2f) / 0.8f));
        }
        else if (act == JetIdx)
        {
            var shake = inActive ? new Vector2(MathF.Sin(time * 70f), MathF.Cos(time * 53f)) * 1.3f : Vector2.Zero;
            Vector2 upF = new(-2f, -19f), upB = new(6f, -18f);
            Vector2 downF = new Vector2(19f, 6f) + shake, downB = new Vector2(21f, 1f) - shake;
            front = inWindup ? Vector2.Lerp(idleFront, upF, Ease(wp)) : inActive ? downF : Vector2.Lerp(downF, idleFront, Ease(rec));
            back  = inWindup ? Vector2.Lerp(idleBack,  upB, Ease(wp)) : inActive ? downB : Vector2.Lerp(downB, idleBack,  Ease(rec));
        }
        var shoulderF = c + new Vector2(f * 4f, -7f);
        var shoulderB = c + new Vector2(-f * 4f, -7f);
        var handB = shoulderB + new Vector2(f * back.X, back.Y);
        var handF = shoulderF + new Vector2(f * front.X, front.Y);
        t.Line(shoulderB, handB, RobeColor, 3f);
        t.Disc(handB, 2f, BodyColor);
        t.Line(shoulderF, handF, BodyColor, 3f);
        t.Disc(handF, 2.5f, inActive ? BoltColor : OpenColor);

        // Eye: looks where it faces, burns brighter as a windup completes.
        float glow = inWindup ? wp : inActive ? 1f : 0.25f + 0.15f * MathF.Sin(time * 3f);
        t.Disc(c + new Vector2(f * 2.5f, -11f), 2f + glow, Color.Lerp(RainColor, BoltColor, glow));

        // Crown of cloud: five lobes bobbing out of step, puffing on each gust; dark and
        // heavy through a lob's or a jet's windup; white for the instant a lob's bolt goes;
        // flickering through a warning, white if what is coming is the intense blow.
        bool  bolt  = act == LobIdx && !inWindup && at < v.WindupDuration + 0.12f;
        float dark  = act == LobIdx || act == JetIdx ? wp : 0f;
        var   cloud = Color.Lerp(CloudColor, new Color(40, 46, 66), dark);
        if (warn > 0f && MathF.Sin(time * (w.Next == StormPhase.Intense ? 38f : 14f)) > 0.2f)
            cloud = Color.Lerp(cloud, w.Next == StormPhase.Intense ? BoltColor : RainColor, 0.35f + 0.65f * warn);
        if (bolt || (w.Phase == StormPhase.Intense && Frac(time * 7f) < 0.15f)) cloud = BoltColor;
        var   crown = c + new Vector2(0f, -HalfHeight - 13f);
        float puff  = 0.7f * blow + 1.5f * swell;
        for (int i = 0; i < 5; i++)
        {
            float u   = i - 2f;
            float bob = MathF.Sin(time * 2.2f + i * 1.3f) * 1.4f;
            t.Disc(crown + new Vector2(u * 5.5f - puff, bob + MathF.Abs(u) * 1.5f),
                   6.5f - MathF.Abs(u) * 1.1f + puff, cloud);
        }
        if (bolt)
        {
            // A jagged line from the cloud into the raised hand; re-jittered every frame.
            var from = crown + new Vector2(0f, 4f);
            var prev = from;
            for (int j = 1; j <= 4; j++)
            {
                var next = Vector2.Lerp(from, handF, j / 4f);
                if (j < 4) next.X += (Frac(MathF.Sin(FrameCount * 12.9898f + j * 78.233f) * 43758.5453f) - 0.5f) * 9f;
                t.Line(prev, next, BoltColor, 2f);
                prev = next;
            }
        }

        // Motes circling the body on a flat ellipse, faster while it is winding something up.
        float spin = time * (3f + 5f * (inWindup ? wp : 0f));
        for (int i = 0; i < 4; i++)
        {
            float a = spin + i * MathHelper.PiOver2;
            t.Disc(c + new Vector2(MathF.Cos(a) * 17f, 4f + MathF.Sin(a) * 6f),
                   1.2f + 0.5f * MathF.Sin(a), RainColor * (0.55f + 0.35f * MathF.Sin(a)));
        }
    }

    private static float Ease(float x) { x = MathHelper.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }
    private static float Frac(float v) => v - MathF.Floor(v);
    private static float Wrap(float v, float span) => span <= 0f ? 0f : v - MathF.Floor(v / span) * span;
}
