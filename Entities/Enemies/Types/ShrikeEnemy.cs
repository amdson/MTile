using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  SHRIKE — the bird that comes for you.
// ─────────────────────────────────────────────────────────────────────────────
//
// EntityKind.Bird is terrain that moves: it patrols a lane, hurts on contact,
// and never notices anyone. The Shrike is the same silhouette with the opposite
// intent — it patrols the same way until the player crosses into its detection
// range, then hovers for a beat and dives, and whatever it reaches first, player
// or rock, it goes off on. A swoop that misses ends in a pull-up and another
// hover, so every pass is announced before it happens, not just the first.
//
// Reusing the bird's shape is the point: the two share an outline, so a player
// who has learned to route around birds has to look twice at every flock. The
// red body and the wind-up hover are the only tells, and both are deliberate —
// see ShrikeDiveState.Telegraph.
//
// ── Why it is built this way ────────────────────────────────────────────────
//
// The three pieces split the way TemplateEnemy.cs describes: brain decides
// whether to patrol or engage, legs execute the flight, arms own the blast.
// Two things about that split are load-bearing here:
//
//   * The dive is a MOVEMENT state, not an action. An action would set
//     Committed, which drops any flight state (their preconditions are all
//     !IsActionCommitted) — a committed dive would stop the bird flying and,
//     at GravityScale 0, leave it hanging in the air mid-swoop.
//
//   * The detonation IS an action, and the only one. It replaces
//     EnemyContactAction rather than joining it: a creature that explodes on
//     touch has no use for a repeating contact tick, and registering both would
//     have the two fight over the same situation with priority silently
//     deciding it.
//
// ── The knobs that must agree ───────────────────────────────────────────────
//
// DetectRange appears in all three pieces (brain: when to aim at the player;
// legs: when to dive; arms: when a terrain impact counts as a miss rather than
// a bump). They are single-sourced from ShrikeEnemy.DetectRange at the bottom
// of this file — if you retune it, retune it there, not in one of the three.
// ─────────────────────────────────────────────────────────────────────────────


// ── 1. THE BRAIN ────────────────────────────────────────────────────────────
// Patrol until the player is close, then point at them. Nothing here knows what
// a dive is: it emits "the way I want to go" and the legs decide how hard.
//
// Stateless, like every controller (see EnemyController). The patrol leg is a
// square wave on ctx.Frame for exactly the reason PatrolController's is — a
// remembered heading would be per-entity mutable state with no snapshot path,
// so a rollback would silently flip patrols mid-flight.
public sealed class ShrikeController : EnemyController
{
    // Engage inside this distance, patrol outside it. At the shipped 3× camera
    // zoom the player sees roughly ±166px of world, so 170 means the dive starts
    // at the edge of the screen rather than from somewhere unseen.
    public float DetectRange { get; init; } = 170f;

    // Seconds per patrol leg before reversing, as PatrolController.LegSeconds.
    public float LegSeconds  { get; init; } = 2.0f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var   to   = ctx.ToPlayer;
        float dist = to.Length();

        // ── Engaged ──────────────────────────────────────────────────────────
        // Point straight at the player in 2D. Deliberately NOT gated on line of
        // sight: a shrike that dives into the wall the player is hiding behind
        // and blows a hole in it is the better outcome in a game where the
        // terrain is the weapon, and ShrikeDetonateAction's arm gate is what
        // makes that impact count.
        if (dist <= DetectRange && dist > 1e-3f)
        {
            var dir = to / dist;
            return new EnemyInput
            {
                MoveDir    = dir,
                Jump       = false,
                AimWorld   = ctx.Player.Body.Position,
                WantAttack = true,
            };
        }

        // ── Patrolling ───────────────────────────────────────────────────────
        float t       = ctx.Frame * ctx.Dt;
        float legs    = t / MathF.Max(LegSeconds, 1e-3f);
        bool  right   = ((int)legs & 1) == 0;        // even leg → right, odd → left
        var   heading = new Vector2(right ? 1f : -1f, 0f);

        return new EnemyInput
        {
            MoveDir    = heading,
            Jump       = false,
            // Face along the patrol, so the sprite points where it is flying.
            AimWorld   = ctx.Self.Body.Position + heading * 32f,
            // Left on even out of range: the detonation's own gates decide when
            // it fires, and a shrike that clips a wall on the way in should go
            // off even if the player has just slipped outside DetectRange.
            WantAttack = true,
        };
    }
}


