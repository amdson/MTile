using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Bulldog's positioning (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, M2): the old
// BulldogBot.Locomote split into two stances. The stuck hop, hop-at-a-target-above and
// air steer are the controller's MoveExecutor, aimed at whichever stance's Target runs.
//
// Bets (M4, hand-authored, HP): Chase (0.25, 0.4, 60) = -0.15, StepOut (0, -0.25, 20) = +0.25.
// The order is what matters: while StepOut applies its value beats Chase's by more than the
// controller's PreemptGap + StayBonus (0.35), and Chase also passes then, so StepOut takes
// Move even with a learner correcting both. Both stay low enough that a reflex (CornerEscape
// 0.60, AntiAirSidestep 0.64) clears either by more than PreemptGap.

// Walk into the opponent's face: stop once inside EngageRange (inside slash reach, so the
// opponent is always within one swing).
public sealed class ChaseStance : Stance
{
    private readonly BulldogBot _bot;
    public ChaseStance(BulldogBot bot) { _bot = bot; }

    // Walking into a duel for a second: the in-face exchanges cut both ways, and the walk-in
    // eats the first swing more often than it throws one (0.25 dealt, 0.4 taken over 60 f).
    public override Bet Bet(BotView v) => StepOutStance.Applies(_bot, v) ? Net.Bet.Pass : new Bet(0.25f, 0.4f, 60);

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

    // Out of reach for the rest of our lag: deals nothing, avoids the ~0.5 HP punish about half
    // the time (-0.25 taken) over the ~20 f it applies.
    public override Bet Bet(BotView v) => Applies(_bot, v) ? new Bet(0f, -0.25f, 20) : Net.Bet.Pass;

    // RetreatDist from the opponent on our side, at our own height; tolerance 0 so it keeps
    // backing off for as long as it applies (which already requires |dx| < RetreatDist).
    public override Vector2 Target(BotView v) => new(v.TargetPos.X - v.Toward * _bot.RetreatDist, v.Me.Y);
    public override float Tolerance => 0f;
    public override bool  MayJump   => false;
}
