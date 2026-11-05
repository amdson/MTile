using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Anti-air: jump up and slash. The opponent is overhead (hopped over us, or dropping
// toward a pogo) -- hop to meet them and click an AirSlash1 aimed at them with the cursor.
//
// Geometry / timing relied on (Y-down; Dy < 0 = target above):
//   * Applies when Dy < -24 (grounded; any height if already airborne) and |Dx| <= 60 (they are above and roughly over us).
//   * A full hop (Space held ~30f) lifts the body ~60+ px, so a target 30..~90 px up is
//     reachable. AirSlash1 is 12f total (6f startup, 3f active) with an arc radius ~0.9x
//     the stock slash, so we click when the perceived target is within ClickRange (~52 px)
//     rather than at launch, and lead a little on their velocity (startup 6f).
//   * The aim is the cursor on the (led) target; an upward click is outside DownAirSlash's
//     bottom sextant, and AirTurnSlash only fires for a click behind Facing, so we hold
//     toward the target (keeping Facing forward) -> the click resolves to AirSlash1.
//     If the target has crossed behind us the same click becomes AirTurnSlash (also an air slash).
//   * Click is one press frame followed by a release (no held LMB).
// Owns locomotion (writes its own Space / direction). Gives up after MaxFrames.
public sealed class AntiAirJumpSlashManeuver : BotManeuver
{
    public float MinAbove      = 24f;   // target at least this far overhead
    public float MaxAbove      = 130f;
    public float MaxDx         = 60f;
    public float ClickRange    = 52f;   // fire the slash within this distance
    public float LeadFrames    = 4f;    // aim lead on target velocity (startup is 6f)
    public int   JumpHoldFrames = 30;
    public int   MaxFrames     = 60;
    public override int Cooldown => 45;
    public override bool OwnsLocomotion => true;

    private int _t, _clicked, _air;   // _clicked: frames since the press (0 = not yet)

    public override float Score(BotView v)
    {
        float dy = v.Dy;
        // Already airborne (e.g. the default chase hopped first): any positive height over
        // us counts; on the ground we need a real overhead gap.
        if (dy > (v.Grounded ? -MinAbove : -6f) || dy < -MaxAbove || MathF.Abs(v.Dx) > MaxDx) return 0f;
        // Rising targets will be past us; descending / hovering ones are the anti-air case.
        // Airborne self only qualifies with the target still in reach to slash.
        if (!v.Grounded && v.Dist > ClickRange * 1.6f) return 0f;
        return 1.4f;
    }

    // Bet (M4): meet them with AirSlash1, 0.5 ~70%, plus the trade it wins (0.5); beating the
    // pogo to it avoids ~0.14; hop + click + 14 f ride-out ~ 45 f.
    // 0.64: the Score 1.4 ties in list order (stab finisher 0.66, wall 0.65, this, AA stab 0.63),
    // all over BulldogPunish (0.62).
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.5f, -0.14f, 45);
    }

    public override void Begin(BotView v) { _t = 0; _clicked = 0; _air = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_t > MaxFrames) return false;
        Vector2 aim = v.TargetPos + v.TargetVel * (LeadFrames * Simulation.FixedDt);
        input.MouseWorldPosition = aim;
        if (MathF.Abs(v.Dx) > 3f) BotInput.Move(ref input, v.Toward);

        if (_clicked > 0)
        {
            // Press already sent: release, then ride out the swing.
            _clicked++;
            return _clicked <= 14;
        }

        if (v.Grounded)
        {
            if (_t <= JumpHoldFrames) input.Space = true;
            return _t <= 8;   // IsGrounded lingers after the takeoff press; never left: give up
        }

        // Airborne: hold the jump for height while rising, click once in range.
        if (_t <= JumpHoldFrames && v.MyVel.Y < 0f) input.Space = true;
        // A click right after takeoff still resolves as a GROUND slash (coyote grace), so
        // wait a few frames of real air time first.
        _air++;
        if (_air >= 5 && v.Dist <= ClickRange) { input.LeftClick = true; _clicked = 1; }
        else if (v.Dy > 0f && v.MyVel.Y > 0f) return false;   // they got below us: abort
        return true;
    }
}