// ── 2. THE LEGS ─────────────────────────────────────────────────────────────
// The dive. A flight mode, not an attack: it inherits EnemyFlyState's gravity
// compensation and acceleration budget wholesale and changes only the target
// velocity, through the DesiredVelocity hook.
//
// The shape of the dive comes out of two numbers rather than any scripting:
// DiveSpeed is high and MaxAcceleration is only moderately high, so the bird
// CANNOT turn as fast as it travels. It commits, overshoots a player who moves,
// and has to arc back around for another pass — which is the "attempt" in
// "attempts to dive bomb", and it costs no extra state to get.
//
// The pass is a CYCLE, not a one-shot wind-up. Every swoop is preceded by its own
// hover, so a shrike that missed pulls up, hangs, telegraphs, and only then comes
// again:
//
//   phase = TimeInState mod (WindupSeconds + DiveSeconds)
//   [0, WindupSeconds)              hover in place, ring telegraph — the tell
//   [WindupSeconds, CycleSeconds)   full speed along the brain's MoveDir
//
// Cycling matters more than the first hover did. The state deliberately survives
// an overshoot (see AbortSlack), so under the old one-shot timeline a shrike that
// missed never hovered again — it just became a permanent 300px/s heat-seeker with
// no tell for every pass after the first. That is what read as "too fast": not the
// dive speed, the absence of any punctuation between dives.
//
// The phase is derived from TimeInState rather than kept in a field on purpose:
// TimeInState is the only movement var EnemyEntity snapshots, so a cycle counter
// would silently desync a rollback (same reason EnemyHopState solves its own cycle
// this way).
public class ShrikeDiveState : EnemyFlyState
{
    // Must match ShrikeController.DetectRange — the brain only points at the
    // player inside it, so diving outside it would mean diving along a patrol
    // heading at attack speed. Single-sourced in ShrikeEnemy.Blueprint.
    public float DiveRange     { get; init; } = 170f;

    // Hysteresis on the exit. Without slack the state drops the frame the bird
    // overshoots past DiveRange and re-enters the frame it swings back, which
    // restarts the wind-up hover forever and the shrike never actually arrives.
    public float AbortSlack    { get; init; } = 1.7f;

    // The tell, once per pass. Long enough to read as "that one has noticed me"
    // and to step out of the line, short enough that it still reads as a swoop.
    // Note the body spends the first ~0.18s of it braking out of the previous
    // dive (300px/s against the acceleration budget below), so the fully-still
    // portion a player actually reads is shorter than the number.
    public float WindupSeconds { get; init; } = 0.5f;

    // How long one swoop runs before the bird pulls up and telegraphs again. At
    // DiveSpeed this is ~120px of travel — roughly DiveRange, so a dive launched
    // from the edge of detection still arrives in a single pass and the cycle only
    // shows itself when the shrike actually missed.
    public float DiveSeconds   { get; init; } = 0.45f;

    public float DiveSpeed     { get; init; } = 300f;

    private float CycleSeconds => MathF.Max(WindupSeconds + DiveSeconds, 1e-3f);

    // Where in the hover/dive cycle t falls. Plain float modulo (no MathF.IEEERemainder,
    // no accumulate-and-subtract counter) so it stays a pure, replay-stable function
    // of TimeInState.
    private float Phase(float t)
    {
        float c = CycleSeconds;
        return t - c * MathF.Floor(t / c);
    }

    // Roughly 4× the patrol's budget, but well under DiveSpeed — that ratio IS
    // the turn radius. Raise it and the shrike becomes a heat-seeker that can't
    // be sidestepped; lower it and it can't correct at all.
    protected override float MaxAcceleration => 1300f;

    // Above Fly (30/26) so an engaged shrike dives instead of cruising, and
    // above Leap (36/30) so nothing in the stock pool outbids it. Below
    // AttackHold (40/35) and Stagger (50/45), neither of which this enemy
    // registers — see the blueprint's note.
    public override int ActivePriority  => 38;
    public override int PassivePriority => 34;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && ctx.Dist <= DiveRange;

    // Continues out to the slack radius: a dive that has overshot is still a
    // dive, and pulling back around is part of it.
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && ctx.Dist <= DiveRange * AbortSlack;

    protected override Vector2 DesiredVelocity(in EnemyContext ctx, in EnemyMovementVars v)
    {
        // Wind-up: hold station. Hovering rather than coasting is what makes the
        // tell legible — the bird visibly stops, then goes. Runs before EVERY
        // swoop, not just the first.
        if (Phase(v.TimeInState) < WindupSeconds) return Vector2.Zero;

        var dir = ctx.Input.MoveDir;
        if (dir.LengthSquared() <= 1e-4f) return Vector2.Zero;
        dir.Normalize();
        return dir * DiveSpeed;
    }

