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

    private enum Phase { Wait, Guard, Retaliate }
    private Phase _phase;
    private int _t;
    private int _wait;       // frames to hold before raising (a read on the string's next hit)
    private int _quiet;      // consecutive guard frames with the target no longer attacking

    // The parry is a READ, not a reaction (2026-11-05): we see the target's action
    // ReactionFrames (6) stale and the light slashes land at 4-7f, so raising on sight is
    // always a frame late. What IS readable is the string: an S1 is followed by S2's hit
    // ~21f after S1 started, an S2 by S3's ~22f after S2 started (FrameData: Total + the
    // 0.4 x Total combo gap + the next startup). Seen at +6, the stance goes up at +16 so
    // its 7f perfect window straddles the follow-up. A heavy with a long wind-up (stab,
    // hammer, pulse) is still reacted to; anything faster is guarded on sight and left to
    // luck, as before.
    private static int WaitFor(string a) => a switch
    {
        "GroundSlash1"    => 10,   // S2 lands at +21 (12 + 4.8 + 4)
        "GroundSlash2"    => 10,   // S3 lands at +22 (10.8 + 4.3 + 7)
        "AirSlash1"       => 11,   // AS2 lands at +24 (12 + 4.8 + 7)
        "AirHammerAction" => 4,    // 15f startup
        _                 => 0,
    };

    public override int Cooldown => 20;
    public override bool OwnsLocomotion => true;   // guard forbids Left/Right
    public override MoveClaim Move => MoveClaim.Still;   // guard writes no Left/Right/Space/Down; the body must hold still under the raised guard

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
        if (c.StunActive) return 0f;
        // GuardAction may come up inside the last 0.2 s (12f) of hit disadvantage - that is
        // the designed escape between a true string's hits (S2 -> S3 lands a frame after the
        // victim is free). Refusing any hitstun (as before 2026-11-05) meant the bot never
        // guarded inside a string once every hit kept its full window.
        if (c.HitstunActive && c.HitstunExpireFrame - v.SelfFrame > 12) return 0f;
        if (v.Dist > ThreatRange) return 0f;
        if (!IsThreat(v.TargetAction)) return 0f;
        // Beats an in-range slash (1.0): a swing is incoming, so parrying first is better.
        return 1.5f;
    }

    // Bet (M4): a parry arms GuardRetaliate (0.5 HP, 280 kb), ~90% of the time vs a seen swing;
    // the swing would otherwise land ~half the time (-0.27 avoided); 28 f guard + 10 retaliate.
    // Value 0.72 beats an in-range string (0.45), as Score 1.5 beat the slash's 1.0, and is
    // 0.02 over BulldogGuard / WallClimb (also Score 1.5) so list order still breaks the tie.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.45f, -0.27f, 40);
    }

    public override void Begin(BotView v)
    {
        _wait  = WaitFor(v.TargetAction);
        _phase = _wait > 0 ? Phase.Wait : Phase.Guard;
        _t = 0;
        _quiet = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        var c = v.Self.Combat;
        if (_phase == Phase.Wait)
        {
            // Hold still (Move is Still) until the read says the follow-up is about to
            // land. If the string was dropped - the target went neutral - stand down.
            _t++;
            if (!IsThreat(v.TargetAction) && v.TargetAction != "RecoveryAction") return false;
            if (_t < _wait) return true;
            _phase = Phase.Guard; _t = 0;
        }
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
