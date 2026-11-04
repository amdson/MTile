using System;
using System.Collections.Generic;

namespace MTile.Net;

// Learned tactics: a contextual bandit over three decision points, each a small set of
// options, with weights kept per coarse situation and updated by multiplicative weights
// with fixed-share forgetting (so an option that stops paying off — a human adapting —
// drifts back out of favour).
//
//   approach  (out of reach, closing): yield (Bulldog's chase / dash) / hold / feint
//   after     (our own swing just ended): yield (keep pressing) / back off / guard
//   defend    (their swing is coming): yield (Bulldog's guard) / step out / counter / guard
//
// Situation = distance bucket × what the opponent is doing (attacking / recovering /
// idle, as perceived through the brain's delay line). Reward for a choice = HP dealt in
// the Window frames after it (minus TakenWeight × HP taken: 0 for approach and after, as
// the maneuver-level learner found dealt-only best; 1 for defend, whose whole job is not
// getting hit).
public sealed class TacticBandit
{
    public float Eta = 0.7f, Share = 0.03f, Sharpness = 2f;
    // Arm 0 of every decision is "yield: do what the hand-tuned brain would do", and it
    // starts with this weight (the rest at 1): the learner begins from the tuned policy
    // and deviates on evidence. Without it (first cut, every arm at 1) a 60 s bout is too
    // few samples, the choice stayed near-uniform, and it lost 46/300 to plain Ranger.
    public float Prior = 4f;
    public int   Window = 60;

    private readonly Dictionary<(string, int), float[]> _w = new();
    private readonly List<Open> _open = new();
    private float _myHp = -1f, _theirHp = -1f;

    private sealed class Open { public float[] W; public int Arm, Age; public float Dealt, Taken, TakenWeight; }

    public float[] Weights(string decision, int ctx, int arms)
    {
        if (!_w.TryGetValue((decision, ctx), out var w)) { w = new float[arms]; Array.Fill(w, 1f); w[0] = Prior; _w[(decision, ctx)] = w; }
        return w;
    }

    public IEnumerable<KeyValuePair<(string, int), float[]>> All => _w;

    public int Choose(string decision, int ctx, int arms, Random rng, float takenWeight)
    {
        var w = Weights(decision, ctx, arms);
        double total = 0;
        for (int i = 0; i < arms; i++) total += Math.Pow(w[i], Sharpness);
        double x = rng.NextDouble() * total;
        int arm = arms - 1;
        for (int i = 0; i < arms; i++) { x -= Math.Pow(w[i], Sharpness); if (x <= 0) { arm = i; break; } }
        _open.Add(new Open { W = w, Arm = arm, TakenWeight = takenWeight });
        return arm;
    }

    // Once per frame (the brain's PerFrame hook).
    public void Tick(BotView v)
    {
        float my = v.MyHealth, their = v.TargetHealth;
        if (_myHp >= 0f)
        {
            float taken = MathF.Max(0f, _myHp - my), dealt = MathF.Max(0f, _theirHp - their);
            foreach (var o in _open) { o.Dealt += dealt; o.Taken += taken; }
        }
        _myHp = my; _theirHp = their;
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            var o = _open[i];
            if (++o.Age < Window) continue;
            var w = o.W;
            w[o.Arm] = Math.Clamp(w[o.Arm] * MathF.Exp(Eta * (o.Dealt - o.TakenWeight * o.Taken)), 0.05f, 20f);
            float mean = 0f; foreach (var x in w) mean += x; mean /= w.Length;
            for (int k = 0; k < w.Length; k++) w[k] = (1f - Share) * w[k] + Share * mean;   // fixed share
            _open.RemoveAt(i);
        }
    }

    // What the opponent is doing, as perceived: 0 attacking, 1 recovering, 2 idle/moving.
    public static int TargetState(BotView v)
        => IsAttack(v.TargetAction) ? 0 : v.TargetAction == "RecoveryAction" ? 1 : 2;

    public static bool IsAttack(string a)
        => a.Contains("Slash") || a is "StabAction" or "AirWhipKick" or "AirHammerAction" or "PulseAction"
                                  or "BurstAction" or "GrabAction" or "GuardRetaliateAction";
}

