using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The Builder's two pool actions (Plans/FIGHTER_DESIGN_PLAN.md §3.3): lay one tile of
// terrain, either next to yourself (PlaceBlock) or conjured over the target's head
// (SpawnBlockInAir). The terrain is the weapon for enemies too.
//
// Shape shared with EnemyRangedAction: the effect fires on the exact windup→active
// transition (`prevT < Windup && t >= Windup`), true on one frame per use at a fixed
// timestep, so nothing beyond EnemyActionVars needs snapshotting — the placed tile
// itself is terrain, and Simulation.Snapshot() already carries terrain.
//
// Energy is NOT spent here: EnemyEntity.SelectAction gates on and deducts
// Spec.EnergyCost at Enter. A placement that is skipped at the transition (cell now
// occupied, a body standing in it) is still paid for — the fighter committed to it.
//
// Both place through the ChunkMap sprout path, so a new tile is Sprouting for
// MovementConfig.SproutLifetime before it turns Solid, exactly like a player-built one.
// Terrain has no gravity: an unsupported tile stays where it was conjured.
internal static class EnemyBlockCells
{
    public static (int gtx, int gty) CellOf(Vector2 world)
        => ((int)MathF.Floor(world.X / Chunk.TileSize), (int)MathF.Floor(world.Y / Chunk.TileSize));

    public static Vector2 Center(int gtx, int gty)
        => new((gtx + 0.5f) * Chunk.TileSize, (gty + 0.5f) * Chunk.TileSize);

    public static bool IsFree(ChunkMap chunks, int gtx, int gty)
        => chunks.GetCellState(gtx, gty) == TileState.Empty && !chunks.Graph.TryGet(gtx, gty, out _);

    // Strict AABB overlap between the cell and a body. A tile grown into a body would
    // crush it (and one grown into the caster would bury it), so either is refused.
    public static bool Overlaps(PhysicsBody body, int gtx, int gty)
    {
        if (body == null) return false;
        var b = body.Bounds;
        float l = gtx * Chunk.TileSize, t = gty * Chunk.TileSize;
        float r = l + Chunk.TileSize,   btm = t + Chunk.TileSize;
        return b.Left < r && b.Right > l && b.Top < btm && b.Bottom > t;
    }

    // Windup marker: a square growing toward the full cell, brightening toward fire.
    public static void Marker(TelegraphList t, int gtx, int gty, float p, Color hot)
    {
        var c = Center(gtx, gty);
        float size = 3f + p * (Chunk.TileSize - 3f);
        t.Rect(c, size, Color.Lerp(new Color(hot, 70), hot, p) * (0.35f + 0.45f * p));
        t.Rect(c, 2f, hot);
    }
}

// Place one tile `Reach` px along the aim toward the target. Supported placement only:
// it goes through ChunkMap.TryRequestTile, the same call the player's BlockPlaceAction
// makes, so the cell needs a solid neighbour (or an adjacent sprout, which parks it as
// a ghost). Aimed at a target on the same floor, that is a brick standing on the floor
// between the fighter and the target.
//
// The aimed point is snapped to the grid and then settled onto the column it falls in,
// because a body centre sits within a tile of a row boundary and an aim a hair upward
// would otherwise land the tile one row up, unsupported, and fail:
//   * aimed cell requestable            → there
//   * aimed cell empty but unsupported  → the cell below, if requestable
//   * aimed cell occupied               → the first requestable cell up to MaxStack
//                                         above it, so repeated uses stack a wall
// No resolvable cell ⇒ the precondition fails and no energy is spent.
//
// The resolved cell is frozen at Enter as an ABSOLUTE world point (its centre) in
// v.LockedAim — not a unit vector for this action. It round-trips through
// EntityData.Aim, so a mid-windup snapshot restores the same destination, and the
// telegraph (handed only the caster's body) draws exactly the cell that will fill.
// A body standing in the cell at the transition cancels the placement.
public class EnemyPlaceBlockAction : EnemyActionState
{
    public EnemyPlaceBlockAction() : this(ActionSpec.Default(ActionKind.PlaceBlock)) {}
    public EnemyPlaceBlockAction(ActionSpec spec) { Spec = spec; }

    protected float    Windup   => Spec.Windup;
    protected float    Active   => Spec.Active;
    protected float    Recovery => Spec.Recovery;
    protected float    MinRange => Spec.MinRange;
    protected float    MaxRange => Spec.MaxRange;
    protected float    Reach    => Spec.Reach;
    protected TileType Material => Spec.Material;
    protected virtual Color MarkerColor => new(200, 160, 90);

    private const int MaxStack = 2;

    public override int ActivePriority  => Spec.ActivePriority;
    public override int PassivePriority => Spec.PassivePriority;

