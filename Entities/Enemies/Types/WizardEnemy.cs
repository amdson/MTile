using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─────────────────────────────────────────────────────────────────────────────
//  WIZARD — a fragile caster that fights with terrain and slow magic.
// ─────────────────────────────────────────────────────────────────────────────
//
// Run it: set "Stage": "wizard" in game_config.json.
//
// Two spells and no melee:
//   • Waves — three columns of slow violet orbs, one after another, drifting
//     toward you on a gentle sine. The orbs are magic: they pass THROUGH terrain,
//     so cover doesn't stop them, but any hit swats one out of the air.
//   • Pillar — when you close in, it raises a 3-block dirt pillar a few tiles in
//     front of itself (sprouted from the ground, so it visibly rises). The pillar
//     walls off your approach while its own waves drift straight through.
// It keeps its distance, backing off as you advance, and dies to two slashes.
//
// Built from the TemplateEnemy pattern (brain / legs / arms); see that file for
// the rules every piece here follows.

// ── Brain ───────────────────────────────────────────────────────────────────
// Hold a stand-off distance: approach from far, back away when crowded.
public sealed class WizardController : EnemyController
{
    public float PreferredRange { get; init; } = 130f;
    public float Deadband       { get; init; } = 25f;
    public float AlertRange     { get; init; } = 360f;

    public override EnemyInput Decide(in EnemyContext ctx)
    {
        var   to   = ctx.ToPlayer;
        float dx   = MathF.Abs(to.X);
        int   side = to.X >= 0f ? 1 : -1;
        var input = new EnemyInput
        {
            AimWorld   = ctx.Player.Body.Position,
            WantAttack = ctx.Dist <= AlertRange,
        };
        if (ctx.Dist > AlertRange) return input;

        if      (dx > PreferredRange + Deadband) input.MoveDir.X =  side;
        else if (dx < PreferredRange - Deadband) input.MoveDir.X = -side;
        return input;
    }
}

// ── Legs ────────────────────────────────────────────────────────────────────
public class WizardWalkState : WardenWalkState
{
    protected override float Speed => 28f;
}

// ── Arms: waves ─────────────────────────────────────────────────────────────
// Windup (a charge gathering over its head), then three orb columns WaveInterval
// apart, then a long recovery (the cooldown). Each column gets its own HitId, so a
// column that overlaps you is one hit, but each of the three can land.
public class WizardWavesAction : EnemyActionState
{
    protected virtual float Windup        => 0.80f;
    protected virtual int   Waves         => 3;
    protected virtual float WaveInterval  => 0.45f;
    protected virtual float Recovery      => 1.40f;

    // Only casts from range: inside this it retreats (or walls you off) instead —
    // otherwise it recasts the moment each volley ends and never moves at all.
    protected virtual float MinRange      => 100f;
    protected virtual float MaxRange      => 300f;

    // One column: OrbsPerWave orbs stacked OrbSpacing apart around the body's
    // height, drifting at DriftSpeed, each on a sine offset in phase from its
    // neighbours so the column writhes.
    protected virtual int   OrbsPerWave   => 3;
    protected virtual float OrbSpacing    => 12f;
    protected virtual float DriftSpeed    => 55f;
    protected virtual float MuzzleOffset  => 10f;

    protected virtual Color ChargeColor   => new(170, 90, 255);

    private float Active => (Waves - 1) * WaveInterval + 0.05f;

    // Active 26 sits UNDER the pillar's passive 27, so a player who closes in mid-
    // volley gets walled off immediately rather than after the recovery.
    public override int ActivePriority  => 26;
    public override int PassivePriority => 22;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= MinRange && ctx.Dist <= MaxRange;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
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
        for (int w = 0; w < Waves; w++)
        {
            float fireT = v.WindupDuration + w * WaveInterval;
            if (prevT < fireT && v.TimeInState >= fireT) FireWave(in ctx, v.LockedFacing);
        }
    }

    private void FireWave(in EnemyContext ctx, int facing)
    {
        int hitId  = ctx.Spawner.HitIds.Next();
        var origin = ctx.Self.Body.Position + new Vector2(facing * MuzzleOffset, 0f);
        float mid  = (OrbsPerWave - 1) * 0.5f;
        for (int i = 0; i < OrbsPerWave; i++)
        {
            var   pos   = origin + new Vector2(0f, (i - mid) * OrbSpacing);
            float phase = i * MathHelper.TwoPi / OrbsPerWave;
            ctx.Spawner.SpawnEntity(new WizardOrbProjectile(pos, facing * DriftSpeed, phase, hitId, ctx.Self.Faction));
        }
    }

    // Windup: a violet charge swelling above the head, plus faint ghosts where the
    // first column will appear. During the volley, a steady glow.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        var   head = body.Position + new Vector2(0f, -14f);
        if (time < v.WindupDuration)
        {
            float p = time / MathF.Max(v.WindupDuration, 1e-3f);
            t.Disc(head, 1.5f + 3f * p, Color.Lerp(new Color(ChargeColor, 70), ChargeColor, p));
            t.Ring(head, 8f - 5f * p, ChargeColor * (0.3f + 0.6f * p), 12, 1f);
            var   origin = body.Position + new Vector2(v.LockedFacing * MuzzleOffset, 0f);
            float mid    = (OrbsPerWave - 1) * 0.5f;
            for (int i = 0; i < OrbsPerWave; i++)
                t.Ring(origin + new Vector2(0f, (i - mid) * OrbSpacing), 3f, ChargeColor * (0.15f + 0.4f * p), 8, 1f);
        }
        else if (time < v.WindupDuration + v.ActiveDuration)
        {
            t.Disc(head, 3f, ChargeColor * 0.8f);
        }
    }
}