    // The wind-up tell: a ring that closes in on the body as the hover runs out,
    // so both "this one has picked you" and "how long you have" are readable.
    // Telegraph sees only (list, body, vars), which is why the phase is measured
    // off TimeInState and not off anything about the player. Draws once per cycle,
    // so the second and third passes are as readable as the first.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyMovementVars v)
    {
        float phase = Phase(v.TimeInState);
        if (phase >= WindupSeconds) return;
        float p = phase / MathF.Max(WindupSeconds, 1e-3f);   // 0 → 1
        t.Ring(body.Position, MathHelper.Lerp(26f, 10f, p),
               Color.Lerp(new Color(255, 120, 90, 90), new Color(255, 120, 90), p), 10, 1.5f);
    }
}


// ── 3. THE ARMS ─────────────────────────────────────────────────────────────
// The whole kit: one blast, then the bird is gone. No windup — the impact is the
// trigger, and a tell after the fact would be describing something that already
// happened. The dive's hover is where the fairness lives.
//
// Two ways in, and both are "impact":
//
//   * the player is within touching distance, or
//   * the body is against solid tile WHILE HUNTING (ctx.Dist <= ArmRange).
//
// The arm gate on the terrain path is what keeps a patrolling shrike from
// popping the first time it brushes a rim. Same reason a real fuse is armed on
// the run-in, not in the nest — and it means a missed dive that ploughs into the
// ground still craters, which is the payoff for dodging.
//
// Terrain contact is an explicit tile probe rather than a velocity-stall test
// (BulletProjectile / StickyGrenadeProjectile use the latter). It has to be:
// the swept solver zeroes the velocity on the frame of contact, so by the time
// this precondition runs on the next frame there is no speed left to read.
// PracticeBall probes for exactly this reason.
public class ShrikeDetonateAction : EnemyActionState
{
    // See the class header. Must be ≥ ShrikeController.DetectRange, or a dive
    // could start from a distance at which a terrain impact wouldn't count.
    // Single-sourced in ShrikeEnemy.Blueprint.
    public float ArmRange { get; init; } = 170f;

    // Touch distance, centre to centre: the shrike's 9px body plus a player's
    // ~6px half-width plus a few px so the blast fires on contact rather than
    // after the solver has pushed the two apart.
    protected virtual float ContactRange => 20f;

    // Probe pad past the body bounds. The swept solver halts a body a hair off
    // the surface, so a zero pad would let a shrike rest against a wall forever
    // without ever "touching" it (PracticeBall.ContactPad, same number).
    protected virtual float ContactPad   => 1.5f;

    // Blast reach. Matches ChargedBlast at 2.5 tiles: big enough that a dodge has
    // to be a real one, small enough that the crater shapes a hole rather than
    // clearing a room.
    protected virtual float RadiusTiles  => 2.5f;
    protected virtual int   Segments     => 10;

    // One number for both channels, as StickyGrenadeProjectile does. Against a
    // body it is HP (1.2 of a player's 5 — a heavy hit that is not a kill);
    // against tiles it is HP against TileMaxHP, so it pops dirt and sand and
    // chips stone. They read as one explosion because they are one set of
    // hitboxes.
    protected virtual float Damage       => TileDamage.TileMaxHP * 1.2f;
    protected virtual float Knockback    => 300f;

    // The ring can't cover its own centre — the segments sit ON the ring — so
    // the epicentre needs a box of its own or the one place the blast doesn't
    // reach is the point of impact. Straight up (y-down), because the common
    // case is a player who has just been reached at head height.
    protected virtual float CoreHalfSize => 0.9f * Chunk.TileSize;
    protected virtual Color BlastColor   => new(255, 170, 90);

    // Nothing else is registered on this enemy, so the absolute numbers matter
    // less than the band: sits with the other committing attacks
    // (Slam / RailShot / PounceSlam at 34/30).
    public override int ActivePriority  => 34;
    public override int PassivePriority => 30;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist <= ContactRange
        || (ctx.Dist <= ArmRange && TouchingSolidTile(ctx.Spawner?.Chunks, ctx.Self.Body));

