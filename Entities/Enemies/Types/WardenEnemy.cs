using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  WARDEN — an armored, player-sized walker that is only open while it swings.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it: set "Stage": "warden" in game_config.json.
//
// The whole fight is one read: it plods toward you (slower than you walk), hops
// steps up to two blocks, and is untouchable until it commits to its one attack —
// a slow, heavily telegraphed, short-reach smash that hits very hard. From the
// windup through the end of the recovery it is exposed, and at 0.5 HP any hit
// kills it. So you bait the swing, stay out of the slab, and punish the recovery
// (or trade into the windup, if you're sure you're faster).
//
// Armor is expressed as OnHit declining the hit (no damage, no knockback, no
// stagger) while still returning the authored impulse, exactly like a parry — so
// the attacker gets the recoil bounce and the hitstop of a clank. The hurtbox stays
// published on purpose: a hit that passed straight through would read as a whiff.
// Armored vs. open is shown twice: the body colour, and a ring while armored.
//
// Built from the TemplateEnemy pattern (brain / legs / arms); see that file for
// the rules every piece here follows.

// ── Brain ───────────────────────────────────────────────────────────────────
// Walk toward the player until inside EngageRange, and ask for a hop whenever the
// next column is a step it can clear. Stateless: the step probe is recomputed from
// the terrain every frame.
public sealed class WardenController : EnemyController
{
    // Stop walking inside this horizontal distance. Must sit under
    // WardenSmashAction.Range, which sits under the smash's effective reach
    // (see the ⚠ ordering note on TemplateAction.Range).
    public float EngageRange { get; init; } = 22f;
    // Beyond this it neither walks nor swings — it just stands guard.
    public float AlertRange  { get; init; } = 600f;
    // Tallest step it will hop, in tiles.
    public int   MaxStepTiles { get; init; } = 2;
    // How far past its leading edge it looks for a step, in px.
    public float ProbeAhead  { get; init; } = 3f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var to    = ctx.ToPlayer;
        int side  = to.X >= 0f ? 1 : -1;
        var input = new EnemyInput
        {
            AimWorld   = ctx.Player.Body.Position,
            WantAttack = ctx.Dist <= AlertRange,
        };
        if (ctx.Dist > AlertRange) return input;

        if (MathF.Abs(to.X) > EngageRange)
        {
            input.MoveDir.X = side;
            input.Jump      = StepAhead(ctx, side);
        }
        return input;
    }

    // True when the column just ahead of the feet is blocked by a step 1..MaxStepTiles
    // tall. A taller wall (or nothing at all) returns false — it walks into the wall
    // and waits, which is the honest failure mode.
    private bool StepAhead(in EnemyContext ctx, int side)
    {
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var   b      = ctx.Self.Body.Bounds;
        float probeX = side > 0 ? b.Right + ProbeAhead : b.Left - ProbeAhead;
        const float ts = Chunk.TileSize;

        int rise = 0;
        for (int k = 1; k <= MaxStepTiles + 1; k++)
        {
            if (!TileQuery.IsSolidAt(chunks, probeX, b.Bottom - (k - 0.5f) * ts)) break;
            rise = k;
        }
        return rise >= 1 && rise <= MaxStepTiles;
    }
}

// ── Legs ────────────────────────────────────────────────────────────────────
// A deliberate plod — about a third of the player's walk speed.
public class WardenWalkState : EnemyMovementState
{
    protected virtual float Speed    => 32f;
    protected virtual float Deadzone => 0.1f;

    public override int ActivePriority  => 20;
    public override int PassivePriority => 15;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyMovementVars v)
        => !ctx.Self.IsActionCommitted && MathF.Abs(ctx.Input.MoveDir.X) > Deadzone;

    public override void Update(in EnemyContext ctx, ref EnemyMovementVars v)
    {
        v.TimeInState += ctx.Dt;
        ctx.Self.Body.Velocity.X = MathF.Sign(ctx.Input.MoveDir.X) * Speed;
    }
}

// The stock lift-and-drift jump, sized for a two-block step: 200 px/s up peaks at
// ~33 px (v²/2g), clearing a 22 px step with room for the feet. The drift is a bit
// faster than the walk so it actually lands on top instead of back at the base.
public class WardenHopState : EnemyJumpState
{
    protected override float JumpImpulse     => -200f;
    protected override float HorizontalDrift => 45f;
    protected override float MaxAirTime      => 0.6f;
}