// ── Arms: pillar ────────────────────────────────────────────────────────────
// When the player is closing in on its facing side, raise a PillarHeight-block
// dirt pillar PillarOffsetTiles ahead. The cells are requested bottom-up through
// the ordinary sprout path, so the bottom one grows off the ground and each one
// above waits for the one below — the pillar visibly rises, and it's ordinary
// breakable dirt afterwards. Only fires if at least MinNewCells of the column are
// still open, so it doesn't recast over a pillar that's already standing.
public class WizardPillarAction : EnemyActionState
{
    protected virtual float Windup            => 0.50f;
    protected virtual float Active            => 0.05f;
    protected virtual float Recovery          => 0.70f;

    protected virtual int   PillarOffsetTiles => 3;
    protected virtual int   PillarHeight      => 3;
    protected virtual int   MinNewCells       => 2;
    // Casts when the player is within this horizontal distance on its facing side.
    protected virtual float TriggerRange      => 110f;
    protected virtual float VerticalSlack     => 60f;
    protected virtual TileType Material       => TileType.Dirt;

    protected virtual Color TelegraphColor    => new(170, 90, 255);

    // Above the waves (even mid-volley): when you're close, walling you off wins.
    public override int ActivePriority  => 32;
    public override int PassivePriority => 27;

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        var to = ctx.ToPlayer;
        int facing = ctx.Facing == 0 ? 1 : ctx.Facing;
        if (to.X * facing <= 0f || MathF.Abs(to.X) > TriggerRange || MathF.Abs(to.Y) > VerticalSlack) return false;
        return TryFindColumn(ctx.Spawner?.Chunks, ctx.Self.Body, facing, out _, out _, out int open)
            && open >= MinNewCells;
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.Committed    = true;
        // LockedAim carries the pillar's column + ground row (in cells), not a
        // direction: it is the one snapshotted Vector2 on the vars, and Telegraph
        // needs to know where the pillar will stand.
        TryFindColumn(ctx.Spawner?.Chunks, ctx.Self.Body, v.LockedFacing, out int gtx, out int ground, out _);
        v.LockedAim = new Vector2(gtx, ground);
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
        if (prevT >= v.WindupDuration || v.TimeInState < v.WindupDuration) return;

        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return;
        int gtx = (int)v.LockedAim.X, ground = (int)v.LockedAim.Y;
        for (int k = 1; k <= PillarHeight; k++)
            chunks.TryRequestTile(gtx, ground - k, Material);
    }

    // The column PillarOffsetTiles ahead of the body, and its ground: the first solid
    // cell at or below the row the feet stand in (searched a few rows down, so a
    // shallow dip still works; a pit returns false). `open` = empty cells among the
    // PillarHeight above the ground.
    private bool TryFindColumn(ChunkMap chunks, PhysicsBody body, int facing,
                               out int gtx, out int ground, out int open)
    {
        const float ts = Chunk.TileSize;
        gtx = (int)MathF.Floor(body.Position.X / ts) + facing * PillarOffsetTiles;
        ground = 0; open = 0;
        if (chunks == null) return false;

        int feetRow = (int)MathF.Floor((body.Bounds.Bottom - 1f) / ts);
        for (int gy = feetRow; gy <= feetRow + 3; gy++)
        {
            if (chunks.GetCellState(gtx, gy) != TileState.Solid) continue;
            ground = gy;
            for (int k = 1; k <= PillarHeight; k++)
                if (chunks.GetCellState(gtx, gy - k) == TileState.Empty) open++;
            return true;
        }
        return false;
    }

    // Windup: outlines of the cells about to rise, filling bottom-up — where and when.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.TimeInState >= v.WindupDuration) return;
        const float ts = Chunk.TileSize;
        float p = v.TimeInState / MathF.Max(v.WindupDuration, 1e-3f);
        int gtx = (int)v.LockedAim.X, ground = (int)v.LockedAim.Y;
        for (int k = 1; k <= PillarHeight; k++)
        {
            var c = new Vector2(gtx * ts + ts * 0.5f, (ground - k) * ts + ts * 0.5f);
            bool lit = p * PillarHeight >= k - 1;
            t.Rect(c, new Vector2(ts - 2f, ts - 2f), TelegraphColor * (lit ? 0.25f + 0.4f * p : 0.1f));
        }
        t.Disc(body.Position + new Vector2(0f, -14f), 1.5f + 2.5f * p, TelegraphColor);
    }
}