    // Never re-evaluated in practice — Update kills the bird on its first frame —
    // but a rollback restore can land mid-action, so the clock has to be honest.
    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        // One id for the whole blast, so a body caught by the core box AND two
        // ring segments takes it once.
        v.HitId     = ctx.Spawner.HitIds.Next();
        v.Committed = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = 0f;
        v.ActiveDuration   = 0.05f;
        v.RecoveryDuration = 0f;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        Detonate(in ctx, in v);
        // One frame of hitbox, then gone — the same shape as ChargedBlast. This
        // frame's boxes stay live in HitboxWorld for CombatSystem.Apply at the
        // end of the tick (entities update before combat resolves), and
        // EnemyEntity.Update returns early on a dead body from here on, so this
        // action can never publish twice.
        ctx.Self.Health = 0f;
    }

    private void Detonate(in EnemyContext ctx, in EnemyActionVars v)
    {
        var   center   = ctx.Self.Body.Position;
        float radius   = RadiusTiles * Chunk.TileSize;
        var   hitboxes = ctx.Hitboxes;

        if (hitboxes != null)
        {
            // Core: the point of impact itself.
            hitboxes.Publish(new Hitbox(
                new BoundingBox(center.X - CoreHalfSize, center.Y - CoreHalfSize,
                                center.X + CoreHalfSize, center.Y + CoreHalfSize),
                v.HitId, Damage, new Vector2(0f, -1f) * Knockback,
                ctx.Self.Faction, ctx.Self.Id, BlastColor,
                targets: HitTargets.All, origin: center));

            // Ring: one box per segment, each shoving outward along its own
            // radius, so bodies on opposite sides are thrown apart instead of
            // sharing one vector. Half-size is DERIVED from the segment spacing
            // (2πR/N) rather than tuned — a hand-picked constant is exactly how a
            // blast ends up with gaps for a body to stand in.
            float segHalf = MathF.Max(0.5f * Chunk.TileSize, MathF.PI * radius / Segments);
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * MathHelper.TwoPi / Segments;
                var   dir   = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var   at    = center + dir * radius;
                hitboxes.Publish(new Hitbox(
                    new BoundingBox(at.X - segHalf, at.Y - segHalf,
                                    at.X + segHalf, at.Y + segHalf),
                    v.HitId, Damage, dir * Knockback,
                    ctx.Self.Faction, ctx.Self.Id, BlastColor,
                    // Occluded from the point of impact: the blast reaches
                    // through the hole it makes and only through it.
                    targets: HitTargets.All, origin: center));
            }
        }

        // Presentation only (particles / audio), keyed by entity id so the render
        // shell dedupes it across rollback replays. NotifyChargedBlast is named
        // for its first caller but is a generic "an explosion of this radius
        // happened here" channel; a second kind of blast wants the same burst,
        // not a parallel event carrying the same payload.
        ctx.Spawner?.NotifyChargedBlast(ctx.Self.Id, center, radius);
    }

    // Any solid cell overlapping the padded body bounds. Row-major over a fixed
    // range, so a rollback replay probes the same cells in the same order.
    private bool TouchingSolidTile(ChunkMap chunks, PhysicsBody body)
    {
        if (chunks == null) return false;
        var b = body.Bounds;
        int gtx0 = (int)MathF.Floor((b.Left   - ContactPad) / Chunk.TileSize);
        int gtx1 = (int)MathF.Floor((b.Right  + ContactPad) / Chunk.TileSize);
        int gty0 = (int)MathF.Floor((b.Top    - ContactPad) / Chunk.TileSize);
        int gty1 = (int)MathF.Floor((b.Bottom + ContactPad) / Chunk.TileSize);
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (chunks.GetCellState(gtx, gty) == TileState.Solid)
                return true;
        return false;
    }
}


// ── 4. THE WIRING ───────────────────────────────────────────────────────────
public static class ShrikeEnemy
{
    // The one range the brain, the legs and the fuse all have to agree on. See
    // the file header.
    private const float DetectRange = 170f;

    public static EnemyBlueprint Blueprint => new()
    {
        Kind          = EntityKind.Shrike,

        // ── body ──
        // The bird's numbers, on purpose: same silhouette, same swattability. A
        // player slash still flings it, and knocking one off its line before it
        // commits is the counterplay.
        Radius        = 9f,
        Sides         = 5,
        Health        = 2f,
        Mass          = 0.8f,
        // Zero, exactly as EntityKind.Bird: EnemyFlyState can hold altitude
        // against gravity out of its own acceleration budget, but spending that
        // budget on the patrol instead is what keeps the cruise line dead level
        // and the dive's turn radius a property of MaxAcceleration alone.
        GravityScale  = 0f,
        FrictionScale = 0.05f,

        // ── looks ──
        // Red, and the only warning the player gets at a distance. See
        // Sprites.Shrike for the rest of the visual separation from the bird.
        Color         = new Color(165, 45, 45),
        Sprite        = Sprites.Shrike,

        Controller    = new ShrikeController { DetectRange = DetectRange },

        // ── the two registries ──
        // No EnemyStaggerState, for the same reason EntityKind.Bird has none:
        // stagger sets Committed, which drops every flight state, and at
        // GravityScale 0 a staggered shrike would hang motionless in the air
        // instead of reacting. Knockback alone reads the hit.
        //
        // No EnemyAttackHoldState either — at priority 40 it would preempt the
        // dive and brake the body, which is the one thing a dive cannot survive.
        Movement = () => new()
        {
            new EnemyIdleState(),                                 // 0 — fallback
            new EnemyFlyState(),                                  // patrol cruise
            new ShrikeDiveState { DiveRange = DetectRange },      // the swoop
        },
        Actions = () => new()
        {
            new ShrikeDetonateAction { ArmRange = DetectRange },
        },
    };
}
