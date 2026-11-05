using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Bulldog's positioning (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, M2): the old
// BulldogBot.Locomote split into two stances. The stuck hop, hop-at-a-target-above and
// air steer are the controller's MoveExecutor, aimed at whichever stance's Target runs.
//
// Bets are HP guesses, placeholders until M4 tunes them. What matters now is the order:
// while StepOut applies its value (+0.3) beats Chase's (-0.1) by more than the
// controller's PreemptGap + StayBonus (0.35), and Chase also passes then, so StepOut
// takes Move even with a learner correcting both.

// Walk into the opponent's face: stop once inside EngageRange (inside slash reach, so the
// opponent is always within one swing).
public sealed class ChaseStance : Stance
{
    private readonly BulldogBot _bot;
    public ChaseStance(BulldogBot bot) { _bot = bot; }

    public override Bet Bet(BotView v) => StepOutStance.Applies(_bot, v) ? Net.Bet.Pass : new Bet(0.2f, 0.3f, 60);

    // The opponent itself, with the walk tolerance = EngageRange: the executor walks while
    // |dx| > EngageRange, hops at a target well above, and air-steers at the opponent
    // exactly as Locomote did. (A target pulled in by EngageRange would make the executor
    // walk AWAY whenever the gap is already under EngageRange.)
    public override Vector2 Target(BotView v) => v.TargetPos;
    public override float Tolerance => _bot.EngageRange;
}

// Our own end-lag is the one window we can't answer a swing in: step out of their reach
// until it is nearly over (RetreatUntil frames left), then Chase walks straight back in.
public sealed class StepOutStance : Stance
{
    private readonly BulldogBot _bot;
    public StepOutStance(BulldogBot bot) { _bot = bot; }

    public static bool Applies(BulldogBot bot, BotView v)
    {
        if (!bot.RetreatInLag || !v.Grounded || v.TargetHelpless) return false;
        var cond = v.Self.Abilities.Condition;
        int lagLeft = cond.RecoveryActive ? cond.RecoveryExpireFrame - v.SelfFrame : 0;
        return lagLeft > bot.RetreatUntil && MathF.Abs(v.Dx) < bot.RetreatDist && MathF.Abs(v.Dy) < 40f;
    }

    public override Bet Bet(BotView v) => Applies(_bot, v) ? new Bet(0.3f, 0f, 20) : Net.Bet.Pass;

    // RetreatDist from the opponent on our side, at our own height; tolerance 0 so it keeps
    // backing off for as long as it applies (which already requires |dx| < RetreatDist).
    public override Vector2 Target(BotView v) => new(v.TargetPos.X - v.Toward * _bot.RetreatDist, v.Me.Y);
    public override float Tolerance => 0f;
    public override bool  MayJump   => false;
}
