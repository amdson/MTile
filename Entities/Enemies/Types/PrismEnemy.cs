using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  PRISM — a player-sized boss that moves only by dashing and fights at range
//  through the ray emitters it plants.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it:  set "Stage": "prism" in configs/game_config.json, then
//          dotnet run --project MTile.Desktop
//
//   DASH   (PrismDashAction)   its only way of moving. A short, telegraphed, fast
//                              burst in a straight line AT you — along the floor, up a
//                              ledge, up a wall you're climbing; gravity is off for its
//                              few frames. The body is the hitbox: it hurts, and it
//                              breaks soft blocks (sand, foam, dirt) in its path. A dash
//                              that ends in the air falls, and the landing is a small
//                              shockwave.
//   PLANT  (PrismPlantAction)  conjures a RayEmitter beside and above you, aimed at
//                              you. Two slots — the far side of you from the boss and
//                              the near side — so the two cones cross on you and cover
//                              the ground both ways: the box. The emitter telegraphs a
//                              silhouette of its cone, then fires a long ray that blocks
//                              stop — and that slowly eats the blocks that stop it. It
//                              is tough but killable, and if it lives out its life it
//                              explodes. Getting out means killing one or building.
//
// The brain is stateless (EnemyController's contract); its memory — cooldowns and the
// commit-edge detector — lives in EnemyEntity.Scratch, which is snapshotted, the same
// way WarlordController keeps time. The dash's four phases (windup → dash → fall →
// landing) sit on one clock, and the clock is snapped forward when the ground arrives
// (the Warlord leap's trick), so there is no "landed" flag to snapshot.
//
// Numbers here are first guesses, deliberately untuned.


// ── Brain ───────────────────────────────────────────────────────────────────
public sealed class PrismController : EnemyController
{
    // Action indices — must match the order PrismEnemy's ctor registers them.
    public const int Dash = 0, PlantFar = 1, PlantNear = 2;

    public float AlertRange    { get; init; } = 900f;
    // Dash whenever the target is at least this far away in any direction.
    public float DashMin       { get; init; } = 36f;
    public float PlantMax      { get; init; } = 560f;
    public int   MaxLiveRays   { get; init; } = 2;
    public float DashCooldown  { get; init; } = 0.6f;
    public float PlantCooldown { get; init; } = 2.0f;

    // BrainScratch layout:
    //   I0  last frame's action index + 1          F0  dash cooldown remaining, s
    //   F3  last frame's action clock, s            F1  plant cooldown remaining, s
    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var self = ctx.Self;
        ref var m = ref self.Scratch;
        float dt = ctx.Dt;

        // Commit edge: a start is the index changing OR the clock running backwards (a
        // finished action re-entered inside one SelectAction never shows an idle frame).
        int   action = self.CurrentActionIndex;
        float clock  = self.CurrentActionTime;
        if (action >= 0 && (action != m.I0 - 1 || clock < m.F3))
        {
            if (action == Dash) m.F0 = DashCooldown;
            else                m.F1 = PlantCooldown;
        }
        m.I0 = action + 1;
        m.F3 = clock;
        if (m.F0 > 0f) m.F0 = MathF.Max(0f, m.F0 - dt);
        if (m.F1 > 0f) m.F1 = MathF.Max(0f, m.F1 - dt);

        var input = new EnemyInput { AimWorld = ctx.Target.Position };
        if (ctx.Dist > AlertRange) return input;

        // The box: which of the two slots is empty (far first). A slot is filled by a
        // live emitter of ours on that side of the target.
        int side = ctx.ToPlayer.X >= 0f ? 1 : -1;          // "far" is further along this
        bool farOpen = true, nearOpen = true;
        Span<RayView> views = stackalloc RayView[16];
        int n = ctx.Spawner?.Targets?.Rays(views) ?? 0, live = 0;
        for (int i = 0; i < n; i++)
        {
            if (views[i].Owner != self.Id) continue;
            live++;
            if ((views[i].Position.X - ctx.Target.Position.X) * side >= 0f) farOpen = false;
            else nearOpen = false;
        }
        bool plantReady = m.F1 <= 0f && live < MaxLiveRays && ctx.Dist <= PlantMax && (farOpen || nearOpen);
        bool dashReady  = m.F0 <= 0f && ctx.Dist >= DashMin;
        int  plant      = farOpen ? PlantFar : PlantNear;

