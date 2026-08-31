using System;
using Microsoft.Xna.Framework;

namespace MTile;

// ---------- Ranged: Laser (R press) ---------------------------------------------
//
// A cutting beam that pays for what it eats. Two phases, both bounded:
//
//   SCAN (ChargeSeconds) — the elongated rectangle the shot will occupy is projected
//   from the muzzle toward the cursor and drawn as a telegraph. Aim tracks the cursor
//   for the whole scan, so the player lines the box up on what they want cut.
//
//   BURN (FireSeconds)   — the direction locks and a burn front sweeps down that
//   rectangle at ScanSpeed. Every solid cell the front crosses is DESTROYED outright
//   (no HP chipping — the beam either affords a block or it doesn't) and charges the
//   shot its material's MaxHP out of a fixed power budget. Air is free. The front stops
//   at whichever comes first: the budget hitting zero, or the far end of the box.
//
// So the reach is terrain-dependent in a way that reads instantly: through open air the
// laser lances the full MaxLength; into stone (2.0/cell) it bores about a third as deep
// as it does through dirt (1.0), and sand (0.5) barely slows it.
//
// DAMAGE is one hitbox over the swept region — the rectangle from the muzzle out to
// wherever the front has reached, republished (growing) every burn frame under a single
// HitId so CombatSystem's (HitId, Target) dedupe lands it on each body exactly once.
// "Prior to where it runs out of power" is therefore literal: a body inside the box
// ahead of the stopping point is inside the published shape, and one behind the
// stopping point never is. `origin` is the muzzle, so the hit is occluded — but the
// burn has already cleared its own line of sight, so occlusion only matters for bodies
// tucked behind the lip of a cell just outside the burn width.
//
// Entities only. The tile side is the burn above, which is a different model (destroy
// outright + spend budget) than the per-frame HP the tile hitbox path applies; letting
// the same box do both would chip cells the burn had already decided it couldn't pay
// for. Same split as BurstAction's, arrived at for the same reason.
//
// Non-cancellable once started: no CheckConditions escape on releasing R. The commitment
// is the price of the reach, and a flinch (RecoveryAction's involuntary eviction) is
// still the way it gets interrupted.
public class LaserAction : ActionState
{
    // ── Timing ────────────────────────────────────────────────────────────────────
    private const float ChargeSeconds = 0.28f;   // the scan: aim tracks the cursor
    private const float FireSeconds   = 0.34f;   // the burn window
    private const float RecoverySeconds = 0.30f;

    // ── Geometry ──────────────────────────────────────────────────────────────────
    private const float MaxLength   = 416f;                      // px, tile-size independent — the box's far end
    private const float HalfWidth   = 10f;                       // 20px across ≈ 1.25 tiles
    private const float MuzzleOffset = PlayerCharacter.Radius * 1.2f;
    // Front speed. Sized so an unobstructed shot reaches the far end at ~0.26s, leaving
    // the rest of FireSeconds as a beat where the full-length box is still live — which
    // is also what guarantees a stopped-early beam publishes its final extent for
    // several frames rather than one.
    private const float ScanSpeed   = MaxLength / 0.26f;
    // A point-blank shot still needs a box with area, and the polygon must not degenerate
    // on the first burn frame.
    private const float MinHitLength = 12f;

    // ── Burn sampling ─────────────────────────────────────────────────────────────
    // Half-tile marching along the axis, five samples across the width (5px apart, so no
    // 16px cell inside the box can be stepped over). ~20 cell probes on a 60fps frame.
    private const float BurnStep       = Chunk.TileSize * 0.5f;
    private const int   LateralSamples = 5;

    // ── Budget & damage ───────────────────────────────────────────────────────────
    // TileMaxHP-units. A cell costs its material's MaxHP (sand 0.5, dirt 1, stone 2), and
    // the box is 1.25 cells wide so a column of the tunnel is ~2 cells. That puts the DEPTH
    // through solid rock at ~6 tiles, dirt ~12, sand ~24 (by which point MaxLength is the
    // binding constraint, not the budget) — the material spread reads as reach, which is
    // the whole point of paying per block.
    private const float PowerBudget    = 24f;
    private const float Damage         = 1.0f;    // ×15 ⇒ ~15% escalation, a heavy
    private const float Knockback      = 480f;
    private const float HitstunSeconds = 0.22f;

    private static readonly Color ScanColor = new(120, 40, 160);
    private static readonly Color BurnColor = new(255, 90, 220);

    public override int ActivePriority  => 40;
    public override int PassivePriority => 45;

