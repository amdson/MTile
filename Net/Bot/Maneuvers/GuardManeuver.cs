using System;

namespace MTile.Net;

// Guard + counter. GuardAction is Shift held with no Left/Right and no RightClick, on the
// ground, and (CombatState.ResolveGuard) it is a TIMED parry: only a hit landing within
// GuardPerfectWindowSeconds (0.12 s, ~7f) of the stance coming up is absorbed outright;
// the longer Shift stays down the more leaks through (and a leak breaks the guard). So
// the bot raises the stance the moment the target starts a swing - a GroundSlash's hit
// begins 5f after its action starts, inside the window - and drops it again if nothing
// connects, rather than turtling.
//
// A weak (<= 1.0 damage) in-cone parry sets Combat.GuardCharged for 0.8 s, which arms
// GuardRetaliateAction: it fires on a Click while charged (it peeks IntentType.Click, the
// release edge of a short LMB tap), so the counter is: Shift up, tap LMB. Shift is
// released first because a Shift-owned press belongs to the Grab/Beam family
// (InputParser._activePressShift) - though it would still emit a Click.
public sealed class GuardManeuver : BotManeuver
{
    // Only worth raising the guard when the swing can actually reach us.
    public float ThreatRange = 70f;
    // Longest stance held without a block resolving. Leak is already heavy after the
    // perfect window; this just stops the bot standing in guard forever.
    public int MaxGuardFrames = 28;
    // Frames to keep trying the counter click before giving up (the charge lasts 0.8 s,
    // but a counter late in the window is pointless).
    public int RetaliateFrames = 10;

    private enum Phase { Guard, Retaliate }
    private Phase _phase;
    private int _t;
    private int _quiet;      // consecutive guard frames with the target no longer attacking

    public override int Cooldown => 20;
    public override bool OwnsLocomotion => true;   // guard forbids Left/Right

    // The target's action names that hurt us when they connect. Matches by substring so
    // every slash variant (Ground/Air/Crouch/Dash/Turn/DownAir) counts.
    private static bool IsThreat(string a)
        => a.Contains("Slash") || a == "StabAction" || a == "AirWhipKick"
        || a == "AirHammerAction" || a == "PulseAction";

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;                       // GuardAction is ground-only
        var c = v.Self.Combat;
        if (c.GuardBroken || c.GuardOnCooldown(v.SelfFrame)) return 0f;
        if (c.HitstunActive || c.StunActive) return 0f;
        if (v.Dist > ThreatRange) return 0f;
        if (!IsThreat(v.TargetAction)) return 0f;
        // Beats an in-range slash (1.0): a swing is incoming, so parrying first is better.
        return 1.5f;
    }

    public override void Begin(BotView v)
    {
        _phase = Phase.Guard;
        _t = 0;
        _quiet = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        var c = v.Self.Combat;
        if (_phase == Phase.Guard)
        {
            // A parry arms the counter; take it right away.
            if (c.GuardCharged) { _phase = Phase.Retaliate; _t = 0; return Step(v, ref input); }

            input.Shift = true;
            _t++;
            _quiet = IsThreat(v.TargetAction) ? 0 : _quiet + 1;

            // A block (or a break) spends the stance: GuardActive drops. Give it a few
            // frames to come up first (Begin -> next sim step), then stop if it is gone.
            if (_t > 3 && !c.GuardActive) return false;
            // Swing finished without connecting (whiff / dodged): stand down.
            if (_t > 8 && _quiet >= 8) return false;
            return _t < MaxGuardFrames;
        }

        // Retaliate: Shift off, then 1-frame LMB taps (press, release) until the
        // charge is spent - GuardRetaliateAction clears GuardCharged in Enter.
        if (!c.GuardCharged) return false;
        input.LeftClick = _t % 5 < 2;   // ~12 presses/s at most (league input-rate rule)
        _t++;
        return _t < RetaliateFrames;
    }
}