        // Pressure: with no box up, build it; otherwise the dash comes first and the
        // box is completed between dashes.
        int want = -1;
        if (plantReady && (live == 0 || !dashReady)) want = plant;
        else if (dashReady)                          want = Dash;

        if (want >= 0)
        {
            input.WantAttack      = true;
            input.RequestedAction = want;
        }
        return input;
    }

    // Live emitters this boss planted.
    public static int LiveRays(in EnemyContext ctx)
    {
        Span<RayView> views = stackalloc RayView[16];
        int n = ctx.Spawner?.Targets?.Rays(views) ?? 0, mine = 0;
        for (int i = 0; i < n; i++) if (views[i].Owner == ctx.Self.Id) mine++;
        return mine;
    }
}


// ── Legs ────────────────────────────────────────────────────────────────────
// The body is a tall, slim figure standing on its feet, so "anchored" is asked under
// the feet — three samples just below the sole — rather than on a ring around the
// centre (which, at this height, would read a wall two tiles away as ground).
internal static class PrismProbe
{
    private const float Under = 2.5f;
    public static bool Anchored(in EnemyContext ctx)
    {
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var p = ctx.Self.Body.Position;
        float y = p.Y + PrismEnemy.FootDrop + Under;
        return TileQuery.IsSolidAt(chunks, p.X, y)
            || TileQuery.IsSolidAt(chunks, p.X - PrismEnemy.HalfWidth, y)
            || TileQuery.IsSolidAt(chunks, p.X + PrismEnemy.HalfWidth, y);
    }
    // Anchored, or at rest: nothing rests in mid-air under gravity (see WarlordProbe).
    public static bool Grounded(in EnemyContext ctx)
        => Anchored(ctx) || ctx.Self.Body.Velocity.LengthSquared() < 1f;
}

// Off the ground with no action in flight: write nothing, so knockback plays out.
public class PrismAirborneState : EnemyMovementState
{
    public override int ActivePriority  => 10;
    public override int PassivePriority => 8;
    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && !PrismProbe.Grounded(ctx);
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && !PrismProbe.Grounded(ctx);
    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v) => v.TimeInState += ctx.Dt;
}

// Plant the feet while an action is committed — only on the ground and not rising, so
// a dash that runs off an edge keeps its momentum through the fall. The dash action
// runs after this and overwrites X on its own frames anyway.
public class PrismHoldState : EnemyMovementState
{
    public override int ActivePriority  => 40;
    public override int PassivePriority => 35;
    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Self.IsActionCommitted;
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v) => ctx.Self.IsActionCommitted;
    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        var body = ctx.Self.Body;
        if (body.Velocity.Y > -20f && PrismProbe.Grounded(ctx))
            body.Velocity.X *= 0.6f;
    }
}


// ── Arms: dash ──────────────────────────────────────────────────────────────
// Timeline (t = TimeInState):
//   [0, Windup)                  crouch; the lane and its end are drawn
//   [Windup, dashEnd)            Speed px/s along the locked direction, gravity off,
//                                body hitbox live
//   the frame t crosses dashEnd  grounded ⇒ clock snaps to the recovery (no landing);
//                                airborne ⇒ fall
//   [dashEnd, fallEnd)           fall; the anchor probe firing SNAPS the clock to the
//                                landing
//   [fallEnd, fallEnd + Slam)    landing shockwave (tile-carving on its first frame)
//   then Recovery
// LockedAim is the UNIT direction, frozen at Enter (the player's position then);
// LockedFacing is its horizontal sign. The lane the telegraph draws is Speed·Dash
// along it from the body.
public class PrismDashAction : EnemyActionState
{
    protected virtual float   Windup      => 0.30f;
    protected virtual float   Dash        => 0.20f;
    protected virtual float   MaxFall     => 1.40f;
    protected virtual float   Slam        => 0.12f;
    protected virtual float   Recovery    => 0.35f;
    protected virtual float   Speed       => 560f;        // × 0.20 s ≈ 112 px

