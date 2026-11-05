using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Ranged block throw for the Ranger duelist. Same paint -> pull -> carry -> swipe gesture
// as BlockThrowManeuver (shares its FindBlock / HasBall helpers) but tuned as mid/long-
// range pressure, and the throw is aimed: it leads the perceived target velocity and
// lobs up to cancel gravity drop.
//
// Numbers relied on:
//   * swipe speed 14 px/frame = 840 px/s; the ball detaches near that, flies under
//     gravity 600 px/s^2, so drop over range R is 0.5*600*(R/~700)^2 (~30 px at 220).
//   * a held clod lasts GrabDissipateSeconds = 8 s (configs/movement_config.json), so the
//     ~1 s gesture loses nothing material.
//   * the block must be within PullPointEntity.GrabReach of the body (FindBlock enforces).
//   * the bot holds still through paint/pull (chasing would drag it off the cell).
// Range: beyond melee (MinRange 60 px) up to MaxRange 280 px; the target may stand up to
// 120 px above (ledge) or 60 below. Score 1.2 beats most melee at range and loses to a
// close-in 1.5ish slash. Cooldown 240 frames (~4 s): frequent, not spam.
public sealed class RangerBlockThrowManeuver : BotManeuver
{
    public float MinRange = 60f;
    public float MaxRange = 280f;
    public float UpReach = 120f;
    public float DownReach = 60f;
    public int   CooldownFrames = 240;

    private const int   PaintFrames = 14;
    private const int   PullFrames  = 50;
    private const int   CarryFrames = 4;
    private const int   SwipeFrames = 5;
    private const float SwipeStep   = 14f;
    private const float LobFraction = 0.8f;  // measured: full drop compensation overshoots (ball already rises from the tracker)
    private const float SwipeSpeed  = 650f;   // effective ball speed assumed for the lob/lead solve
    private const float PullDist    = 48f;

    private enum Phase { Paint, Pull, Carry, Swipe }

    private Phase   _phase;
    private int     _t;
    private Vector2 _block;
    private Vector2 _dir;
    private Vector2 _swipeFrom;

    public override int Cooldown => CooldownFrames;
    public override bool OwnsLocomotion => true;   // default chase hops/dashes, which wrecks the grab
    public override MoveClaim Move => MoveClaim.Still;   // Shift+LMB paint/carry/swipe only; chase hops/dashes would wreck the grab

    public override float Score(BotView v)
    {
        if (v.Self.Combat.GrabbedActive || v.Self.Combat.BlocksAttack) return 0f;
        float ax = MathF.Abs(v.Dx);
        if (ax < MinRange || ax > MaxRange) return 0f;
        if (v.Dy < -UpReach || v.Dy > DownReach) return 0f;
        if (!v.Grounded) return 0f;
        // Lobbed shots arc, so only the stretch near the shooter must be clear.
        if (v.FirstSolidAlong(v.Me, Vector2.Lerp(v.Me, v.TargetPos, 0.35f)) != null) return 0f;
        return BlockThrowManeuver.FindBlock(v).HasValue ? 1.2f : 0f;
    }

    // Bet (M4): aimed clod ~1 HP on a hit (impact), ~60% at 60-280 px; planted at range, little
    // comes back (0.07); 14 paint + <=50 pull + 4 carry + 5 swipe + flight ~ 90 f.
    // 0.53: under the stab / dash (Score 1.2 ties, earlier in the list), over the wave.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.6f, 0.07f, 90);
    }

    public override void Begin(BotView v)
    {
        _block = BlockThrowManeuver.FindBlock(v) ?? v.Me;
        _phase = Phase.Paint; _t = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        switch (_phase)
        {
            case Phase.Paint:
                Hold(ref input);
                input.MouseWorldPosition = _block;
                if (_t >= PaintFrames) { _phase = Phase.Pull; _t = 0; }
                return true;

            case Phase.Pull:
                Hold(ref input);
                input.MouseWorldPosition = _block + new Vector2(0f, -PullDist);
                if (BlockThrowManeuver.HasBall(v)) { _phase = Phase.Carry; _t = 0; return true; }
                return _t <= PullFrames;

            case Phase.Carry:
                Hold(ref input);
                _dir = AimDir(v);
                input.MouseWorldPosition = v.Me + _dir * 30f;
                if (_t >= CarryFrames) { _phase = Phase.Swipe; _t = 0; _swipeFrom = input.MouseWorldPosition; }
                return true;

            default:
                input.MouseWorldPosition = _swipeFrom + _dir * (SwipeStep * _t);
                if (_t >= SwipeFrames) return false;
                Hold(ref input);
                return true;
        }
    }

    // Aim at where the target will be after the flight time, raised to cancel gravity drop.
    private static Vector2 AimDir(BotView v)
    {
        float t = Vector2.Distance(v.Me, v.TargetPos) / SwipeSpeed;
        var aim = v.TargetPos + new Vector2(v.TargetVel.X * (t + 0.1f), 0f);   // + gesture delay
        aim.Y -= 0.5f * 600f * t * t * LobFraction;
        var d = aim - v.Me;
        return d.LengthSquared() < 1f ? new Vector2(v.Toward, 0f) : Vector2.Normalize(d);
    }

    private static void Hold(ref PlayerInput input) { input.Shift = true; input.LeftClick = true; }
}
