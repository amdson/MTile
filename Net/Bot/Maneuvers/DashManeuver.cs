using System;

namespace MTile.Net;

// Dash chord to close a mid/long gap fast, with a DashSlash on arrival.
//
// Input (what a hand does): Space and Shift pressed on the same frame — InputParser's
// "Space edge under Shift" branch issues IntentType.Dash (a late Shift would also be
// accepted within DashChordGraceFrames = 3, but we don't rely on it). The dash throws
// the body along body → cursor, and the brain keeps the cursor on the target, so the
// dash aims at where the bot sees P1. On the ground the chord is a jump that dashes off
// the take-off (DashState.Enter forces >= 15 degrees of rise so it doesn't skim the floor).
//
// DashState: 360 px/s for 0.24 s ≈ 86 px of burst, then a half-speed carry. Once per
// airtime (HasDashed), so one dash per jump — the Cooldown also keeps us from chaining
// the chord into a "dash" that is only a plain jump.
//
// DashSlash: a click while DashState runs (PressEdge or Click both qualify; Shift is
// ignored mid-dash). Fires from the press edge with a 3 frame startup, the fastest cut
// in the kit, so we press as soon as the target is within DashSlash reach
// (1.15 x ArcRadius plus the 360 px/s body already carrying us into it).
public sealed class DashManeuver : BotManeuver
{
    // Gap band that is worth a dash: below MinGap an ordinary walk/slash is quicker and
    // the dash would overshoot; above MaxGap the 86 px burst + carry can't arrive.
    public float MinGap = 6f * Chunk.TileSize;     // 66 px
    public float MaxGap = 14f * Chunk.TileSize;    // 154 px
    // Body-to-target distance at which the dash-slash click is thrown.
    public float SlashDist = 46f;
    // Frames to wait for DashState to appear after the chord before giving up
    // (a hit-stunned chord is declined by DashState's capability check).
    private const int EnterTimeout = 12;

    public override int Cooldown => 90;
    public override MoveClaim Move => MoveClaim.Exclusive;   // the input IS movement: Space+Shift dash then Left/Right toward the target

    private int  _t;
    private bool _inDash, _clicked;

    public override float Score(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        // Mostly horizontal only: a dash is a straight line, a target far above/below
        // is the climb / drop maneuvers' business.
        if (gap < MinGap || gap > MaxGap || MathF.Abs(v.Dy) > gap * 0.6f) return 0f;
        if (!v.LineOfSight()) return 0f;
        return 1.2f;
    }

    public override void Begin(BotView v) { _t = 0; _inDash = false; _clicked = false; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        bool dashing = v.Self.CurrentStateName == "DashState";
        if (dashing) _inDash = true;

        // Frame 0: the chord. Frame 1 releases Space (short hop) but keeps Shift.
        if (_t == 0) { input.Space = true; input.Shift = true; }
        else if (_t == 1) { input.Shift = true; }

        if (_inDash)
        {
            if (!dashing) return false;                    // dash over
            BotInput.Move(ref input, v.Toward);
            if (!_clicked && v.Dist <= SlashDist) { input.LeftClick = true; _clicked = true; }
        }
        else if (_t > EnterTimeout) return false;

        _t++;
        return true;
    }
}