    // The body's own silhouette, padded a little sideways.
    protected virtual float   HalfWidth   => PrismEnemy.HalfWidth + 3f;
    protected virtual float   Top         => -PrismEnemy.HeadUp;
    protected virtual float   Bottom      => PrismEnemy.FootDrop;
    // Per overlapped frame: sand/foam (0.5) go at once, dirt (2.0) on the second frame,
    // stone (12) is a wall.
    protected virtual float   TileDamage  => 1.0f;
    protected virtual float   BodyDamage  => 1.0f;
    protected virtual Vector2 Knockback   => new(440f, -170f);

    public virtual  bool      LandingSlam => true;
    protected virtual float   ShockHalfWidth => 40f;
    // The wave sits at the feet: from ShockUp above the sole to ShockDown below it.
    protected virtual float   ShockUp        => 10f;
    protected virtual float   ShockDown      => 12f;
    protected virtual float   SlamDamage     => 1.2f;
    protected virtual Vector2 SlamKnockback  => new(420f, -260f);
    protected virtual float   SlamTileDamage => 1.0f;
    protected virtual float   SlamTileInset  => PrismEnemy.HalfWidth + 4f;

    public override int ActivePriority  => PrismEnemy.ActionActive;
    public override int PassivePriority => 27;

    private float DashEnd => Windup + Dash;
    private float FallEnd => DashEnd + MaxFall;
    private float Active  => Dash + MaxFall + Slam;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => MathF.Abs(ctx.Self.Body.Velocity.Y) < 30f && PrismProbe.Grounded(ctx);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        var to = ctx.ToPlayer;
        v.LockedAim    = to.LengthSquared() > 1f ? Vector2.Normalize(to)
                       : new Vector2(ctx.Facing == 0 ? 1 : ctx.Facing, 0f);
        v.LockedFacing = v.LockedAim.X > 0.001f ? 1 : v.LockedAim.X < -0.001f ? -1 : (ctx.Facing == 0 ? 1 : ctx.Facing);
        v.HitId        = ctx.Spawner.HitIds.Next();
        ctx.Spawner.HitIds.Next();                    // reserved: the landing is HitId + 1
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.Committed = false;
        ctx.Self.GravityScale = 1f;                   // never leave the body weightless
    }

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Windup;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        var self = ctx.Self;
        var body = self.Body;
        float dashEnd = DashEnd, fallEnd = FallEnd;

        if (t < v.WindupDuration) { body.Velocity.X *= 0.7f; return; }

        if (t < dashEnd)
        {
            // Weightless and on rails for the burst, so an upward dash is a straight
            // line rather than an arc. GravityScale rides EntityData, so a rollback
            // restores it with the clock.
            self.GravityScale = 0f;
            body.Velocity     = v.LockedAim * Speed;
            var p = body.Position;
            ctx.Hitboxes?.Publish(new Hitbox(
                new BoundingBox(p.X - HalfWidth, p.Y + Top, p.X + HalfWidth, p.Y + Bottom),
                v.HitId, TileDamage, new Vector2(v.LockedFacing * Knockback.X, Knockback.Y),
                Faction.Enemy, self.Id, PrismEnemy.Beam,
                targets: HitTargets.All, bodyDamage: BodyDamage * self.Strength, origin: p));
            return;
        }

        if (prev < dashEnd)
        {
            self.GravityScale = 1f;
            body.Velocity     = v.LockedAim * Speed * 0.25f;      // skid
            // On the ground at the end of the lane: straight to the recovery. In the
            // air: fall, and land (with the slam) when the ground arrives.
            if (PrismProbe.Grounded(ctx) || !LandingSlam)
                v.TimeInState = v.WindupDuration + v.ActiveDuration;
            return;
        }

        if (t < fallEnd)
        {
            if (body.Velocity.Y > -10f && PrismProbe.Anchored(ctx))
                v.TimeInState = fallEnd - 0.5f * ctx.Dt;         // next frame is the landing's first
            return;
        }

        if (t < fallEnd + Slam)
        {
            body.Velocity.X = 0f;
            var p = body.Position;
            float sole = p.Y + PrismEnemy.FootDrop;
            var region = new BoundingBox(p.X - ShockHalfWidth, sole - ShockUp, p.X + ShockHalfWidth, sole + ShockDown);
            float side = ctx.ToPlayer.X >= 0f ? 1f : -1f;
            ctx.Hitboxes?.Publish(new Hitbox(
                region, v.HitId + 1, SlamDamage * self.Strength,
                new Vector2(side * SlamKnockback.X, SlamKnockback.Y),
                Faction.Enemy, self.Id, PrismEnemy.Beam, targets: HitTargets.EntitiesOnly));
            if (prev < fallEnd)
            {
                var l = new BoundingBox(p.X - ShockHalfWidth, sole - ShockUp, p.X - SlamTileInset, sole + ShockDown);
                var r = new BoundingBox(p.X + SlamTileInset,  sole - ShockUp, p.X + ShockHalfWidth, sole + ShockDown);
                ctx.Hitboxes?.Publish(new Hitbox(l, v.HitId + 1, SlamTileDamage, Vector2.Zero,
                    Faction.Enemy, self.Id, PrismEnemy.Pale, targets: HitTargets.TilesOnly));
                ctx.Hitboxes?.Publish(new Hitbox(r, v.HitId + 1, SlamTileDamage, Vector2.Zero,
                    Faction.Enemy, self.Id, PrismEnemy.Pale, targets: HitTargets.TilesOnly));
            }
        }
    }

    // Which pose the figure holds at clock `t` (render-only; PrismEnemy.SyncSprite).
    public PrismFigurePose PoseAt(float t, in EnemyActionVars v, PhysicsBody body)
    {
        if (t < v.WindupDuration)         return PrismFigurePose.Crouch;
        if (t < DashEnd)                  return PrismFigurePose.Dash;
        if (t < FallEnd)                  return body.Velocity.Y > 40f ? PrismFigurePose.Fall : PrismFigurePose.Dash;
        if (t < FallEnd + Slam)           return PrismFigurePose.Land;
        float recover = t - (v.WindupDuration + v.ActiveDuration);
        return recover < Recovery * 0.5f ? PrismFigurePose.Land : PrismFigurePose.Idle;
    }

    // Windup: the lane along the floor to its planned end, filling in, with the end
    // box outlined and a strobe at the finish. Dash: speed lines. Fall: a danger mark
    // under the body. Landing: the wave.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var pos = body.Position;
        int f   = v.LockedFacing;
        float time = v.TimeInState;
        float dashEnd = DashEnd, fallEnd = FallEnd;

        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            var   dir  = v.LockedAim;
            float lane = Speed * Dash;
            float ang  = MathF.Atan2(dir.Y, dir.X);
            var   mid  = pos + new Vector2(0f, (Top + Bottom) * 0.5f);
            float pulse = p < 0.6f ? 1f : 0.6f + 0.4f * MathF.Abs(MathF.Sin(time * 34f));
            // The lane: the body's height swept along the dash vector, filling in from
            // the body, with the planned end ringed.
            t.RotatedRect(mid + dir * (lane * 0.5f), new Vector2(lane, Bottom - Top), ang, PrismEnemy.Beam * (0.06f + 0.14f * p));
            t.RotatedRect(mid + dir * (lane * p * 0.5f), new Vector2(lane * p, 3f), ang, PrismEnemy.Beam * (0.4f + 0.5f * p) * pulse);
            t.Ring(mid + dir * lane, HalfWidth + 4f, PrismEnemy.Beam * (0.3f + 0.7f * p), 10, 1f + p);
            t.Ring(pos + new Vector2(0f, Bottom - 4f), 12f - 7f * p, PrismEnemy.Beam * (0.3f + 0.7f * p), 12, 1f + p);
            return;
        }
        if (time < dashEnd)
        {
            // Speed lines trailing the dash vector.
            var dir = v.LockedAim;
            var perp = new Vector2(-dir.Y, dir.X);
            for (int i = 0; i < 4; i++)
            {
                float len = 18f - i * 4f;
                var a = pos - dir * (10f + i * 7f) + perp * (-8f + i * 5f);
                t.Line(a, a - dir * len, PrismEnemy.Beam * (0.7f - i * 0.15f), 2f);
            }
            t.Line(pos + dir * (PrismEnemy.HalfWidth + 1f) + new Vector2(0f, Top), pos + dir * (PrismEnemy.HalfWidth + 1f) + new Vector2(0f, Bottom), PrismEnemy.Pale * 0.5f, 4f);
            return;
        }
        if (time < fallEnd)
        {
            if (body.Velocity.Y > 60f)
                t.Box(pos.X - 10f, pos.Y + Bottom - 4f, 20f, 10f, PrismEnemy.Beam * 0.35f);
            return;
        }
        if (time < fallEnd + Slam)
        {
            float p = (time - fallEnd) / MathF.Max(Slam, 1e-3f);
            float reach = ShockHalfWidth * (0.3f + 0.7f * p);
            float sole = pos.Y + PrismEnemy.FootDrop;
            t.Box(pos.X - reach, sole - ShockUp, reach * 2f, ShockUp + ShockDown, PrismEnemy.Beam * (0.6f - 0.3f * p));
            t.Ring(new Vector2(pos.X, sole), reach, Color.Lerp(PrismEnemy.Beam, Color.White, p), 16, 2f);
        }
    }
}


