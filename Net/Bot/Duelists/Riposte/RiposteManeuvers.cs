using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Riposte's own maneuvers. Opponent facts come from RiposteSense (delayed); self/terrain
// from the BotView. Attack gestures are the stock ones (a 1-frame LMB tap = slash, the
// StabGesture hold+swipe), so InputParser decodes them exactly like a mouse.

// ── Parry: Guard stance on a read or on a visible slow wind-up, then the counter ─────
//
// GuardAction is a timed parry: a hit within ~7 frames of the stance coming up is fully
// absorbed and (for a weak hit) arms GuardRetaliate for 0.8 s. A late block still eats
// most of the hit but breaks the stance for 0.5 s. So:
//   * slow wind-ups (stab 11f, air hammer 15f, pulse 12f) are parried ON REACTION — the
//     delayed pose still leaves time to raise the stance inside the window;
//   * fast slashes (5f) can't be reacted to, so they are parried on a READ: the
//     opponent is closing into its swing range and the model says it swings on entry;
//   * a combo follow-up after a slash that just hit us (guard comes up out of light
//     hitstun).
public sealed class RiposteParry : BotManeuver
{
    private readonly RiposteSense _s;
    public RiposteParry(RiposteSense s) => _s = s;

    private enum Phase { Guard, Retaliate }
    private Phase _phase;
    private int _t, _hold, _sinceClick;
    private bool _read;

    public int ReadsTried, Parries, Counters;   // stats for tuning
    private int _cooldown = 12;
    public override int Cooldown => _cooldown;
    public override bool OwnsLocomotion => true;

    // Frames from the start of the visible action until its hit lands.
    private static int Startup(string a) => a switch
    {
        "StabAction"      => 11,
        "AirHammerAction" => 15,
        "PulseAction"     => 14,
        "AirWhipKick"     => 6,
        "GrabAction"      => 20,
        "BurstAction"     => 6,
        _                 => 5,
    };

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        var c = v.Self.Combat;
        if (c.GuardBroken || c.GuardOnCooldown(v.SelfFrame) || c.GrabbedActive) return 0f;
        if (c.GuardCharged) return 2.0f;   // a parry is already banked: go take the counter
        float dist = v.Dist;
        string a = _s.Action;

        if (_s.TargetAttacking && a != "GrabAction")
        {
            int remaining = Startup(a) - (_s.ActionAge + RiposteBot.ReactionFrames);
            float reach = a == "StabAction" ? 100f : a == "PulseAction" ? 80f : a == "AirHammerAction" ? 80f : 62f;
            if (dist <= reach && remaining >= -1 && remaining <= 7) return 1.9f;
            // Pulse ring keeps expanding for its whole active window.
            if (a == "PulseAction" && dist < 70f && _s.ActionAge + RiposteBot.ReactionFrames < 30) return 1.9f;
            // Slash chain: the next step of the combo is still to come.
            if (a.StartsWith("GroundSlash") && a != "GroundSlash3" && dist < 58f && _s.ActionAge < 6) return 1.7f;
        }

        // A dash coming at us: its slash is 3f, so the stance goes up for the arrival.
        if (_s.TargetDashing && !_s.Helpless)
        {
            var now = _s.Predicted(v);
            float closingD = _s.Closing(v);
            float d = Vector2.Distance(now, v.Me);
            if (closingD > 120f && d < 150f && (d - 46f) / closingD * 60f <= 6f) return 1.9f;
        }