// approach: out of reach and closing. Arms: 0 yield (Bulldog's own chase and dash), 1 hold
// (stand off, let them come), 2 feint (step to their reach edge and straight back out —
// bait a swing).
public sealed class ApproachTactic : BotManeuver
{
    public float MinGap = 48f, MaxGap = 140f;
    private readonly TacticBandit _b;
    private int _arm, _t;
    public ApproachTactic(TacticBandit b) => _b = b;

    public override int  Cooldown => 30;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        if (!v.Grounded || v.TargetHelpless || MathF.Abs(v.Dy) > 30f) return 0f;
        if (gap < MinGap || gap > MaxGap || !v.LineOfSight()) return 0f;
        return 1.25f;
    }

    public override void Begin(BotView v)
    {
        int ctx = (MathF.Abs(v.Dx) < 85f ? 0 : 3) + TacticBandit.TargetState(v);
        _arm = _b.Choose("approach", ctx, 3, v.Rng, 0f);
        _t = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        float gap = MathF.Abs(v.Dx);
        switch (_arm)
        {
            case 0: return false;
            case 1: return gap > 44f && _t < 30;
            default:
                if (_t < 30 && gap > 50f) { BotInput.Move(ref input, v.Toward); return true; }
                BotInput.Move(ref input, -v.Toward);
                return _t < 48;
        }
    }
}

// after: our own swing just ended. Arms: 0 keep pressing (yield straight back to the melee
// maneuvers), 1 back off out of reach, 2 guard (parry, retaliate if it charges).
public sealed class AfterSwingTactic : BotManeuver
{
    private readonly TacticBandit _b;
    private int _arm, _t;
    public AfterSwingTactic(TacticBandit b) => _b = b;

    public override int  Cooldown => 20;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        if (!v.Grounded || v.TargetHelpless) return 0f;
        if (v.FramesSinceAttack < 6 || v.FramesSinceAttack > 14) return 0f;
        if (MathF.Abs(v.Dx) > 60f || MathF.Abs(v.Dy) > 30f) return 0f;
        return 1.4f;
    }

    public override void Begin(BotView v)
    {
        int ctx = TacticBandit.TargetState(v);
        _arm = _b.Choose("after", ctx, 3, v.Rng, 0f);
        _t = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        switch (_arm)
        {
            case 0: return false;
            case 1: BotInput.Move(ref input, -v.Toward); return _t < 18;
            default: return DefendTactic.GuardStep(v, ref input, _t, 16);
        }
    }
}

// defend: their swing is coming. Arms: 0 yield (Bulldog's own guard, which parries only
// slow moves), 1 step out of reach, 2 counter-swing (equal swings clank), 3 guard anyway.
public sealed class DefendTactic : BotManeuver
{
    public float ThreatRange = 75f;
    private readonly TacticBandit _b;
    private int _arm, _t;
    public DefendTactic(TacticBandit b) => _b = b;

    public override int  Cooldown => 15;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        if (!v.Grounded || v.Dist > ThreatRange) return 0f;
        var c = v.Self.Combat;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return 0f;
        if (!TacticBandit.IsAttack(v.TargetAction) || v.TargetAction == "GuardRetaliateAction") return 0f;
        return 1.6f;
    }

    public override void Begin(BotView v)
    {
        bool slow = v.TargetAction is "StabAction" or "AirHammerAction" or "DownAirSlash" or "PulseAction" or "GroundSlash3" or "GrabAction";
        int ctx = (v.Dist < 40f ? 0 : 2) + (slow ? 1 : 0);
        _arm = _b.Choose("defend", ctx, 4, v.Rng, 1f);
        var c = v.Self.Combat;
        if (_arm == 3 && (c.GuardBroken || c.GuardOnCooldown(v.SelfFrame))) _arm = 1;
        _t = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        switch (_arm)
        {
            case 0: return false;
            case 1: BotInput.Move(ref input, -v.Toward); return _t < 14;
            case 2:
                if (_t == 1) { input.LeftClick = true; if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward); }
                return _t < 4;
            default: return GuardStep(v, ref input, _t, 22);
        }
    }

    // BulldogGuard's stance: hold Shift (no direction) up to `max` frames; once a parry
    // charges the counter, click it out.
    public static bool GuardStep(BotView v, ref PlayerInput input, int t, int max)
    {
        var c = v.Self.Combat;
        if (c.GuardCharged) { input.LeftClick = t % 5 == 0; return t < max + 10; }
        input.Shift = true;
        if (t > 3 && !c.GuardActive) return false;
        return t < max;
    }
}
