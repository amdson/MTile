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

// Hug: walk inside Chase's EngageRange to point-blank, where grabs and strings start and
// the opponent's long normals whiff. Bet value 0 like HoldRange: the ledger decides.
public sealed class PressStance : Stance
{
    public float HugDist = 10f;
    public float MaxGap  = 60f;

    public override Bet Bet(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        if (!v.Grounded || gap > MaxGap || MathF.Abs(v.Dy) > 30f) return Net.Bet.Pass;
        return new Bet(0.1f, 0.1f, 60);
    }

    public override Vector2 Target(BotView v) => new(v.TargetPos.X - v.Toward * HugDist, v.TargetPos.Y);
    public override float Tolerance => 4f;
}

// Kite: hold the aimed block throw's sweet spot (RangerBlockThrowManeuver: 60-280 px, best
// around 120) and let the throws and whiff punishes do the work. Backs off when the
// opponent closes, walks in when they run. Bet value 0: the ledger decides.
public sealed class KiteStance : Stance
{
    public float KiteDist = 120f;
    public float MinGap   = 50f;
    public float MaxGap   = 240f;

    public override Bet Bet(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        if (!v.Grounded || v.TargetHelpless || gap < MinGap || gap > MaxGap || !v.LineOfSight()) return Net.Bet.Pass;
        return new Bet(0.1f, 0.1f, 60);
    }

    public override Vector2 Target(BotView v) => new(v.TargetPos.X - v.Toward * KiteDist, v.TargetPos.Y);
    public override float Tolerance => 10f;
    public override bool  MayJump   => false;
}