// ── Arms ────────────────────────────────────────────────────────────────────
// One attack: a slow windup, a short heavy slab right in front of it, and a long
// recovery. The whole activation is the vulnerability window (WardenEnemy.IsOpen),
// so Recovery doubles as "shortly after" — tune it to taste.
public class WardenSmashAction : EnemyActionState
{
    protected virtual float   Windup        => 0.75f;
    protected virtual float   Active        => 0.10f;
    protected virtual float   Recovery      => 0.85f;

    // Reach ordering (widest → narrowest):
    //   effective reach  Inset + Reach + player half-width ≈ 6 + 24 + 5 = 35
    //   Range            30   (trigger, centre-to-centre)
    //   brain hold       WardenController.EngageRange = 22 (horizontal)
    protected virtual float   Range         => 30f;
    protected virtual float   VerticalSlack => 26f;
    protected virtual float   Inset         => 6f;
    protected virtual float   Reach         => 24f;
    protected virtual float   HalfHeight    => 15f;

    // 2 of the player's 5 HP, and a launch well past the big-hit line, so it
    // freezes and shakes (CombatState.BigHitStrength).
    protected virtual float   Damage        => 2.0f;
    protected virtual Vector2 Knockback     => new(620f, -260f);

    protected virtual Color   TelegraphColor => new(255, 150, 60);
    protected virtual Color   StrikeColor    => new(255, 70, 40);

    public override int ActivePriority  => 30;
    public override int PassivePriority => 25;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist < Range && MathF.Abs(ctx.ToPlayer.Y) < VerticalSlack;

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
            origin: ctx.Self.Body.Position));
    }

    // Windup: the exact strike slab, outlined, filling in as the swing comes — so
    // the tell shows both WHERE (the slab never moves) and WHEN (how full it is).
    // Active: the solid slab. Recovery: nothing; the body colour says "open".
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
            // Fill rises from the ground as the windup completes.
            float h = r.Height * p;
            t.Box(r.Left, r.Bottom - h, r.Width, h, TelegraphColor * (0.15f + 0.3f * p));
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            t.Box(r.Left, r.Top, r.Width, r.Height, StrikeColor * 0.7f);
        }
    }
}

// ── Body ────────────────────────────────────────────────────────────────────
public sealed class WardenEnemy : EnemyEntity
{
    // Player-sized: roughly the player's standing envelope (~12 wide, ~33 tall).
    // A tall hexagon rather than a box so the pointed foot slides up onto a step
    // edge instead of catching on its corner.
    private const float HalfHeight = 15f;
    private const float WidthScale = 0.4f;   // 2·15·cos30°·0.4 ≈ 10.4 px wide

    private static readonly Color ArmoredColor = new(125, 135, 155);
    private static readonly Color OpenColor    = new(255, 90, 60);

    public override EntityKind Kind => EntityKind.Warden;

    // Open from the start of the windup to the end of the recovery — exactly the
    // span the smash holds Committed.
    public bool IsOpen => IsActionCommitted;

    public WardenEnemy(Vector2 pos)
        : base(new PhysicsBody(CreateBodyPolygon(), pos),
               health: 0.5f,
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),        // 0 — fallback
                   new WardenWalkState(),       // 1
                   new WardenHopState(),        // 2
                   new EnemyAttackHoldState(),  // 3 — plant during the smash
               },
               actions: new List<EnemyActionState>
               {
                   new WardenSmashAction(),
               },
               controller: new WardenController())
    {
        Mass   = 2.5f;           // the player's
        // Low like every other walker: the walk ASSIGNS velocity each frame, and full
        // ground friction would brake that away before the body moved.
        Body.FrictionScale = 0.12f;
        Color  = ArmoredColor;
        Sprite = null;           // drawn as its body outline in Color
    }

    private static Polygon CreateBodyPolygon()
    {
        var verts = Polygon.CreateRegular(HalfHeight, 6).GetVertices(Vector2.Zero);
        for (int i = 0; i < verts.Length; i++) verts[i].X *= WidthScale;
        return new Polygon(verts);
    }

    public override Vector2 OnHit(in Hitbox hit, in Hurtbox hurtbox)
    {
        // Armored: decline the hit like a parry — no damage, knockback or stagger —
        // but echo the impulse so the attacker still gets the clank recoil.
        if (!IsOpen) return hit.KnockbackImpulse;
        return base.OnHit(in hit, in hurtbox);
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        base.Update(dt, player, hitboxes, spawner);
        Color = IsOpen ? OpenColor : ArmoredColor;
    }

    public override void Telegraph(TelegraphList t)
    {
        base.Telegraph(t);
        if (!IsOpen && !IsDead) t.Ring(Body.Position, HalfHeight + 3f, ArmoredColor * 0.6f, 20);
    }
}
