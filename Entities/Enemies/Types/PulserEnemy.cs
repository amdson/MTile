using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  PULSER — a one-trick crawler: it creeps along terrain toward you and, every
//  few seconds, charges and releases one big pulse that terrain blocks.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it: set "Stage": "pulser" in game_config.json.
//
// The read: a slow, tough lump that sticks to whatever ground it touches — floor,
// wall, ceiling, around corners — with gravity off while it's touching, so it
// can come at you from any surface-adjacent direction. Once it sees you inside
// its trigger range and its cooldown is up it plants, and a ring grows out to the
// pulse's full radius over a long windup. On release, everything inside that
// radius WITH A CLEAR LINE to its centre is hurt and thrown outward.
//
// Ways out, all intended:
//   • Leave the circle. The windup is long enough to walk out from most of it,
//     not from point-blank.
//   • Put terrain between you and it. The pulse is occluded like ChargedBlast's
//     body channel (origin = its centre), so a block, a ledge lip, a pit you dug,
//     or a wall you just placed is full cover.
//   • Punish the cooldown. Between pulses it is just a slow body with a lot of HP.
//
// It does not flinch: there is no stagger state, so a hit costs HP but never
// cancels the charge or knocks it off its surface. Trading into the windup is
// therefore a choice, not a counter.
//
// Pacing lives in Scratch.F0 (cooldown, s), ticked by PulserEnemy.Update and
// started by the pulse's first active frame — the Spearman's regrow pattern, so
// it outlives the action and snapshots with the entity.
//
// Built on the TemplateEnemy pattern (brain / legs / arms); see that file for the
// rules every piece here follows.

// ── Brain ───────────────────────────────────────────────────────────────────
// Head straight for the player in 2D and settle once close; the crawl state turns
// that into whichever surface-hugging direction gets nearest to it.
public sealed class PulserController : EnemyController
{
    // Close enough that it stops pushing in. Well inside the pulse radius: the
    // whole point is to have you in the circle when it goes off.
    public float PreferredRange { get; init; } = 32f;
    public float AlertRange     { get; init; } = 420f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var input = new EnemyInput
        {
            AimWorld   = ctx.Target.Position,
            // The pulse gates itself (cooldown, range, sight).
            WantAttack = true,
        };
        if (ctx.Dist > AlertRange || ctx.Dist <= PreferredRange) return input;

        // Barriers: when the way to the player is blocked, go up and over it instead
        // of pushing into it. The crawl turns "up, a bit toward them" into climbing
        // the near face, and once the top clears the barrier both tests pass again
        // and it heads straight for them down the far side.
        int side = ctx.ToPlayer.X >= 0f ? 1 : -1;
        input.MoveDir = Blocked(in ctx, side)
            ? Vector2.Normalize(new Vector2(side * OverLean, -1f))
            : ctx.ToPlayer / ctx.Dist;
        return input;
    }

    // Sideways component of the "up and over" request, per unit of up.
    private const float OverLean = 0.5f;

    // Blocked = it can't see the player, or there's terrain right against its body's
    // face on the player's side — a low barrier the sight line passes over, or the lip
    // its flank is still caught on after its centre has cleared the top.
    private static bool Blocked(in EnemyContext ctx, int side)
    {
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var pos = ctx.Self.Body.Position;
        if (!EnemyAim.HasLineOfSight(pos, ctx.Target.Position, chunks, 8f)) return true;
        float x = pos.X + side * (PulserEnemy.BodyRadius + 2f);
        float h = PulserEnemy.BodyRadius * 0.7f;
        return TileQuery.IsSolidAt(chunks, x, pos.Y - h)
            || TileQuery.IsSolidAt(chunks, x, pos.Y)
            || TileQuery.IsSolidAt(chunks, x, pos.Y + h);
    }
}

// ── Legs ────────────────────────────────────────────────────────────────────
// The stock cling crawl (gravity off while anchored; picks the anchored direction
// closest to the brain's request; roots while the pulse is committed), slowed.
// Off every surface it drops to Idle and falls under gravity until it touches
// something again.
public sealed class PulserCrawlState : EnemyClingMoveState
{
    protected override float Speed        => 30f;
    // Body radius is 11, so this is the stock cling's ~3 px of slack.
    protected override float AnchorDist   => 14f;
    // Just below zero, so a sideways crawl is allowed when nothing points closer —
    // with the player straight across a wall, "along the wall" beats sitting still.
    protected override float DotThreshold => -0.05f;

