using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// The stab gesture, shared by every maneuver in this family. A stab is NOT a click: LMB
// must stay down longer than InputParser.ClickMaxHoldSeconds (0.2 s = 12 frames at 60 fps)
// AND the cursor must move >= InputParser.StabSwipeThreshold (12 px) player-relative
// between the press and the release, in the direction of the thrust. We hold for
// HoldFrames (> 12, so holdFrames > clickMaxHoldFrames with margin), parking the cursor
// at the body on the press frame and SwipePx out along `dir` for every frame after — so
// the parser's swipe = dir * SwipePx regardless of how the body moves.
//
// Step i: 0 = press, 1..HoldFrames-1 = held with swiped cursor, HoldFrames = release
// (LeftClick false, cursor still swiped). The Stab intent is issued on the release frame;
// the caller returns false from Step on that frame.
internal static class StabGesture
{
    public const int   HoldFrames = 14;
    public const float SwipePx    = 40f;   // >> StabSwipeThreshold so body drift can't cancel it

    // Returns true on the release frame (gesture complete).
    public static bool Drive(BotView v, ref PlayerInput input, int i, Vector2 dir)
    {
        input.LeftClick          = i < HoldFrames;
        input.MouseWorldPosition = i == 0 ? v.Me : v.Me + dir * SwipePx;
        return i >= HoldFrames;
    }

    // Body-centre -> target direction, normalised (never zero).
    public static Vector2 Aim(BotView v)
    {
        var d = v.TargetPos - v.Me;
        return d.LengthSquared() < 1e-4f ? new Vector2(v.Facing == 0 ? 1 : v.Facing, 0f) : Vector2.Normalize(d);
    }

    public static float TargetHp(BotView v) => v.TargetHealth;
}

// Ground (or air-forward) stab: the kit's launcher. Wind-up is ~11 frames after release
// (FrameData.StabAction.Startup), and the action lunges forward at 135 px/s, so it is
// scored for the mid band just OUTSIDE slash reach, where a slash would whiff but the
// stab's 48 px tip plus lunge connects; and as a finisher at low target HP, where the
// slower swing is worth it.
//
// Aim: swipe direction = direction to the target. StabAction declines under Shift (we
// never press it). In the air a forward swipe still selects StabAction, but a swipe
// within 30 deg of straight down becomes AirHammerAction and a backward one
// AirWhipKick, so the air case requires a clearly-forward, shallow target.
public sealed class StabManeuver : BotManeuver
{
    public float MinGap = 36f;     // inside this a slash is the better tool
    public float MaxGap = 84f;     // stab tip 48 + 135 px/s lunge over the wind-up
    public float VerticalReach = 26f;
    public float FinisherHp = 1.5f;
    public int   CooldownMin = 40, CooldownMax = 70;   // long recovery (24f end lag) -> readable rhythm

    private int _i, _cooldown = 40;
    public override int Cooldown => _cooldown;

    public override float Score(BotView v)
    {
        float adx = MathF.Abs(v.Dx);
        if (MathF.Abs(v.Dy) > VerticalReach || adx > MaxGap) return 0f;
        if (!v.Grounded && v.Toward != v.Facing) return 0f;   // backward = whip kick, not us
        bool finisher = StabGesture.TargetHp(v) <= FinisherHp;
        if (adx >= MinGap) return 1.2f;
        return finisher ? 1.4f : 0f;   // point-blank: only worth the wind-up to finish
    }

    public override void Begin(BotView v)
    {
        _i = 0;
        _cooldown = v.Rng.Next(CooldownMin, CooldownMax + 1);
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        // Re-aim every frame (the swipe direction is sampled at release), but keep it
        // near-horizontal on the ground so the stab doesn't clamp into the floor.
        var dir = StabGesture.Aim(v);
        if (v.Grounded && dir.Y > 0.3f) dir = Vector2.Normalize(new Vector2(dir.X, 0.3f));
        if (v.Facing != v.Toward && v.Grounded) BotInput.Move(ref input, v.Toward);
        bool done = StabGesture.Drive(v, ref input, _i, dir);
        _i++;
        return !done;
    }
}
