using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Two fighter tools that are not attacks on their own: a dash (move, and for a few
// frames be untouchable) and a mine (lay a delayed explosive). Both are RequestOnly — a
// brain gets one only by naming it in EnemyInput.RequestedAction; the open "attack if
// you can" scan never picks it, so a brain written before they existed is unchanged.

// A burst along the ground. Direction is the brain's MoveDir.X at Enter (or straight
// away from the target if it gave none). For the first Intangible seconds of the Active
// window the body publishes no hurtbox (EnemyEntity.PublishHurtboxes): nothing can hit
// it and a mine cannot feel it. Bodies never collide with each other in this sim, so a
// dash can cross straight through the opponent. Recovery is the commitment: the body is
// locked and hittable.
public class EnemyDashAction : EnemyActionState
{
    public EnemyDashAction() : this(ActionSpec.Default(ActionKind.Dash)) {}
    public EnemyDashAction(ActionSpec spec) { Spec = spec; }

    // Grounded-ish: |Vy| small. Leaves the dash a ground move, not a second jump.
    private const float GroundedVy = 5f;
    // Speed kept into recovery, as a fraction of the dash — a skid, not a hard stop.
    private const float SkidFraction = 0.3f;

    public override int  ActivePriority  => Spec.ActivePriority;
    public override int  PassivePriority => Spec.PassivePriority;
    public override bool RequestOnly     => true;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => MathF.Abs(ctx.Self.Body.Velocity.Y) < GroundedVy;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float mx = ctx.Input.MoveDir.X;
        int dir = mx > 0.01f ? 1 : mx < -0.01f ? -1
                : ctx.ToPlayer.X > 0f ? -1 : ctx.ToPlayer.X < 0f ? 1
                : -(ctx.Facing == 0 ? 1 : ctx.Facing);
        v.LockedFacing = dir;
        v.Committed    = true;
        PopulateDurations(ref v);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Spec.Windup;
        v.ActiveDuration   = Spec.Active;
        v.RecoveryDuration = Spec.Recovery;
    }

    public override bool IsIntangible(in EnemyActionVars v)
        => v.TimeInState >= v.WindupDuration && v.TimeInState < v.WindupDuration + MathF.Min(Spec.Intangible, v.ActiveDuration);

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prevT = v.TimeInState;
        v.TimeInState += ctx.Dt;
        float t = v.TimeInState, end = v.WindupDuration + v.ActiveDuration;
        // Runs after movement.Update (EnemyEntity orders the action last), so it wins.
        if (t >= v.WindupDuration && t < end)
            ctx.Self.Body.Velocity.X = v.LockedFacing * Spec.Speed;
        else if (prevT < end && t >= end)
            ctx.Self.Body.Velocity.X = v.LockedFacing * Spec.Speed * SkidFraction;
    }

    // A short streak behind the body while dashing; ghosted while untouchable.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState;
        if (time < v.WindupDuration || time >= v.WindupDuration + v.ActiveDuration) return;
        var c = Color.LightSkyBlue * (IsIntangible(v) ? 0.8f : 0.4f);
        var p = body.Position;
        for (int i = 0; i < 3; i++)
        {
            int len = 14 - i * 4;
            float x = p.X - v.LockedFacing * (8 + i * 6);
            t.Box(v.LockedFacing > 0 ? x - len : x, p.Y - 6 + i * 6, len, 1, c);
        }
    }
}

// Summon a FighterMine at a point of the brain's choosing: AimWorld at Enter, pulled in to
// MaxRange from the body and frozen there as an ABSOLUTE point in v.LockedAim (which
// round-trips through EntityData.Aim, so a snapshot mid-summon restores the same spot).
// The windup is long (1.0 s, fixed per kind) and public: the destination draws a marker
// that fills in, and every opponent's free FighterSenses.Mines lists it as Pending with
// the seconds until it appears. The mine appears on the windup→active transition (one
// frame per use at a fixed timestep, so nothing beyond the vars needs snapshotting),
// exactly where it was aimed — mid-air included; mines are rooted and never fall. A
// destination inside solid terrain fizzles (still paid: the fighter committed to it).
// Refused while this fighter already has MaxLive mines out or on the way.
public class EnemyMineAction : EnemyActionState
{
    public EnemyMineAction() : this(ActionSpec.Default(ActionKind.Mine)) {}
    public EnemyMineAction(ActionSpec spec) { Spec = spec; }