    // A direction is open when the body's leading face would not be inside a tile:
    // the point straight ahead plus two either side of it, so a corner grazing the
    // body's flank counts too. Without this it picks "into the wall" whenever the
    // player is on the far side and stalls against it instead of climbing.
    protected override bool IsOpen(Vector2 landing, Vector2 dir, in EnemyContext ctx)
    {
        var chunks = ctx.Spawner.Chunks;
        var ahead  = landing + dir * (PulserEnemy.BodyRadius + 1.5f);
        var side   = new Vector2(-dir.Y, dir.X) * (PulserEnemy.BodyRadius * 0.7f);
        return !Solid(chunks, ahead) && !Solid(chunks, ahead + side) && !Solid(chunks, ahead - side);
    }

    private static bool Solid(ChunkMap chunks, Vector2 p) => TileQuery.IsSolidAt(chunks, p.X, p.Y);
}

// ── Arms: the pulse ─────────────────────────────────────────────────────────
// Charge (a ring grows from the body out to the full radius; the outer edge is
// drawn from frame one so WHERE is never in question) → Release (one short live
// window: a disc of wedge hitboxes, each throwing outward along its own radius)
// → Recovery (spent, planted). Starting the cooldown is the release's job.
public sealed class PulserBurstAction : EnemyActionState
{
    private const float Charge   = 1.20f;
    private const float Active   = 0.10f;
    private const float Recovery = 0.55f;

    // The pulse's reach from its centre. "Large": fifteen tiles across.
    public  const float Radius       = 120f;
    // Starts a charge when the target is this close. Inside the radius by enough
    // that someone who just walks in can't stand on the edge and wait it out.
    public  const float TriggerRange = 96f;

    // Body HP off whoever it reaches: under a third of the pool, but it's the only
    // thing this enemy does, so it has to matter.
    private const float BodyDamage = 1.5f;
    // Impulse-mode knockback (Δv·mass), outward along each wedge's radius with a
    // little lift so a body on the same floor is thrown clear rather than slid.
    private const float Knockback  = 650f;
    private const float Lift       = 0.35f;

    // A disc built from Segments convex wedges in local space (centre + an arc of
    // ArcPoints), so each can carry its own outward knockback. Immutable after
    // construction, so sharing them across instances is fine.
    private const int Segments  = 16;
    private const int ArcPoints = 4;
    private static readonly Polygon[] Wedges   = BuildWedges();
    private static readonly Vector2[] WedgeDir = BuildWedgeDirs();

    private static readonly Color ChargeColor  = new(190, 120, 255);
    private static readonly Color FlashColor   = Color.White;
    private static readonly Color ReleaseColor = new(255, 90, 200);

    public override int ActivePriority  => 34;
    public override int PassivePriority => 30;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (ctx.Self is not PulserEnemy p || !p.PulseReady) return false;
        if (ctx.Dist > TriggerRange) return false;
        return EnemyAim.HasLineOfSight(ctx.Self.Body.Position, ctx.Target.Position, ctx.Spawner?.Chunks, 8f);
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Charge;
        v.ActiveDuration   = Active;
        v.RecoveryDuration = Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState;
        if (t < v.WindupDuration || t >= v.WindupDuration + v.ActiveDuration) return;

        var center = ctx.Self.Body.Position;
        if (ctx.Self is PulserEnemy p && p.StartCooldown())
            ctx.Spawner?.NotifyChargedBlast(ctx.Self.Id, center, Radius);   // particles + sound, once

