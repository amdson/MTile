using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Down-air pogo chop: be above the target in the air and click with the cursor straight
// down. DownAirSlash's aim wedge is ±30 degrees of straight down (AimCosThreshold =
// cos 30 ≈ 0.866 on the normalized body→cursor y component), so we OVERRIDE the cursor
// to a point 100 px below the body instead of leaving it on the target — the target
// only has to be inside the wedge for the hitbox to land, which Over() checks.
// A connect pogoes us back up 200-280 px/s.
//
// Two entries:
//   * already airborne above the target (the brain's chase jump, a knockback) — chop;
//   * grounded near the target — hop toward it first (Space held 14 frames like the
//     brain's own jump) and chop when over it. Score is low (0.6) so a plain slash wins
//     inside slash range; this is the mix-up just outside it.
public sealed class DownAirManeuver : BotManeuver
{
    public override int Cooldown => 80;

    // Vertical window (target below us, Y-down so Dy > 0) in which the chop reaches.
    private const float MinBelow = 12f, MaxBelow = 46f;
    private const int   HopFrames = 14, Timeout = 70;

    private int  _t;
    private bool _grounded0, _clicked;

    private static bool Over(BotView v)
        => v.Dy >= MinBelow && v.Dy <= MaxBelow
           && MathF.Abs(v.Dx) <= 0.5f * v.Dy + 6f;   // tan 30 ≈ 0.577; a little tighter

    public override float Score(BotView v)
    {
        if (!v.Grounded) return Over(v) ? 1.3f : 0f;
        float ax = MathF.Abs(v.Dx);
        if (ax >= 44f && ax <= 80f && MathF.Abs(v.Dy) <= 20f) return 0.6f;
        return 0f;
    }

    public override void Begin(BotView v) { _t = 0; _grounded0 = v.Grounded; _clicked = false; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        // Cursor straight down so the click lands in DownAirSlash's wedge.
        input.MouseWorldPosition = v.Me + new Vector2(0f, 100f);

        if (_clicked) return false;                        // the release frame has been emitted

        if (_grounded0 && _t < HopFrames) input.Space = true;
        BotInput.Move(ref input, MathF.Abs(v.Dx) > 4f ? v.Toward : 0);

        if (!v.Grounded && Over(v))
        {
            input.LeftClick = true;
            _clicked = true;
            return true;
        }

        _t++;
        // Landed without a chop, or took too long: give up.
        if (_t > 6 && v.Grounded) return false;
        return _t <= Timeout;
    }
}
