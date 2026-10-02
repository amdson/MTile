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

// Lay a FighterMine on the floor under the body. The windup is the tell; the mine appears
// on the windup→active transition (one frame per use at a fixed timestep, like the
// ranged shot, so nothing beyond the vars needs snapshotting). Refused while this fighter
// already has MaxLive mines out.
public class EnemyMineAction : EnemyActionState
{
    public EnemyMineAction() : this(ActionSpec.Default(ActionKind.Mine)) {}
    public EnemyMineAction(ActionSpec spec) { Spec = spec; }

    // How far below the feet to look for a floor to rest the mine on.
    private const int FloorSearchTiles = 6;

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
        PopulateDurations(ref v);
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
        var self = ctx.Self;
        var pos  = Floor(ctx.Spawner?.Chunks, self.Body.Position, self.Body.Bounds.Bottom);
        ctx.Spawner?.SpawnEntity(new FighterMine(pos, self.Id, self.Team, v.HitId,
            Spec.Damage, Spec.BlastRadius, Spec.ArmDelay, Spec.Fuse, Spec.Knockback.X));
    }

    // The mine rests on the first solid cell at or below the feet (within a few tiles);
    // over a drop it hangs at the feet.
    private static Vector2 Floor(ChunkMap chunks, Vector2 body, float feetY)
    {
        var at = new Vector2(body.X, feetY - FighterMine.BodyRadius);
        if (chunks == null) return at;
        int gtx = (int)MathF.Floor(body.X / Chunk.TileSize);
        int gty = (int)MathF.Floor((feetY - 1f) / Chunk.TileSize);
        for (int k = 0; k <= FloorSearchTiles; k++)
            if (chunks.GetCellState(gtx, gty + k) == TileState.Solid)
                return new Vector2(body.X, (gty + k) * Chunk.TileSize - FighterMine.BodyRadius);
        return at;
    }

    private static int LiveMines(in EnemyContext ctx)
    {
        Span<MineView> views = stackalloc MineView[16];
        int n = ctx.Spawner?.Targets?.Mines(views) ?? 0, mine = 0;
        for (int i = 0; i < n; i++) if (views[i].Owner == ctx.Self.Id) mine++;
        return mine;
    }

    // The windup: a small disc growing at the feet.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f || v.TimeInState >= v.WindupDuration) return;
        float p = v.TimeInState / v.WindupDuration;
        t.Rect(new Vector2(body.Position.X, body.Bounds.Bottom - 2f), 2f + p * 4f, new Color(230, 70, 40) * (0.3f + 0.6f * p));
    }
}

// What a brain (and the mine action's own cap) sees of a mine: free, exact — a mine is
// a still object lying in the open. Owner is its layer's id; Hostile is "not my team".
public struct MineView
{
    public Vector2  Position;
    public float    Radius;      // blast reach, px
    public bool     Armed;       // contact sets it off now
    public float    FuseLeft;    // seconds until it goes off on its own
    public EntityId Owner;
    public int      Team;
}