    // Aim exactly as EnemyLashAction freezes it (EnemyAim.AimAt off the brain's
    // AimWorld), then settle the aimed point onto a buildable cell.
    private bool TryResolve(in EnemyContext ctx, int facing, out int gtx, out int gty)
    {
        gtx = gty = 0;
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var body = ctx.Self.Body;
        var dir  = EnemyAim.AimAt(ctx.Input.AimWorld - body.Position, facing);
        (gtx, gty) = EnemyBlockCells.CellOf(body.Position + dir * Reach);

        if (chunks.CanRequestTile(gtx, gty)) return true;
        if (EnemyBlockCells.IsFree(chunks, gtx, gty))
            return chunks.CanRequestTile(gtx, ++gty);
        for (int k = 0; k < MaxStack; k++)
            if (chunks.CanRequestTile(gtx, --gty)) return true;
        return false;
    }

    public override bool CheckPreConditions(in EnemyContext ctx)
        => ctx.Dist >= MinRange && ctx.Dist <= MaxRange
        && TryResolve(ctx, ctx.Facing == 0 ? 1 : ctx.Facing, out _, out _);

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        // Same inputs as the precondition this frame, so this resolves; the fallback
        // (flat-forward at body height) only guards a direct Enter without one.
        v.LockedAim = TryResolve(ctx, v.LockedFacing, out int gtx, out int gty)
            ? EnemyBlockCells.Center(gtx, gty)
            : ctx.Self.Body.Position + new Vector2(v.LockedFacing * Reach, 0f);
        v.Committed = true;
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
        if (!(prevT < v.WindupDuration && v.TimeInState >= v.WindupDuration)) return;

        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return;
        var (gtx, gty) = EnemyBlockCells.CellOf(v.LockedAim);
        if (EnemyBlockCells.Overlaps(ctx.Self.Body, gtx, gty)) return;
        if (EnemyBlockCells.Overlaps(ctx.Player?.Body, gtx, gty)) return;
        chunks.TryRequestTile(gtx, gty, Material);   // null ⇒ filled / unsupported meanwhile: skip
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f || v.TimeInState >= v.WindupDuration) return;
        var (gtx, gty) = EnemyBlockCells.CellOf(v.LockedAim);
        EnemyBlockCells.Marker(t, gtx, gty, v.TimeInState / v.WindupDuration, MarkerColor);
    }
}

// Conjure one tile `HalfHeight` px directly above where the target stood at Enter.
// Needs no line of sight and no support (ChunkMap.ForceSprout, the unsupported-sprout
// path BlockBurstAction uses) — that is the point premium over PlaceBlock. The target
// can step out from under it during the windup, and must: it reads the marker.
//
// Terrain has no gravity in this sim (TileMassField only settles fractional build
// mass; a whole tile never falls), so the block hangs where it was conjured — a
// ceiling that caps a jump, or a stepping stone for whoever gets there first.
//
// The destination is frozen at Enter as an ABSOLUTE world point in v.LockedAim (it is
// not a unit vector for this action). LockedAim round-trips through EntityData.Aim, so
// a snapshot mid-windup restores the same cell, and the telegraph — which is handed
// only the caster's body — can draw it.
public class EnemySpawnBlockInAirAction : EnemyActionState
{
    public EnemySpawnBlockInAirAction() : this(ActionSpec.Default(ActionKind.SpawnBlockInAir)) {}
    public EnemySpawnBlockInAirAction(ActionSpec spec) { Spec = spec; }

    protected float    Windup   => Spec.Windup;
    protected float    Active   => Spec.Active;
    protected float    Recovery => Spec.Recovery;
    protected float    MinRange => Spec.MinRange;
    protected float    MaxRange => Spec.MaxRange;
    protected float    Height   => Spec.HalfHeight;
    protected TileType Material => Spec.Material;
    protected virtual Color MarkerColor => new(230, 120, 220);

    public override int ActivePriority  => Spec.ActivePriority;
    public override int PassivePriority => Spec.PassivePriority;

    private Vector2 PointAbove(in EnemyContext ctx) => ctx.Player.Body.Position - new Vector2(0f, Height);

    public override bool CheckPreConditions(in EnemyContext ctx)
    {
        if (ctx.Dist < MinRange || ctx.Dist > MaxRange) return false;
        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return false;
        var (gtx, gty) = EnemyBlockCells.CellOf(PointAbove(ctx));
        return EnemyBlockCells.IsFree(chunks, gtx, gty);
    }

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.LockedFacing = ctx.Facing == 0 ? 1 : ctx.Facing;
        v.LockedAim    = PointAbove(ctx);   // absolute destination — see the header
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
        if (!(prevT < v.WindupDuration && v.TimeInState >= v.WindupDuration)) return;

        var chunks = ctx.Spawner?.Chunks;
        if (chunks == null) return;
        var (gtx, gty) = EnemyBlockCells.CellOf(v.LockedAim);
        if (EnemyBlockCells.Overlaps(ctx.Self.Body, gtx, gty)) return;
        if (EnemyBlockCells.Overlaps(ctx.Player?.Body, gtx, gty)) return;
        chunks.ForceSprout(gtx, gty, Material);      // null ⇒ filled meanwhile: skip
    }

    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        if (v.WindupDuration <= 0f || v.TimeInState >= v.WindupDuration) return;
        var (gtx, gty) = EnemyBlockCells.CellOf(v.LockedAim);
        EnemyBlockCells.Marker(t, gtx, gty, v.TimeInState / v.WindupDuration, MarkerColor);
    }
}