// ── Arms: plant a ray emitter ───────────────────────────────────────────────
// One slot of the box: the emitter goes SideOffset px beside the TARGET — on the far
// side of it from the boss (Side = +1) or the near side (−1) — and Rise px above,
// chosen at Enter (LockedAim, an absolute point) and shown through the windup. It
// appears on the windup→active transition, aimed at wherever the target IS on that
// frame; its own silhouette is the real tell. A destination inside rock is nudged
// upward a few cells, and fizzles if that doesn't find air.
public class PrismPlantAction : EnemyActionState
{
    public PrismPlantAction(int side) { Side = side; }
    public int Side { get; }

    protected virtual float Windup     => 0.55f;
    protected virtual float Active     => 0.05f;
    protected virtual float Recovery   => 0.45f;
    protected virtual float SideOffset => 110f;
    protected virtual float Rise       => 70f;
    protected virtual int   NudgeCells => 6;

    // The ray's parameters. Wide: the two cones have to cross on the target and
    // between them cover the ground on both sides of it.
    public virtual float HalfAngleDegrees => 30f;
    public virtual float RayRange         => 900f;
    public virtual float RayTelegraph     => 1.0f;
    public virtual float RayLife          => 7.0f;

    public override int ActivePriority  => PrismEnemy.ActionActive;
    public override int PassivePriority => 29;

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Spawner != null;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.Committed    = true;
        v.LockedAim    = PlantPoint(ctx);
        PopulateDurations(ref v);
    }

    private Vector2 PlantPoint(in EnemyContext ctx)
    {
        int away = ctx.ToPlayer.X >= 0f ? 1 : -1;            // from the boss, past the target
        var at = ctx.Target.Position + new Vector2(away * Side * SideOffset, -Rise);
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return at;
        for (int k = 0; k <= NudgeCells; k++)
        {
            var q = at - new Vector2(0f, k * Chunk.TileSize);
            if (!TileQuery.IsSolidAt(chunks, q.X, q.Y)) return q;
        }
        return at;      // buried: Update fizzles
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
        float prev = v.TimeInState;
        v.TimeInState += ctx.Dt;
        if (!(prev < v.WindupDuration && v.TimeInState >= v.WindupDuration)) return;
        var at = v.LockedAim;
        var chunks = ctx.Spawner?.Chunks;
        if (chunks != null && TileQuery.IsSolidAt(chunks, at.X, at.Y)) return;   // fizzles inside rock
        var self = ctx.Self;
        var aim  = ctx.Target.Position - at;
        float angle = aim.LengthSquared() > 1e-4f ? MathF.Atan2(aim.Y, aim.X) : (v.LockedFacing > 0 ? 0f : MathF.PI);
        ctx.Spawner?.SpawnEntity(new RayEmitter(at, self.Id, self.Team, angle,
            MathHelper.ToRadians(HalfAngleDegrees), RayRange, RayTelegraph, RayLife));
    }

    // Windup: a marker at the plant point that tightens and brightens, a thread from
    // the body to it, and a charge over the head.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f || v.TimeInState >= v.WindupDuration) return;
        float p = v.TimeInState / v.WindupDuration;
        var at = v.LockedAim;
        var c  = PrismEnemy.Ray;
        t.Line(body.Position, at, c * (0.15f + 0.35f * p), 1f);
        t.Ring(at, RayEmitter.BodyRadius + 12f - 10f * p, c * (0.3f + 0.7f * p), 12, 1f + p);
        t.Rect(at, 2f + 3f * p, Color.Lerp(c, Color.White, p));
        t.Disc(body.Position + new Vector2(0f, -PrismEnemy.HeadUp - 6f), 2f + 3f * p, Color.Lerp(new Color(c, 80), c, p));
    }
}