    // Scan + burn are one activation as far as the overlay is concerned, so the clip
    // sweeps once across the whole thing.
    public override float AnimationProgress(in ActionVars vars)
        => vars.TimeInState / (ChargeSeconds + FireSeconds);

    // The animator re-aims the authored (horizontal) overlay along the shot.
    public override bool TryAnimationAim(in ActionVars vars, out Vector2 dir)
    {
        dir = vars.BeamDir;
        return dir.LengthSquared() > 1e-6f;
    }

    public override bool CheckPreConditions(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        if (!ctx.Input.R) return false;
        var prev = ctx.Controller.GetPrevious(1);
        if (prev.R) return false;                       // press edge only
        if (ctx.Combat?.BlocksAttack == true) return false;
        if (ab.Condition.RecoveryActive)    return false;
        // From-set: neutral/recovery, or straight out of a live Guard — same as the
        // other ranged openers (Grenade, Beam).
        if (ctx.RecoveryIndex() == null && ctx.PreviousAction(0) is not GuardAction) return false;
        return true;
    }

    public override bool CheckConditions(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        if (ctx.Combat?.BlocksAttack == true) return false;
        return vars.Firing ? vars.FiringTime < FireSeconds : vars.ChargeTime < ChargeSeconds;
    }

    public override void Enter(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState    = 0f;
        vars.ChargeTime     = 0f;
        vars.FiringTime     = 0f;
        vars.Firing         = false;
        vars.LaserPower     = PowerBudget;
        vars.LaserReach     = 0f;
        vars.LaserFireFrame = 0;
        vars.HitId          = ctx.HitIds.Next();
        vars.BeamDir        = Aim(ctx, ab);
    }

    public override void Update(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        vars.TimeInState += ctx.Dt;

        if (!vars.Firing)
        {
            // Scan. Aim is live — the box follows the cursor until the shot commits.
            vars.ChargeTime += ctx.Dt;
            vars.BeamDir = Aim(ctx, ab);
            if (vars.ChargeTime < ChargeSeconds) return;
            vars.Firing         = true;
            vars.LaserFireFrame = ctx.CurrentFrame;
            return;                                   // the burn starts next frame
        }

        vars.FiringTime += ctx.Dt;

        var dir = vars.BeamDir;
        if (dir.LengthSquared() < 1e-6f) return;
        // The muzzle rides the body: a laser fired while being knocked around drags its
        // origin along. Only the DIRECTION is locked.
        var muzzle = ctx.Body.Position + dir * MuzzleOffset;

        // Advance the front and destroy whatever it crossed this frame. Burn writes the
        // new LaserReach itself, because a power-out mid-step freezes the front short of
        // where it would otherwise have travelled.
        float from = vars.LaserReach;
        if (from < MaxLength && vars.LaserPower > 0f)
            Burn(ctx, muzzle, dir, from, MathF.Min(MaxLength, from + ScanSpeed * ctx.Dt), ref vars);

        // One hitbox over everything the beam has passed through so far.
        if (ctx.Hitboxes == null) return;
        float len      = MathF.Max(vars.LaserReach, MinHitLength);
        var   poly     = Polygon.CreateRectangle(len, HalfWidth * 2f);
        var   center   = muzzle + dir * (len * 0.5f);
        float rotation = MathF.Atan2(dir.Y, dir.X);
        ctx.Hitboxes.Publish(new Hitbox(
            poly.GetBoundingBox(center, rotation), vars.HitId, Damage,
            dir * Knockback,
            ctx.Faction, ctx.SelfId, BurnColor,
            targets: HitTargets.EntitiesOnly,
            shape: poly, shapePos: center, shapeRotation: rotation,
            hitstunSecondsOverride: HitstunSeconds,
            origin: muzzle));
    }

    public override void Exit(EnvironmentContext ctx, PlayerAbilityState ab, ref ActionVars vars)
    {
        ConditionState.SetForSeconds(ref ab.Condition.RecoveryActive, ref ab.Condition.RecoveryExpireFrame,
                                     RecoverySeconds, ctx.CurrentFrame, ctx.Dt);
    }

    // Planted stance through scan and burn alike — the same commitment Beam asks for.
    public override void ApplyMovementModifiers(ref MovementModifiers m, in ActionVars vars)
    {
        m.MaxWalkSpeed *= 0.35f;
        m.WalkAccel    *= 0.5f;
        m.MaxAirSpeed  *= 0.5f;
        m.AirAccel     *= 0.5f;
    }