// ── The orb ─────────────────────────────────────────────────────────────────
// Slow and gravity-free, drifting horizontally on a sine. Passes through terrain
// (IgnoreTiles) — the defining difference from the Aspid's fireball — and pops on
// the player it touches or on any hit (0.1 HP).
public class WizardOrbProjectile : Projectile, ITelegraphSource
{
    private const float Radius         = 3.5f;
    private const float LifeSeconds    = 7f;
    private const float Damage         = 0.5f;     // a stock slash's worth
    private const float HitboxHalfSize = 4f;
    private const float Knockback      = 180f;     // under the big-hit line: no hitstop
    private const float SineAmplitude  = 7f;       // px
    private const float SinePeriod     = 1.3f;     // s

    private int   _hitId;
    private float _driftX;
    private float _phase;

    public override EntityKind Kind => EntityKind.WizardOrb;

    public WizardOrbProjectile(Vector2 pos, float driftX, float phase, int hitId, Faction owner)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius, 6), pos) { IgnoreTiles = true },
               health: 0.1f, lifetime: LifeSeconds, owner: owner)
    {
        _hitId       = hitId;
        _driftX      = driftX;
        _phase       = phase;
        Mass         = 0.3f;
        GravityScale = 0f;
        Color        = new Color(170, 90, 255);
        Sprite       = Sprites.WizardOrb(Radius);
        Body.Velocity = VelocityAt(0f);
    }

    // Position offset y = A·sin(ωt + φ) ⇒ vy = A·ω·cos(ωt + φ). Re-derived from Age
    // every frame, so the path is a pure function of snapshotted state.
    private Vector2 VelocityAt(float age)
    {
        float w = MathHelper.TwoPi / SinePeriod;
        return new Vector2(_driftX, SineAmplitude * w * MathF.Cos(w * age + _phase));
    }

    // Drift + phase ride the Aim slot; HitId has its own.
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.HitId = _hitId;
        s.Aim   = new Vector2(_driftX, _phase);
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _hitId  = s.HitId;
        _driftX = s.Aim.X;
        _phase  = s.Aim.Y;
    }

    protected override void ProjectileUpdate(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        Body.Velocity = VelocityAt(Age);

        var p = Body.Position;
        var region = new BoundingBox(p.X - HitboxHalfSize, p.Y - HitboxHalfSize,
                                     p.X + HitboxHalfSize, p.Y + HitboxHalfSize);
        hitboxes?.Publish(new Hitbox(
            region, _hitId, Damage, new Vector2(MathF.Sign(_driftX) * Knockback, 0f),
            Faction, Id, Color,
            targets: HitTargets.EntitiesOnly));   // no origin: magic ignores cover

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

    public void Telegraph(TelegraphList t)
    {
        float flicker = 0.5f + 0.5f * MathF.Sin(AgeSeconds * 25f + _phase);
        t.Ring(Body.Position, Radius + 2f + flicker, new Color(200, 150, 255) * (0.3f + 0.25f * flicker), 10, 1f);
    }
}

// ── Body ────────────────────────────────────────────────────────────────────
public sealed class WizardEnemy : EnemyEntity
{
    private const float Radius = 9f;

    public override EntityKind Kind => EntityKind.Wizard;

    public WizardEnemy(Vector2 pos)
        : base(new PhysicsBody(Polygon.CreateRegular(Radius, 5), pos),
               health: 1.0f,                     // two stock slashes
               movement: new List<EnemyMovementState>
               {
                   new EnemyIdleState(),         // 0 — fallback
                   new WizardWalkState(),        // 1
                   new EnemyAttackHoldState(),   // 2 — plant while casting
                   new EnemyStaggerState(),      // 3 — a hit interrupts the cast
               },
               actions: new List<EnemyActionState>
               {
                   new WizardWavesAction(),
                   new WizardPillarAction(),
               },
               controller: new WizardController())
    {
        Mass               = 0.8f;
        Body.FrictionScale = 0.12f;
        Color              = new Color(150, 80, 220);
        Sprite             = null;               // drawn as its outline in Color
    }
}
