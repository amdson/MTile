using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Foam puff on the target (BlockBurstAction): RMB held with the cursor over dead air,
// then an LMB press edge. The action force-sprouts the cursor cell as Foam and injects
// enough mass that the cascade grows a plus (cursor cell + 4 neighbours), anywhere
// within 128 px of the caster. No meter spend — ForceSprout plus a fixed injection.
//
// What that does to a player depends entirely on whether they can be pushed (measured,
// see BotManeuverBurstTests):
//   * Open floor — growth into a body just displaces it: a plus centred on the body
//     lifts them onto the foam (~30 px up); one centred a row ABOVE the body grows its
//     down-arm into them and shoves them sideways (TerrainCarriedState, ~20–30 px, in
//     practice toward the caster — a little pull into slash range). No damage.
//   * Confined (body fills the gap to a ceiling) — the sprouts can't push them anywhere,
//     so PhysicsWorld.CrushOverlappingSprouts breaks each one and bills SproutCrushDamage
//     (0.5 HP per cell, with stun). Measured: 1.5 HP in one press, 3× a slash.
// So the Score is low in the open (a poke/pull at range, mostly to keep pressure on)
// and high when the target is in a crawlspace.
//
// Gesture constraints, from CheckPreConditions:
//   * PreviousAction(0) must already be BlockPaintAction — so RMB has to be down for at
//     least one settled frame before the LMB edge. RMB held with the cursor in OPEN air
//     makes that hold a paint stroke (not a charge), which is what we want.
//   * The cursor cell must be Empty and NOT paintable by the drag-build (in BuildReach
//     and 4-adjacent to support). A standing body's centre cell has empty cells above
//     and below it, so it's never paintable; the row right above the floor or right
//     under a ceiling would be, and the press would fall through to the LMB wind-up —
//     AimPoint avoids those rows.
//   * No recovery stamp active, not in hitstun.
public sealed class BlockBurstManeuver : BotManeuver
{
    // BlockBurstAction.BurstReach is 128 px (to the cell centre); keep a margin for the
    // perception lag and the frames spent arming.
    public float MaxRange        = 115f;
    // Closer than this the slash is the right answer, and the plus would sprout into our
    // own body too.
    public float MinRange        = 3f * Chunk.TileSize;
    // RMB-only frames before the LMB edge (≥ 1: the action must be settled into paint).
    public int   ArmFrames       = 2;
    public int   FireTimeout     = 3;
    public float OpenUtility     = 0.5f;
    public float ConfinedUtility = 1.6f;
    public int   CooldownMin     = 60;
    public int   CooldownMax     = 100;

    private int _cooldown = 80;
    public override int Cooldown => _cooldown;

    private int  _t;
    private bool _fired;
    private bool _confined;

    public override float Score(BotView v)
    {
        float d = v.Dist;
        if (d < MinRange || d > MaxRange) return 0f;
        bool confined = BotTerrainSense.Confined(v, v.TargetPos);
        var aim = AimPoint(v, confined, ArmFrames + 1);
        if (!Placeable(v, aim)) return 0f;
        // With a banked charge a held RMB becomes a charging hold (ChargeLocksPaint) and
        // the release could spend the eruption — leave the button alone then.
        if (v.Self.Abilities.Meters.ChargeLocksPaint) return 0f;
        return confined ? ConfinedUtility : OpenUtility;
    }

    public override void Begin(BotView v)
    {
        _t = 0;
        _fired = false;
        _confined = BotTerrainSense.Confined(v, v.TargetPos);
        _cooldown = v.Rng.Next(CooldownMin, CooldownMax + 1);
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        if (v.Self.CurrentActionName == nameof(BlockBurstAction)) _fired = true;
        if (_fired) { input.LeftClick = input.RightClick = false; return false; }

        _t++;
        // Track the target through the arm frames: the puff lands wherever the cursor is
        // on the LMB edge.
        input.MouseWorldPosition = AimPoint(v, _confined, Math.Max(0, ArmFrames + 1 - _t));
        input.RightClick = true;
        input.LeftClick  = _t > ArmFrames;
        if (_t > ArmFrames + FireTimeout)
        {
            // Never came out (the cell became paintable / solid, a recovery stamp). Let
            // go of both; nothing has been spent.
            input.LeftClick = input.RightClick = false;
            return false;
        }
        return true;
    }

    // Body centre when confined (all five cells land on/around the body; nowhere for it
    // to go), one row above it in the open (the down-arm shoves instead of lifting the
    // target onto a free platform). Led by perceived velocity over the frames still to
    // go before the press — a walking target otherwise steps out of the plus.
    private static Vector2 AimPoint(BotView v, bool confined, int lead)
    {
        var p = v.TargetPos + v.TargetVel * (lead * Simulation.FixedDt);
        if (!confined) p.Y -= Chunk.TileSize;
        return p;
    }

    // BlockBurstAction's own gate restated: an empty cell the drag-build could NOT paint
    // (not 4-adjacent to solid — that's the support rule the in-reach paint test uses).
    private static bool Placeable(BotView v, Vector2 at)
    {
        var c = BotView.Cell(at);
        if (v.Solid(c)) return false;
        return !(v.Solid(c.X, c.Y + 1) || v.Solid(c.X, c.Y - 1) ||
                 v.Solid(c.X - 1, c.Y) || v.Solid(c.X + 1, c.Y));
    }
}
