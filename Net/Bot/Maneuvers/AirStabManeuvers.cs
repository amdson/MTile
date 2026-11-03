using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Air hammer: swipe within 30 deg of straight down (AirHammerAction.AimCosThreshold =
// cos 30) while airborne. Wind-up 15 frames, a 1-body-wide box 1.9R under the feet, and a
// slam that drives the body down at 180 px/s — so it wants us DIRECTLY ABOVE the target
// with a little height. Grounded, it first hops (Space), steers over the target, then
// holds the swipe on the way over. Owns locomotion (it does its own Space press).
public sealed class AirHammerManeuver : BotManeuver
{
    public float LaunchRangeX = 70f;     // grounded: how far a hop can still get above them
    public float AlignX       = 20f;     // airborne: close enough above to start the swipe
    // Height needed over the target (Dy, Y-down: target below = positive). The wind-up is
    // 15f after the 14f swipe, and the body falls ~16-20 px of it, while the hammer box
    // only spans 23..59 px below the body centre (AirHammerAction: DownOffset 1.9R,
    // height 1.5 x 2R) and the target's capsule must overlap it. A plain hop gives ~25 px
    // of apex, which is NOT enough over a same-level target — so we need a real drop.
    public float MinAbove     = 30f;
    public float GroundedMinDrop = 20f;  // on a ledge: the hop's apex adds ~25 more
    public float MaxAbove     = 110f;
    public int   MaxFrames    = 90;      // hop + align + gesture budget
    public int   JumpHoldFrames = 30;    // near-full hop: swipe (14f) + wind-up (15f) must finish before landing
    public override int Cooldown => 60;
    public override bool OwnsLocomotion => true;

    private int _t, _gi;
    private bool _inGesture;

    public override float Score(BotView v)
    {
        // Y-down: target BELOW us means Dy > 0.
        if (!v.Grounded)
            return v.Dy >= MinAbove && v.Dy <= MaxAbove && MathF.Abs(v.Dx) <= AlignX * 2f ? 1.3f : 0f;
        // On the ground: hop-and-slam only off a ledge above them (see MinAbove).
        return MathF.Abs(v.Dx) <= LaunchRangeX && v.Dy >= GroundedMinDrop ? 0.8f : 0f;
    }

    public override void Begin(BotView v) { _t = 0; _gi = 0; _inGesture = false; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_t > MaxFrames) return false;
        input.MouseWorldPosition = v.TargetPos;

        if (!_inGesture)
        {
            if (v.Grounded)
            {
                if (_t <= JumpHoldFrames) input.Space = true;
                if (MathF.Abs(v.Dx) > 4f) BotInput.Move(ref input, v.Toward);
                return _t <= 8;   // IsGrounded lingers a few frames after the takeoff press; never left: give up
            }
            // Airborne: steer over the target; start the gesture once roughly above it.
            if (MathF.Abs(v.Dx) > 3f) BotInput.Move(ref input, v.Toward);
            if (v.Dy < MinAbove * 0.5f && v.MyVel.Y > 0f) return false;   // already below their head: abort
            if (MathF.Abs(v.Dx) <= AlignX) { _inGesture = true; _gi = 0; }
            else return true;
        }

        if (MathF.Abs(v.Dx) > 3f) BotInput.Move(ref input, v.Toward);
        bool done = StabGesture.Drive(v, ref input, _gi, Vector2.UnitY);
        _gi++;
        return !done;
    }
}

// Air whip kick: a BACKWARD air swipe (intent.X * Facing < 0, within +-25 deg of
// horizontal, from the hip) — a fast poke for when the target is behind us. The natural
// way to face away from a target mid-air is to have jumped over it, so this is a
// cross-up: hop toward the target (holding toward keeps Facing forward), and once they
// are behind us and within the ~50 px kick reach, hold the swipe straight back at them.
// AirWhipKick.Enter flips Facing to the swipe side, which is the kick.
public sealed class AirWhipKickManeuver : BotManeuver
{
    public float LaunchRangeX = 55f;     // hop from here: crosses over the target
    public float KickReachX   = 44f;     // behind-us gap at which the kick connects (reach ~50)
    public int   JumpHoldFrames = 30;    // near-full hop, same reason as the hammer
    public int   MaxFrames    = 80;
    public override int Cooldown => 50;
    public override bool OwnsLocomotion => true;

    private int _t, _gi, _facing;
    private bool _inGesture;

    public override float Score(BotView v)
    {
        if (!v.Grounded)   // already airborne with the target behind us and in reach
            return v.Toward != v.Facing && MathF.Abs(v.Dx) <= KickReachX && MathF.Abs(v.Dy) <= 40f ? 1.2f : 0f;
        // Grounded, target a hop away on our level: cross-up.
        return MathF.Abs(v.Dx) <= LaunchRangeX && MathF.Abs(v.Dx) >= 16f && MathF.Abs(v.Dy) <= 20f ? 0.6f : 0f;
    }

    public override void Begin(BotView v) { _t = 0; _gi = 0; _inGesture = false; _facing = v.Facing; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_t > MaxFrames) return false;
        input.MouseWorldPosition = v.TargetPos;

        if (!_inGesture)
        {
            if (v.Grounded)
            {
                if (_t <= JumpHoldFrames) input.Space = true;
                BotInput.Move(ref input, v.Toward);
                return _t <= 8;   // IsGrounded lingers a few frames after the takeoff press; never left: give up
            }
            // Airborne: keep drifting toward (and past) the target until it's behind us.
            BotInput.Move(ref input, v.Toward);
            // The swipe takes 14f to release and the kick is active ~6f later, so start it
            // ~14f before we expect to be over the target (|vx| in px/s -> px per frame).
            float lead = MathF.Max(10f, MathF.Abs(v.MyVel.X) * StabGesture.HoldFrames * Simulation.FixedDt);
            if (v.Toward != v.Facing || MathF.Abs(v.Dx) > lead) return true;
            _inGesture = true; _gi = 0; _facing = v.Facing;
        }

        // Keep drifting toward the target so we cross it by release (Facing holds).
        BotInput.Move(ref input, v.Toward);
        // Backward = opposite of Facing; near-horizontal (MaxAimAngle 0.44 rad) at the
        // target's height.
        float back = -_facing;
        var dir = Vector2.Normalize(new Vector2(back, Math.Clamp(v.Dy / 60f, -0.3f, 0.3f)));
        bool done = StabGesture.Drive(v, ref input, _gi, dir);
        _gi++;
        return !done;
    }
}