        // The read: they are walking into their own swing range and they like to swing.
        if (!_s.TargetAttacking && !_s.Helpless && _s.TargetGrounded)
        {
            var now = _s.Predicted(v);
            float gapNow = MathF.Abs(now.X - v.Me.X);
            float closing = _s.Closing(v);
            float reach = _s.ThreatReach(v);
            if (closing > 20f && gapNow < reach + 6f && gapNow > reach - 14f && MathF.Abs(v.Dy) < 24f
                && _s.Model.Aggression > 0.6f)
                return 1.1f;
            // Stuck in my own tail with them on me: I can't swing, but I can block.
            bool myTail = v.Self.CurrentActionName == "RecoveryAction" && v.FramesSinceAttack < 40;
            if (myTail && gapNow < reach + 4f && (closing > 10f || gapNow < reach - 10f)
                && MathF.Abs(v.Dy) < 30f && !_s.TargetRecovering)
                return 1.6f;
        }
        return 0f;
    }

    public override void Begin(BotView v)
    {
        _t = 0; _sinceClick = 99;
        _phase = v.Self.Combat.GuardCharged ? Phase.Retaliate : Phase.Guard;
        _read = !_s.TargetAttacking;
        if (_read) ReadsTried++;
        // A read is held a short while; a reaction until the hit has had time to land.
        _hold = _read ? 12 : 16;
        _cooldown = _read ? 24 : 6;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        var c = v.Self.Combat;
        input.MouseWorldPosition = v.TargetPos;
        if (_phase == Phase.Guard)
        {
            if (c.GuardCharged) { Parries++; _phase = Phase.Retaliate; _t = 0; return Step(v, ref input); }
            input.Shift = true;
            _t++;
            if (_t > 3 && !c.GuardActive) return false;            // block resolved / broke
            // A read that the opponent answered with a swing: stay up for it.
            if (_read && _s.TargetAttacking && _t < 8) _hold = Math.Max(_hold, _t + 10);
            return _t < _hold;
        }

        // Retaliate: Shift off; get in reach and tap LMB (≤ 12 presses/s).
        if (!c.GuardCharged) { Counters++; return false; }
        float adx = MathF.Abs(v.Dx);
        if (adx > 40f) BotInput.Move(ref input, v.Toward);
        else if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        _sinceClick++;
        if (adx <= 54f && _sinceClick >= 5) { input.LeftClick = true; _sinceClick = 0; _s.NoteMyAttack(v, "slash"); }
        _t++;
        return _t < 40;
    }
}

// ── Punish: they whiffed (visible recovery pose after a swing) or they're flinching ──
// Walk/step in and run the slash chain while they can't answer.
public sealed class RipostePunish : BotManeuver
{
    private readonly RiposteSense _s;
    public RipostePunish(RiposteSense s) => _s = s;

    public float Reach = 42f;
    private int _t, _sinceClick, _clicks;
    public override int Cooldown => 6;
    public override bool OwnsLocomotion => true;

    // A grab's 20-frame wind-up holds nothing and has no armor: hit it.
    private bool Window => _s.TargetRecovering || _s.Helpless
        || (_s.Action == "GrabAction" && _s.ActionAge + RiposteBot.ReactionFrames < 17);

    public override float Score(BotView v)
    {
        if (!Window) return 0f;
        if (MathF.Abs(v.Dy) > 28f) return 0f;
        if (v.Self.Combat.BlocksAttack) return 0f;
        float adx = MathF.Abs(v.Dx);
        if (adx > 62f) return 0f;
        return 1.6f;
    }

    public override void Begin(BotView v) { _t = 0; _sinceClick = 99; _clicks = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++; _sinceClick++;
        float adx = MathF.Abs(v.Dx);
        var now = _s.Predicted(v);
        float gapNow = MathF.Abs(now.X - v.Me.X);
        if (gapNow > Reach - 8f || v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        // Chain: each tap after the previous step's hit (10–12 frames apart).
        if (gapNow <= Reach && _sinceClick >= (_clicks == 0 ? 0 : 11) && MathF.Abs(v.Dy) < 30f)
        {
            input.LeftClick = true;
            _sinceClick = 0;
            _clicks++;
            _s.NoteMyAttack(v, "slash");
        }
        if (_clicks >= 3 && _sinceClick > 2) return false;
        if (!Window && _clicks == 0 && _t > 4) return false;   // window closed before we got there
        if (_clicks > 0 && _sinceClick > 14) return false;     // chain dropped
        return _t < 45 && adx < 80f;
    }
}

// ── Dash in: close a mid gap in one burst and dash-slash on arrival ──────────────────
// Used to punish a long tail from range (their stab/hammer/pulse recovery, a flinch from
// our own stab), and to break a zoner's ranged wind-up (eruption/throw/beam charge).
public sealed class RiposteDashIn : BotManeuver
{
    private readonly RiposteSense _s;
    public RiposteDashIn(RiposteSense s) => _s = s;

