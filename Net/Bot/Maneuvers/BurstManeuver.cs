using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Close-range blast (BurstAction): LMB held, then an RMB press edge while it's still down.
//
// What the move is: a fan of five boxes thrown out along the cursor direction, anchored
// on the body and expanding 1.5R → 2.5R over ~8 frames. Each box carries two hitboxes —
// a terrain channel (breaks dirt in one touch) and an entity channel with a 430 px/s
// radial shove, 0.15 damage and a short 0.2 s hitstun. So it's a SHOVE, not a damage
// tool: a third of a slash's damage for more knockback than GroundSlash3, reaching
// ~54 px from the body centre (anchor R + shell 30 + box half 12) against the slash's ~47.
//
// Why the gesture is shaped like this: BurstAction.CheckPreConditions needs the LMB
// wind-up (RecoveryAction index 0) to have already picked up the press — "LMB-then-RMB in
// the same frame charges first and the burst lands a couple frames later" — and an RMB
// press EDGE, so a held RMB (drag-build) can't re-fire it. So: press LMB, hold it a few
// frames, add RMB for one edge, hold both until the action shows up, release both. The
// burst's Enter consumes the wind-up's press, so letting LMB up afterwards doesn't also
// buy a slash.
//
// When it's worth it over the slash:
//   * the target is close but OFF the slash's flat band — above us (an up-aimed shell
//     reaches where the slash arc doesn't) or below us while we're airborne (the
//     down-aimed AIR burst also pogos us off them, BurstAction.IsPogo);
//   * in the slash's band, as a lower-utility mixup between slashes (SlashManeuver's
//     cooldown is when this one gets picked), to shove a target off us.
//
// NOTE (pending user change): BurstAction is being reworked in the user's tree to scale
// with charge time and to fire while the player is in a charging state. This maneuver
// only relies on the committed contract (LMB-hold → RMB edge within a few frames); if the
// rework adds a charge, HoldFrames below is the knob to lengthen.
public sealed class BurstManeuver : BotManeuver
{
    // Shell reach from the body centre, with a little margin for the 6-frame startup
    // (the target keeps moving while we arm).
    public float Reach            = 48f;
    // The slash's vertical band (SlashManeuver.VerticalReach). Off-band targets in reach
    // are this move's best case.
    public float SlashBand        = 30f;
    public float OffBandUtility   = 1.15f;
    public float InBandUtility    = 0.7f;
    // Frames LMB is held before the RMB edge. The wind-up needs one settled frame to own
    // the press; BurstPogoTests uses 3.
    public int   HoldFrames       = 3;
    // Give up if the action hasn't started this many frames after the RMB edge (combat
    // lockout, hitstun, a recovery stamp we didn't see).
    public int   FireTimeout      = 4;
    public int   CooldownMin      = 35;
    public int   CooldownMax      = 60;

    private int _cooldown = 45;
    public override int Cooldown => _cooldown;
    public override MoveClaim Move => MoveClaim.Forbid(MoveKeys.Space | MoveKeys.Down);   // turns toward the target with Left/Right; never jump or drop mid-burst

    private int  _t;
    private bool _fired;

    public override float Score(BotView v)
    {
        if (v.Dist > Reach) return 0f;
        // Line of sight matters: the terrain channel would eat a dirt wall, but the
        // entity shove is what we're paying for and it shouldn't be spent on a block.
        if (!v.LineOfSight()) return 0f;
        bool offBand = MathF.Abs(v.Dy) > SlashBand;
        // Below us only counts from the air: grounded, a down-aimed shell just digs.
        if (offBand && v.Dy > 0f && v.Grounded) return 0f;
        return offBand ? OffBandUtility : InBandUtility;
    }

    public override void Begin(BotView v)
    {
        _t = 0;
        _fired = false;
        _cooldown = v.Rng.Next(CooldownMin, CooldownMax + 1);
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        // Cursor stays on the (perceived) target — the shell's direction is read from it
        // at Enter. A cursor behind the facing has its X zeroed (ComputeBurstDir), so
        // turn toward the target on every frame of the wind-up.
        if (v.Facing != v.Toward && MathF.Abs(v.Dx) > 2f) BotInput.Move(ref input, v.Toward);

        if (v.Self.CurrentActionName == nameof(BurstAction)) _fired = true;
        if (_fired) { input.LeftClick = false; input.RightClick = false; return false; }

        _t++;
        input.LeftClick = true;
        // RMB from frame HoldFrames+1 on: the first of those frames is the press edge.
        input.RightClick = _t > HoldFrames;
        if (_t > HoldFrames + FireTimeout)
        {
            // Never came out. Let go of both; the LMB release spends the press as
            // whatever the wind-up resolves to (a slash at worst), which is harmless.
            input.LeftClick = input.RightClick = false;
            return false;
        }
        return true;
    }
}
