using System;

namespace MTile.Net;

// Parry, but only what a parry can catch. The stance comes up ReactionFrames + 1 after
// the opponent's swing starts and is perfect for ~7 frames, so a 5-frame jab has always
// landed by then — raising guard against one just forfeits our turn. Slow, committal
// moves (stab, hammer, down-air, pulse, the S3 finisher) are caught, and the counter
// (GuardRetaliate) is a free hit. Otherwise as the stock GuardManeuver.
public sealed class BulldogGuard : BotManeuver
{
    public float ThreatRange = 80f;
    public int   MaxGuardFrames = 22, RetaliateFrames = 10;

    private enum Phase { Guard, Retaliate }
    private Phase _phase;
    private int _t, _quiet;

    public override int Cooldown => 20;
    public override bool OwnsLocomotion => true;
    public override MoveClaim Move => MoveClaim.Still;   // guard writes only a click rhythm; the body must hold still

    public bool Wide = true;
    private bool IsSlowThreat(string a)
        => a is "StabAction" or "AirHammerAction" or "DownAirSlash" or "PulseAction"
              or "GroundSlash3" or "BurstAction"
        || (Wide && a is "GroundSlash2" or "AirSlash1" or "AirSlash2" or "AirTurnSlash" or "AirWhipKick");

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        var c = v.Self.Combat;
        if (c.GuardBroken || c.GuardOnCooldown(v.SelfFrame)) return 0f;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return 0f;
        if (v.Dist > ThreatRange) return 0f;
        if (!IsSlowThreat(v.TargetAction)) return 0f;
        return 1.5f;
    }

    // Bet (M4): slow moves only, so the parry lands ~80% -> retaliate 0.5 ~0.4; a stab / hammer /
    // S3 (~0.75 HP) avoided ~40% (-0.3); 22 f guard + 10 retaliate. 0.70: just under the stock Guard.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.4f, -0.3f, 32);
    }

    public override void Begin(BotView v) { _phase = Phase.Guard; _t = 0; _quiet = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        var c = v.Self.Combat;
        if (_phase == Phase.Guard)
        {
            if (c.GuardCharged) { _phase = Phase.Retaliate; _t = 0; return Step(v, ref input); }
            input.Shift = true;
            _t++;
            _quiet = IsSlowThreat(v.TargetAction) ? 0 : _quiet + 1;
            if (_t > 3 && !c.GuardActive) return false;
            if (_t > 6 && _quiet >= 6) return false;
            return _t < MaxGuardFrames;
        }
        if (!c.GuardCharged) return false;
        input.LeftClick = _t % 5 == 0;   // ~12 presses/s
        _t++;
        return _t < RetaliateFrames;
    }
}
