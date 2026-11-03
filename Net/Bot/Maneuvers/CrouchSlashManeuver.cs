using System;

namespace MTile.Net;

// Low poke: hold Down until CrouchedState is up, then tap LMB. CrouchSlash only fires
// from CrouchedState (precondition `PreviousState(0) is CrouchedState`, grounded), has
// 1.45 x ArcRadius reach (a bit longer than a stand slash's) and a short 9 frame tail:
// the safest normal in the kit, and it hits targets that are LOWER than us (a
// crouched/short target, a lower step).
//
// Score: grounded with the target 6-30 px below our centre line (a stand slash is a flat
// arc and tends to whiff over it) and within Reach. 1.1 beats SlashManeuver's 1.0 there.
public sealed class CrouchSlashManeuver : BotManeuver
{
    public float Reach = 46f;
    // Frames of Down before the click: CrouchedState needs the Down key and a
    // crouch-ground probe; 3 frames is a margin over the 1 frame the FSM needs.
    private const int CrouchFrames = 3;

    public override int Cooldown => 40;

    private int _t;

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        float ax = MathF.Abs(v.Dx);
        if (ax > Reach) return 0f;
        // Target below our centre line (a short opponent, a step below).
        if (v.Dy > 6f && v.Dy < 30f) return 1.1f;
        return 0f;
    }

    public override void Begin(BotView v) => _t = 0;

    public override bool Step(BotView v, ref PlayerInput input)
    {
        input.Down = true;
        input.Left = input.Right = false;                 // crouch in place
        if (_t == CrouchFrames) input.LeftClick = true;   // press; next frame's release = Click
        _t++;
        return _t <= CrouchFrames + 1;
    }
}