    // ── The burn ──────────────────────────────────────────────────────────────────
    // Walk the front from `from` to `to` (distances along dir, measured from the muzzle),
    // destroying every solid cell inside the rectangle's width as it goes and charging
    // each one to the budget. Writes vars.LaserReach to the furthest distance actually
    // cleared: on a power-out that is the step where the budget ran dry, and the front
    // never moves again (Update stops calling in once LaserPower hits zero).
    //
    // Deterministic order — front distance ascending, then lateral offset ascending —
    // so which cell drains the last of the budget is a pure function of sim state, and a
    // rollback replay eats the same blocks in the same order.
    //
    // Cells are re-probed rather than remembered: a cell the front already cleared reads
    // Empty and is skipped, so overlapping lateral samples cost nothing and no dedupe
    // structure has to ride in ActionVars.
    private static void Burn(EnvironmentContext ctx, Vector2 muzzle, Vector2 dir,
                             float from, float to, ref ActionVars vars)
    {
        var chunks = ctx.Chunks;
        if (chunks == null) { vars.LaserReach = to; return; }

        var perp    = new Vector2(-dir.Y, dir.X);
        float spread = 2f * HalfWidth / (LateralSamples - 1);
        int steps   = (int)MathF.Ceiling((to - from) / BurnStep);

        for (int i = 1; i <= steps; i++)
        {
            float d = MathF.Min(to, from + i * BurnStep);
            var   axis = muzzle + dir * d;
            for (int l = 0; l < LateralSamples; l++)
            {
                var p   = axis + perp * (-HalfWidth + l * spread);
                int gtx = (int)MathF.Floor(p.X / Chunk.TileSize);
                int gty = (int)MathF.Floor(p.Y / Chunk.TileSize);
                if (chunks.GetCellState(gtx, gty) == TileState.Empty) continue;

                // Cost is read before the break — the cell's type is gone afterwards.
                float cost = TileDamage.MaxHPFor(chunks.GetCellType(gtx, gty));
                if (!chunks.BreakCell(gtx, gty)) continue;
                vars.LaserPower -= cost;
                // The last cell is paid for even if it overdraws — a beam with a sliver
                // of charge left still takes the block in front of it, then dies. The
                // alternative (refusing what it can't fully afford) leaves the shot
                // stopping one cell short of the wall it visibly just hit.
                if (vars.LaserPower <= 0f)
                {
                    vars.LaserPower = 0f;
                    vars.LaserReach = d;
                    return;
                }
            }
            vars.LaserReach = d;
        }
        vars.LaserReach = to;
    }

    private static Vector2 Aim(EnvironmentContext ctx, PlayerAbilityState ab)
    {
        var toCursor = ctx.Input.MouseWorldPosition - ctx.Body.Position;
        return toCursor.LengthSquared() < 1e-4f
            ? new Vector2(ab.Facing == 0 ? 1f : ab.Facing, 0f)
            : Vector2.Normalize(toCursor);
    }

    // ── Telegraph ─────────────────────────────────────────────────────────────────
    public override void Telegraph(TelegraphList t, PhysicsBody body, in ActionVars vars)
    {
        var dir = vars.BeamDir;
        if (dir.LengthSquared() < 1e-6f) return;
        float rotation = MathF.Atan2(dir.Y, dir.X);
        var   muzzle   = body.Position + dir * MuzzleOffset;

        if (!vars.Firing)
        {
            // The scan reads as a targeting sweep, not as a beam: the full box is a faint
            // wash showing the swathe that is about to be cut, with a bright hairline down
            // its axis that sharpens as the charge completes. Reach shown here is the
            // optimistic one (straight through air) — terrain is what actually shortens it.
            float frac = MathHelper.Clamp(vars.ChargeTime / ChargeSeconds, 0f, 1f);
            var  mid   = muzzle + dir * (MaxLength * 0.5f);
            var  col   = Color.Lerp(ScanColor, BurnColor, frac);
            t.RotatedRect(mid, new Vector2(MaxLength, HalfWidth * 2f), rotation,
                          col * (0.10f + 0.12f * frac));
            t.RotatedRect(mid, new Vector2(MaxLength, 2f), rotation,
                          col * (0.35f + 0.5f * frac));
            t.Rect(body.Position, 4f + 8f * frac, col * 0.7f);
            return;
        }

        // The burn: the swept box with a white-hot core, plus a cap at the front so the
        // stopping point — the thing the power budget decides — is legible.
        float len = MathF.Max(vars.LaserReach, MinHitLength);
        var   c   = muzzle + dir * (len * 0.5f);
        t.RotatedRect(c, new Vector2(len, HalfWidth * 2f), rotation, BurnColor * 0.75f);
        t.RotatedRect(c, new Vector2(len, 4f), rotation, Color.White * 0.85f);
        t.Rect(muzzle + dir * len, HalfWidth * 2f, Color.White * 0.6f);
    }
}