    public float MinGap = 58f, MaxGap = 150f, SlashDist = 46f;
    private int _t;
    private bool _inDash, _clicked;
    public override int Cooldown => 75;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        if (gap < MinGap || gap > MaxGap || MathF.Abs(v.Dy) > gap * 0.5f) return 0f;
        if (!v.LineOfSight() || v.Self.Combat.BlocksAttack) return 0f;
        if (_s.TargetRanged && !_s.Helpless) return 1.7f;                    // break the wind-up
        bool longTail = _s.Action == "RecoveryAction" && _s.SinceMelee < 26
                        && (_s.LastMelee == "StabAction" || _s.LastMelee == "AirHammerAction"
                            || _s.LastMelee == "PulseAction" || _s.LastMelee.StartsWith("GroundSlash"));
        if (longTail && gap < 110f) return 1.5f;
        if (_s.Helpless && gap < 120f) return 1.4f;
        return 0f;
    }

    public override void Begin(BotView v) { _t = 0; _inDash = false; _clicked = false; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        bool dashing = v.Self.CurrentStateName == "DashState";
        if (dashing) _inDash = true;
        if (_t == 0) { input.Space = true; input.Shift = true; }
        else if (_t == 1) input.Shift = true;
        BotInput.Move(ref input, v.Toward);
        if (_t == 0) BotInput.Move(ref input, 0);   // a clean chord frame
        if (_inDash)
        {
            if (!dashing) return false;
            var now = _s.Predicted(v);
            if (!_clicked && Vector2.Distance(now, v.Me) <= SlashDist)
            {
                input.LeftClick = true; _clicked = true; _s.NoteMyAttack(v, "slash");
            }
        }
        else if (_t > 12) return false;
        _t++;
        return true;
    }
}

// ── Stab check: the long poke at the edge of their reach ─────────────────────────────
// The stab out-ranges every slash and is armored through its wind-up, so it is the
// answer to an opponent walking into our range, and the punish for one backing off.
public sealed class RiposteStab : BotManeuver
{
    private readonly RiposteSense _s;
    public RiposteStab(RiposteSense s) => _s = s;

    public float MinGap = 48f, MaxGap = 78f;
    private int _i, _cooldown = 50;
    public override int Cooldown => _cooldown;

    public override float Score(BotView v)
    {
        if (!v.Grounded || v.Self.Combat.BlocksAttack) return 0f;
        if (MathF.Abs(v.Dy) > 22f) return 0f;
        var now = _s.Predicted(v);
        float gapNow = MathF.Abs(now.X - v.Me.X);
        // The thrust lands ~11 frames from now; they keep moving meanwhile.
        float closing = _s.Closing(v);
        float atHit = gapNow - closing * (11f / 60f);
        if (atHit < MinGap - 6f || atHit > MaxGap) return 0f;
        // A fresh guard parries; one held for a while leaks and breaks.
        if (_s.Action == "GuardAction" && _s.ActionAge < 24) return 0f;
        float u;
        if (_s.TargetRecovering || _s.Helpless) u = 1.3f;
        else if (closing > 25f) u = 1.2f;                // check the approach
        else if (closing < -25f) u = 1.1f;               // chase a retreat
        else u = _s.Pressing(v) ? 1.0f : 0.5f;            // neutral poke
        // Learned: an opponent that parries or sidesteps the stab stops seeing it outside
        // the sure windows.
        float learned = 0.2f + 1.6f * _s.Model.StabSuccess;
        return (_s.TargetRecovering || _s.Helpless) ? u * MathF.Max(learned, 0.8f) : u * learned;
    }