        // One HitId across every wedge, so a body overlapping several is hit once.
        // origin = the centre: CombatSystem drops any hurtbox without a clear line
        // to it, which is the whole terrain-blocking rule.
        for (int i = 0; i < Segments; i++)
        {
            var poly = Wedges[i];
            ctx.Hitboxes?.Publish(new Hitbox(
                poly.GetBoundingBox(center, 0f), v.HitId, BodyDamage * ctx.Self.Strength,
                WedgeDir[i] * Knockback, Faction.Enemy, ctx.Self.Id, ReleaseColor,
                targets: HitTargets.EntitiesOnly,
                shape: poly, shapePos: center, shapeRotation: 0f,
                origin: center));
        }
    }

    // The outer edge at full radius from the first frame; an inner ring grows out
    // to meet it; the last quarter-second flashes. Release is a burst of rings,
    // recovery a fading echo.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        var   pos  = body.Position;
        float time = v.TimeInState;

        if (time < v.WindupDuration)
        {
            float p     = time / v.WindupDuration;
            bool  late  = v.WindupDuration - time < 0.25f;
            bool  flash = late && ((int)(time * 16f) & 1) == 0;
            var   edge  = flash ? FlashColor : ChargeColor;
            t.Ring(pos, Radius, edge * (0.35f + 0.45f * p), 40, late ? 2f : 1.5f);
            t.Ring(pos, Radius * p, ChargeColor * (0.25f + 0.5f * p), 32, 2f);
            t.Ring(pos, 13f + 3f * p, edge, 12, 2f);
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            for (int k = 0; k < 4; k++)
                t.Ring(pos, Radius * (0.4f + 0.2f * k), ReleaseColor * (0.5f + 0.15f * k), 40, 3f);
        }
        else
        {
            float p = v.RecoveryDuration > 0f
                ? (time - v.WindupDuration - v.ActiveDuration) / v.RecoveryDuration : 1f;
            t.Ring(pos, Radius * (1f + 0.1f * p), ReleaseColor * (0.5f * (1f - p)), 40, 2f);
        }
    }

    private static Polygon[] BuildWedges()
    {
        var wedges = new Polygon[Segments];
        float step = MathHelper.TwoPi / Segments;
        for (int i = 0; i < Segments; i++)
        {
            var verts = new Vector2[ArcPoints + 1];
            verts[0] = Vector2.Zero;
            for (int k = 0; k < ArcPoints; k++)
            {
                float a = step * (i + k / (float)(ArcPoints - 1));
                verts[k + 1] = new Vector2(MathF.Cos(a), MathF.Sin(a)) * Radius;
            }
            wedges[i] = new Polygon(verts);
        }
        return wedges;
    }

    private static Vector2[] BuildWedgeDirs()
    {
        var dirs = new Vector2[Segments];
        float step = MathHelper.TwoPi / Segments;
        for (int i = 0; i < Segments; i++)
        {
            float a = step * (i + 0.5f);
            dirs[i] = Vector2.Normalize(new Vector2(MathF.Cos(a), MathF.Sin(a) - Lift));
        }
        return dirs;
    }
}

// ── Body ────────────────────────────────────────────────────────────────────
public sealed class PulserEnemy : EnemyEntity
{
    public const float BodyRadius = 11f;

    // Seconds from a release until it may charge again.
    public const float CooldownSeconds = 2.4f;

    private static readonly Color IdleColor   = new(120, 90, 170);
    private static readonly Color ChargeColor = new(200, 140, 255);

    public override EntityKind Kind => EntityKind.Pulser;

    // Remaining cooldown, s (0 ⇒ ready). Scratch.F0 round-trips through the
    // snapshot EnemyEntity already writes.
    public float Cooldown   => Scratch.F0;
    public bool  PulseReady => Scratch.F0 <= 0f;

    // Idempotent across the active window's frames: only the first starts the
    // clock, and reports that it did.
    public bool StartCooldown()
    {
        if (Scratch.F0 > 0f) return false;
        Scratch.F0 = CooldownSeconds;
        return true;
    }

    public PulserEnemy(Vector2 pos)
        : base(new PhysicsBody(Polygon.CreateRegular(BodyRadius, 8), pos),
               health: 4f,                  // eight stock slashes
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),    // 0 — fallback; falls under gravity
                   new PulserCrawlState(),  // 1 — any surface, gravity off
                   // No EnemyAttackHoldState (the crawl roots itself while committed,
                   // and AttackHold would un-latch it) and no stagger (it doesn't flinch).
               },
               actions: new List<EnemyActionState>
               {
                   new PulserBurstAction(),
               },
               controller: new PulserController())
    {
        Mass   = 6f;
        Body.FrictionScale = 0.12f;
        Color  = IdleColor;
        Sprite = null;              // drawn as its body outline in Color
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (!IsDead && Scratch.F0 > 0f) Scratch.F0 = MathF.Max(0f, Scratch.F0 - dt);
        base.Update(dt, player, hitboxes, spawner);
        Color = IsActionCommitted ? ChargeColor : IdleColor;
    }

    // Between pulses: a small ring that closes in as the cooldown runs out.
    public override void Telegraph(TelegraphList t)
    {
        base.Telegraph(t);
        if (IsDead || CurrentActionIndex >= 0 || Scratch.F0 <= 0f) return;
        float left = Scratch.F0 / CooldownSeconds;
        t.Ring(Body.Position, BodyRadius + 2f + 10f * left, IdleColor * 0.6f, 16);
    }
}
