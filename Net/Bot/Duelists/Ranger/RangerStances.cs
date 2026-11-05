using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Ranger's extra positioning choice, so the bet ledger has something to pick between on
// Move (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, M4).
//
// Hold just outside the opponent's slash reach, in the 80-95 px band WhiffPunishManeuver
// baits in (its header says why: inside ~84 the bait eats stabs, beyond ~120 the dash
// can't reach). Holding range deals nothing directly but takes little, so the authored
// bet (0.1, 0.1, 60) has value 0: above ChaseStance's -0.1, but by less than the
// controller's PreemptGap + StayBonus (0.35), so with no learning Chase keeps Move and
// the ledger can promote this when it pays off.
public sealed class HoldRangeStance : Stance
{
    public float HoldDist = 90f;
    public float MinGap   = 48f;
    public float MaxGap   = 160f;

    public override Bet Bet(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        if (!v.Grounded || v.TargetHelpless || gap < MinGap || gap > MaxGap || !v.LineOfSight())
            return Net.Bet.Pass;
        return new Bet(0.1f, 0.1f, 60);
    }

    public override Vector2 Target(BotView v) => new(v.TargetPos.X - v.Toward * HoldDist, v.TargetPos.Y);
    public override float Tolerance => 8f;
    public override bool  MayJump   => false;
}