// ── Body ────────────────────────────────────────────────────────────────────
public sealed class PrismEnemy : EnemyEntity
{
    // The player's standing envelope, as a body: a slim hexagon from the head top
    // (HeadUp above the centre) down to the soles (FootDrop below it), the player's
    // width. It stands on its bottom vertex like the Warden; there is no corrector
    // under it, so the feet are the polygon rather than a hover.
    public const float HeadUp    = PlayerCharacter.Radius;
    public static readonly float FootDrop  = PlayerCharacter.StandingHeight - PlayerCharacter.Radius;
    public static readonly float HalfWidth = PlayerCharacter.Radius * 0.8660254f * PlayerCharacter.BodyWidthScale;
    public const float MaxHp  = 12f;
    // Shared Active priority of both actions: above their Passives (27, 29), so a
    // committed one is never replaced mid-swing.
    public const int   ActionActive = 40;

    public static readonly Color Beam = new(90, 220, 255);    // the body's attacks
    public static readonly Color Ray  = new(200, 120, 255);   // anything to do with emitters
    public static readonly Color Pale = new(230, 240, 255);

    public override EntityKind Kind        => EntityKind.Prism;
    public override string     DisplayName => "Prism";

    private static Polygon CreateBodyPolygon()
    {
        float w = HalfWidth, top = -HeadUp, bot = FootDrop;
        float shoulder = top + 0.25f * (bot - top), hip = top + 0.75f * (bot - top);
        return new Polygon(new[]
        {
            new Vector2(0f, top), new Vector2(w, shoulder), new Vector2(w, hip),
            new Vector2(0f, bot), new Vector2(-w, hip), new Vector2(-w, shoulder),
        });
    }