    public override void Begin(BotView v)
    {
        _i = 0;
        _cooldown = v.Rng.Next(36, 60);
        _s.NoteMyAttack(v, "stab");
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        var dir = StabGesture.Aim(v);
        if (dir.Y > 0.25f || dir.Y < -0.4f) dir = Vector2.Normalize(new Vector2(MathF.Sign(dir.X == 0 ? v.Toward : dir.X), MathF.Max(-0.4f, MathF.Min(0.25f, dir.Y))));
        if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        else BotInput.Move(ref input, 0);
        bool done = StabGesture.Drive(v, ref input, _i, dir);
        _i++;
        return !done;
    }
}

// ── Poke slash: inside slash reach and they are not swinging ─────────────────────────
public sealed class RiposteSlash : BotManeuver
{
    private readonly RiposteSense _s;
    public RiposteSlash(RiposteSense s) => _s = s;

    public float Reach = 40f, VerticalReach = 28f;
    private int _cooldown = 20;
    public override int Cooldown => _cooldown;

    public override float Score(BotView v)
    {
        if (v.Self.Combat.BlocksAttack) return 0f;
        var now = _s.Predicted(v);
        if (MathF.Abs(now.X - v.Me.X) > Reach || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (_s.Action == "GuardAction") return _s.ActionAge > 24 ? 1.0f : 0.2f;
        return _s.TargetAttacking ? 0.6f : 1.0f;
    }

    public override void Begin(BotView v)
    {
        _cooldown = v.Rng.Next(16, 28);
        _s.NoteMyAttack(v, "slash");
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        input.LeftClick = true;
        if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        return false;
    }
}

// ── Hit-and-run / step out of a swing ────────────────────────────────────────────────
public sealed class RiposteRetreat : BotManeuver
{
    private readonly RiposteSense _s;
    public RiposteRetreat(RiposteSense s) => _s = s;

    private int _frames, _len, _blocked, _hop;
    private float _goal;
    public override int Cooldown => 6;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        float adx = MathF.Abs(v.Dx);
        if (MathF.Abs(v.Dy) > 30f) return 0f;
        if (_s.Helpless) return 0f;
        if (adx < 58f && v.FramesSinceAttack < 14) return 1.45f;
        if (_s.TargetAttacking && adx < _s.ThreatReach(v) + 10f) return 1.3f;
        if (adx < 22f) return 0.8f;
        return 0f;
    }

    public override void Begin(BotView v)
    {
        _len = v.Rng.Next(12, 22);
        _goal = _s.DesiredGap(v);
        _frames = 0; _blocked = 0; _hop = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        BotInput.Move(ref input, -v.Toward);
        bool wallBehind = v.Solid(v.Me + new Vector2(-v.Toward * (Chunk.TileSize * 1.5f), 0f));
        if (wallBehind && MathF.Abs(v.MyVel.X) < 8f && _frames > 2) _blocked++; else _blocked = 0;
        if (_hop > 0) { input.Space = true; _hop--; }
        else if (_blocked >= 4 && v.Grounded) { input.Space = true; _hop = 12; _blocked = 0; }
        ++_frames;
        // Walk out until clear of their reach (bounded), not a fixed few pixels.
        return _frames < _len;
    }
}

// Wraps a stock maneuver behind an extra gate (the shared files stay untouched).
public sealed class RiposteGated : BotManeuver
{
    private readonly BotManeuver _inner;
    private readonly Func<BotView, bool> _gate;
    public RiposteGated(BotManeuver inner, Func<BotView, bool> gate) { _inner = inner; _gate = gate; }
    public override string Name => _inner.Name;
    public override int Cooldown => _inner.Cooldown;
    public override bool OwnsLocomotion => _inner.OwnsLocomotion;
    public override float Score(BotView v) => _gate(v) ? _inner.Score(v) : 0f;
    public override void Begin(BotView v) => _inner.Begin(v);
    public override bool Step(BotView v, ref PlayerInput input) => _inner.Step(v, ref input);
    public override void Abort(BotView v, ref PlayerInput input) => _inner.Abort(v, ref input);
}
