using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// The shared front half of every avalanche maneuver: charge RMB in the floor, then pull
// the cursor out along a straight line and release so the detached MassBall flies at a
// chosen angle through a chosen point. Not a BotManeuver — the Ride/Charge/Crush
// maneuvers each own one and differ only in where they aim and what they do after.
//
// Why a straight line through the launch point, starting INSIDE the floor:
//   * The paint ball is seeded under the cursor at the press and follows it critically
//     damped (BlockPaintAction, SmoothTime 0.12 s). A cursor that leaves the charge
//     point along a straight line drags the ball along the SAME line — a 1-D chase, so
//     the ball's velocity points exactly along it from the first frame. MassBall has no
//     gravity and only scalar drag, so the release direction is the wave's direction
//     for its whole life (AVALANCHE_RIDING_V2 "Dropped from v1").
//   * Charging needs the cursor buried (BuildMeters.StepCharging only runs while the
//     press started in solid); once the meter passes PaintLockoutMin the hold stays a
//     charge out in the air too, so the ball can travel through the floor and up out of
//     it without painting anything along the way.
//   * A wave carries whoever its FRONT grows into (TerrainCarriedState's entry wants a
//     live horizontal push). The AvalancheRideTests sweep launches every wave from the
//     rider's own cell and all of them catch at every angle 20°–90°. So for a ride the
//     line is aimed through the bot's own feet cell; for a crush it goes through the
//     target's.
//
// Release rule: RMB comes up on the first frame the LIVE ball (read off the action's
// vars — perception, not injection) has reached the launch point along the line, as long
// as it's moving at least ReleaseSpeed. Releasing earlier would start the wave behind the
// point (deposits only commit where supported, so a ball still inside rock wastes mass);
// releasing late starts the wave past the rider, who then never touches its front.
internal sealed class AvalancheLauncher
{
    public enum Status { Running, Fired, Failed }

    // Cursor speed along the line during the pull (px/frame). The ball needs ~12 frames of
    // pull to reach ~85% of cursor speed, so the charge point sits a few tiles back along
    // the line to give it that run-up.
    public float PullSpeed    = 6.5f;
    // Minimum live ball speed to release on (px/s). BlockPaintAction's own floor is 220;
    // margin above it so the eruption can't silently fail on a slow frame.
    public float ReleaseSpeed = 245f;
    // Distance back along the line from the launch point to the charge point (px). Must
    // land in solid; FindChargePoint walks it out further if it doesn't.
    public float RunUp        = 3.0f * Chunk.TileSize;
    public int   PullBudget   = 40;
    public int   ChargeGrace  = 30;

    public Vector2 Origin;      // where the ball should be at release
    public Vector2 Dir;         // unit, y-down
    public Vector2 ChargePoint;
    public int     ChargeFrames;
    public bool    SelectDirt;  // press Num2 on the first frame (foam decays, spikes can't erupt)

    private enum Phase { Pick, Charge, Pull }
    private Phase   _phase;
    private int     _t;
    private Vector2 _cursor;
    public int      FiredFrame = -1;

    // Plan a launch. False when no buried charge point exists along the line (thin floor,
    // or the line runs back into open air) — the caller should not Score > 0.
    public bool Plan(BotView v, Vector2 origin, Vector2 dir, int chargeFrames)
    {
        Origin = origin; Dir = Vector2.Normalize(dir); ChargeFrames = chargeFrames;
        _phase = Phase.Pick; _t = 0; FiredFrame = -1;
        var t = v.Self.ActiveBlockType;
        SelectDirt = v.Self.SpikesSelected || t == TileType.Foam;
        return FindChargePoint(v, Origin, Dir, RunUp, out ChargePoint);
    }

    // Walk back along the line from the launch point until the cursor is buried with a
    // solid cell under it too (so a chipped surface can't un-bury the charge mid-hold).
    public static bool FindChargePoint(BotView v, Vector2 origin, Vector2 dir, float runUp, out Vector2 p)
    {
        for (float back = runUp; back <= runUp + 8f * Chunk.TileSize; back += Chunk.TileSize * 0.5f)
        {
            p = origin - dir * back;
            if (v.Solid(p) && v.Solid(p + new Vector2(0f, Chunk.TileSize))) return true;
        }
        p = default;
        return false;
    }

    // Past the charge: the ball is moving, so letting go now would erupt.
    public bool Pulling => _phase == Phase.Pull;

    // Let go of a charge without erupting: RMB up with the cursor back in the floor, so
    // BlockPaintAction.Exit sees a buried cursor and banks the charge instead. Only
    // meaningful before the pull — once the ball is moving the release IS the eruption.
    public void Cancel(ref PlayerInput input)
    {
        input.RightClick = false;
        input.MouseWorldPosition = ChargePoint;
    }

    public Status Step(BotView v, ref PlayerInput input)
    {
        _t++;
        switch (_phase)
        {
            case Phase.Pick:
                _phase = Phase.Charge; _t = 0;
                if (SelectDirt) { input.Num2 = true; return Status.Running; }
                goto case Phase.Charge;

            case Phase.Charge:
            {
                var m = v.Self.Abilities.Meters;
                // Somebody carved our charge point out: the hold has become a paint stroke.
                if (!v.Solid(ChargePoint)) { input.RightClick = false; return Status.Failed; }
                input.RightClick = true;
                input.MouseWorldPosition = ChargePoint;
                if (_t >= ChargeFrames)
                {
                    if (m.CanFireEruption) { _phase = Phase.Pull; _t = 0; _cursor = ChargePoint; }
                    else if (_t >= ChargeFrames + ChargeGrace) { input.RightClick = false; return Status.Failed; }
                }
                return Status.Running;
            }

            default: // Pull
            {
                _cursor += Dir * PullSpeed;
                input.MouseWorldPosition = _cursor;
                // The live ball, read off the hold (BlockPaintAction keeps it in vars).
                bool holding = v.Self.CurrentActionName == nameof(BlockPaintAction);
                if (!holding) { input.RightClick = false; return Status.Failed; }
                var av = v.Self.CurrentActionVars;
                float along = Vector2.Dot(av.BallPos - Origin, Dir);
                float speed = av.BallVel.Length();
                bool cursorClear = !v.Solid(_cursor);
                if (along >= 0f && speed >= ReleaseSpeed && cursorClear)
                {
                    input.RightClick = false;          // RMB up while moving fast: erupt
                    FiredFrame = v.Frame;
                    return Status.Fired;
                }
                // Never reached the line point at speed (something carved the floor, the
                // ball got dragged off): let go with the cursor back in the floor so this
                // banks instead of erupting somewhere we didn't aim.
                if (_t > PullBudget) { Cancel(ref input); return Status.Failed; }
                input.RightClick = true;
                return Status.Running;
            }
        }
    }
}