    private readonly PrismFigure     _figure = Sprites.Prism();
    private readonly PrismDashAction _dash   = new();
    private readonly AnimatedSprite _sprite = new();
    private PrismFigurePose _pose = (PrismFigurePose)(-1);
    private int             _poseFacing;

    public PrismEnemy(Vector2 pos)
        : base(new PhysicsBody(CreateBodyPolygon(), pos),
               health: MaxHp,
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),          // 0 — fallback (brakes X: it does not walk)
                   new PrismAirborneState(),      // 1
                   new PrismHoldState(),          // 2 — plant while attacking (fall-aware)
                   // No stagger: boss poise. Hitstop still lands on big hits.
               },
               actions: new List<EnemyActionState>
               {
                   new PrismDashAction(),         // 0  PrismController.Dash (_dash is a second, stateless copy for SyncSprite)
                   new PrismPlantAction(+1),      // 1  PrismController.PlantFar
                   new PrismPlantAction(-1),      // 2  PrismController.PlantNear
               },
               controller: new PrismController())
    {
        Mass               = 3f;
        Armor              = 1f;
        Body.FrictionScale = 0.15f;
        Color              = Beam;
        Sprite             = _sprite;
    }

    // Render-only: pick the figure's pose from the action in flight. Re-derived every
    // rendered frame from snapshotted state, so a rollback needs nothing.
    public override void SyncSprite()
    {
        base.SyncSprite();
        var pose = PrismFigurePose.Idle;
        if (CurrentActionIndex == PrismController.Dash)
            pose = _dash.PoseAt(CurrentActionTime, in CurrentActionVars, Body);
        else if (CurrentActionIndex >= PrismController.PlantFar && CurrentActionTime < CurrentActionVars.WindupDuration)
            pose = PrismFigurePose.Cast;
        int facing = Facing == 0 ? 1 : Facing;
        if (pose == _pose && facing == _poseFacing) return;
        _pose = pose; _poseFacing = facing;
        _sprite.Play(_figure.Get(pose, facing));
    }
}
