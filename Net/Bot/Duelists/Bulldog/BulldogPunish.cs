using System;

namespace MTile.Net;

// Whiff punish: the opponent is (as we saw it ReactionFrames ago) busy — swinging,
// charging, recovering, flinching — just outside slash range. Dash at them and cut on
// arrival (DashSlash: 3 f startup, the fastest hit in the kit). Same chord as the stock
// DashManeuver; only the trigger differs.
public sealed class BulldogPunish : BotManeuver
{
    public float MinGap = 44f, MaxGap = 150f, SlashDist = 46f;
    private const int EnterTimeout = 12;

    private readonly BulldogSense _s;
    private int _t;
    private bool _inDash, _clicked;

    public BulldogPunish(BulldogSense s) => _s = s;
    public override int Cooldown => 60;
    public override MoveClaim Move => MoveClaim.Exclusive;   // the input IS movement: Space+Shift dash then Left/Right toward the target

    public static bool Busy(string a) => a is not ("NullAction" or "None" or "GuardAction");

    public override float Score(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        if (gap < MinGap || gap > MaxGap || MathF.Abs(v.Dy) > gap * 0.6f) return 0f;
        if (!BulldogKit.CanAttack(v, early: 6)) return 0f;
        if (!(Busy(v.TargetAction) || _s.TargetHelpless)) return 0f;
        if (!v.LineOfSight()) return 0f;
        return 1.35f;
    }

    // Bet (M4): DashSlash (3 f startup) into a busy target, ~0.75 HP with the string it opens,
    // ~85% -> 0.65; they are busy, ~0 taken; dash ~25 f + 11 f cut + 33 f lag ~ 60 f.
    // Value 0.62 sits over a stab / dash / block throw (0.55), as Score 1.35 did over 1.2.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.65f, 0.03f, 60);
    }

    public override void Begin(BotView v) { _t = 0; _inDash = false; _clicked = false; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        bool dashing = v.Self.CurrentStateName == "DashState";
        if (dashing) _inDash = true;
        if (_t == 0) { input.Space = true; input.Shift = true; }
        else if (_t == 1) { input.Shift = true; }
        if (_inDash)
        {
            if (!dashing) return false;
            BotInput.Move(ref input, v.Toward);
            if (!_clicked && v.Dist <= SlashDist) { input.LeftClick = true; _clicked = true; }
        }
        else if (_t > EnterTimeout) return false;
        _t++;
        return true;
    }
}
