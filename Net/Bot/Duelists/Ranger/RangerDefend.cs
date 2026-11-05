using System;

namespace MTile.Net;

// Two more answers to a seen swing, so the learner has a defend CHOICE (guard / step back /
// counter / nothing) instead of guard-or-nothing. Both are Actions holding Move, so they
// compete with GuardManeuver in the Act selector and the bet ledger ranks them per situation.
// Bets start below Guard's (0.72) so the plain argmax brain still guards; the ledger moves them.

// Step straight back out of the swing's reach for a few frames.
public sealed class StepBackManeuver : BotManeuver
{
    public float ThreatRange = 75f;
    public int   Frames = 14;
    private int _t;

    public override int       Cooldown => 15;
    public override MoveClaim Move     => MoveClaim.Exclusive;   // writes Left/Right itself

    public override float Score(BotView v)
    {
        if (!v.Grounded || v.Dist > ThreatRange) return 0f;
        var c = v.Self.Combat;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return 0f;
        if (!Situation.IsAttack(v.TargetAction) || v.TargetAction == "GuardRetaliateAction") return 0f;
        return 1.3f;
    }

    // Avoids about a 0.5 HP swing most of the time; gives up the counter. 14 f.
    public override Bet Bet(BotView v) => Score(v) > 0f ? new Bet(0.05f, -0.35f, 20) : Net.Bet.Pass;

    public override void Begin(BotView v) => _t = 0;

    public override bool Step(BotView v, ref PlayerInput input)
    {
        BotInput.Move(ref input, -v.Toward);
        return ++_t < Frames;
    }
}

// Swing into their swing: equal attacks clank, a faster one wins the exchange.
public sealed class CounterSlashManeuver : BotManeuver
{
    public float ThreatRange = 50f;
    private int _t;

    public override int       Cooldown => 15;
    public override MoveClaim Move     => MoveClaim.Exclusive;   // turns to face on the first frame

    public override float Score(BotView v)
    {
        if (!v.Grounded || v.Dist > ThreatRange) return 0f;
        var c = v.Self.Combat;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return 0f;
        if (!Situation.IsAttack(v.TargetAction) || v.TargetAction == "GuardRetaliateAction") return 0f;
        return 1.2f;
    }

    // A clank or a trade: ~0.5 landed half the time, eats the other half of the time. 4 f press + lag.
    public override Bet Bet(BotView v) => Score(v) > 0f ? new Bet(0.5f, 0.15f, 30) : Net.Bet.Pass;

    public override void Begin(BotView v) => _t = 0;

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_t == 1)
        {
            input.LeftClick = true;
            if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        }
        return _t < 4;
    }
}