    public override int  ActivePriority  => Spec.ActivePriority;
    public override int  PassivePriority => Spec.PassivePriority;
    public override bool RequestOnly     => true;

    public override bool CheckPreConditions(in EnemyContext ctx)
        => LiveMines(ctx) < Math.Max(1, Spec.MaxLive);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.HitId        = ctx.Spawner.HitIds.Next();
        v.Committed    = true;
        v.LockedAim    = Destination(ctx.Self.Body.Position, ctx.Input.AimWorld, Spec.MaxRange);
        PopulateDurations(ref v);
    }

    // The aimed point, pulled in along the aim line to at most `range` from the body.
    public static Vector2 Destination(Vector2 body, Vector2 aim, float range)
    {
        var d = aim - body;
        float len = d.Length();
        return len <= range || len < 1e-4f ? aim : body + d * (range / len);
    }

    public override void Exit(in EnemyContext ctx, ref EnemyActionVars v) => v.Committed = false;

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Spec.Windup;
        v.ActiveDuration   = Spec.Active;
        v.RecoveryDuration = Spec.Recovery;
    }

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float prevT = v.TimeInState;
        v.TimeInState += ctx.Dt;
        if (!(prevT < v.WindupDuration && v.TimeInState >= v.WindupDuration)) return;
        var at = v.LockedAim;
        var chunks = ctx.Spawner?.Chunks;
        if (chunks != null)
        {
            int gtx = (int)MathF.Floor(at.X / Chunk.TileSize), gty = (int)MathF.Floor(at.Y / Chunk.TileSize);
            if (chunks.GetCellState(gtx, gty) == TileState.Solid) return;   // fizzles inside rock
        }
        var self = ctx.Self;
        ctx.Spawner?.SpawnEntity(new FighterMine(at, self.Id, self.Team, v.HitId,
            Spec.Damage, Spec.BlastRadius, Spec.ArmDelay, Spec.Fuse, Spec.Knockback.X));
    }

    // Live mines plus summons on the way, both counted against MaxLive.
    private static int LiveMines(in EnemyContext ctx)
    {
        Span<MineView> views = stackalloc MineView[16];
        int n = ctx.Spawner?.Targets?.Mines(views) ?? 0, mine = 0;
        for (int i = 0; i < n; i++) if (views[i].Owner == ctx.Self.Id) mine++;
        return mine;
    }

    // The summon, drawn at the destination: the blast ring fills in across the windup,
    // with a pulsing core in the last third.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f || v.TimeInState >= v.WindupDuration) return;
        float p = v.TimeInState / v.WindupDuration;
        var at = v.LockedAim;
        var hot = new Color(230, 70, 40);
        t.Ring(at, Spec.BlastRadius, hot * 0.15f, 20);
        t.Ring(at, Spec.BlastRadius * p, hot * (0.25f + 0.5f * p), 20);
        float pulse = p > 0.66f ? 0.5f + 0.5f * MathF.Abs(MathF.Sin(v.TimeInState * 20f)) : 0.5f;
        t.Rect(at, 2f + 3f * p, hot * pulse);
    }
}

// What a brain (and the mine action's own cap) sees of a mine: free, exact — a mine is a
// still object in the open, and a summon is a public marker. Owner is its summoner's id.
public struct MineView
{
    public Vector2  Position;
    public float    Radius;      // blast reach, px
    public bool     Pending;     // still being summoned: not there yet, cannot go off
    public float    AppearsIn;   // Pending: seconds until it appears (0 once it has)
    public bool     Armed;       // contact sets it off now
    public float    FuseLeft;    // seconds until it goes off on its own (from now, pending included)
    public EntityId Owner;
    public int      Team;
}
