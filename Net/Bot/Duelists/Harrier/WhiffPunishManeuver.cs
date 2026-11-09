using System;
using System.Collections.Generic;

namespace MTile.Net;

// The hit-and-run counter (Plans/PLAYER_AGENTS.md §6): dance at the edge of the target's
// slash reach to bait a swing, and when a swing is seen that didn't land, dash in and
// DashSlash the recovery.
//
// Why it works (MTile.Tests/Sim/ZzzHitAndRunProbe): a dropped Slash 1 can't be followed
// by Slash 2 until ~17 frames after the press (S1's 12 frames + ComboGapFraction), and
// Slash 2 then needs 4 more to come out. A dash slash chorded on seeing the swing
// (6–8 frames late) from 52–90 px lands at t+10..t+21 — before Slash 2 at the short end,
// trading into a clank at the long end. The runner's real answer is to stop and guard
// on seeing the dash, which a walk-back can't be doing.
//
// Phases: Bait (step in to BaitIn, back out to BaitOut, repeat — the target, perceiving
// us stale, swings at where we were) → Punish (the chord, then the click inside
// ClickDist while DashState runs). Everything about the target is BotView's delayed
// fields; a swing that hit us aborts the maneuver through the brain.
public sealed class WhiffPunishManeuver : BotManeuver
{
    // The bait dances at the edge of STAB range: a stab reaches ~80 px centre to centre
    // (48 px blade + ~20 px of wind-up lunge + the body), and StabManeuver throws it at a
    // perceived gap <= 84. Inside that band the bait just eats stabs.
    public float BaitIn    = 80f;     // perceived gap to step in to
    public float BaitOut   = 95f;     // ...and back out to
    public float MinGap    = 36f;     // closer than this, ordinary melee owns the exchange
    public float MaxGap    = 120f;    // beyond the dash's reach to punish
    public float ClickDist = 46f;     // perceived body-to-body distance for the DashSlash press
    public int   BaitFrames = 150;    // give up baiting after this long
    public float Utility   = 0.9f;    // below an in-reach attack (1.0)

    // Diagnostics: punishes thrown, and the target HP they took (credited while dashing).
    public int   Punishes;
    public float PunishDamage;
    public int   Baits, BaitHits, PunishHit, PunishGotHit, PunishNoClick, PunishNoDash;
    public readonly System.Collections.Generic.Dictionary<string, int> HitBy = new();
    public readonly int[] TriggerGap = new int[16];   // perceived gap at trigger, 10 px buckets

    private enum Phase { Bait, Punish }
    private Phase _phase;
    private int   _t, _dir;
    private bool  _inDash, _clicked;
    private string _lastSeen = "";
    private int    _age;
    private float _hpAtPunish;

    public override int  Cooldown       => 20;

    public override void Abort(BotView v, ref PlayerInput input)
    {
        if (_phase == Phase.Bait) BaitHits++; else PunishGotHit++;
        // Diagnostics only (live read, never used to decide): what hit us.
        string k = (_phase == Phase.Bait ? "bait:" : "punish:") + v.Target.CurrentActionName;
        HitBy[k] = HitBy.GetValueOrDefault(k) + 1;
    }
    public override bool OwnsLocomotion => true;

    // Real frames from a swing's start to the end of its active window (FrameData
    // Startup + Active); past this, a swing that hasn't hit us has whiffed. Slash 1/2 are
    // capped to short gaps: their chain step is back ~17 frames in, so the window closes
    // fast. A stab (or Slash 3) ends in a long tail, so any reachable gap pays.
    private static int WhiffAt(string a) => a switch
    {
        "GroundSlash1" => 8, "GroundSlash2" => 7, "GroundSlash3" => 12,
        "CrouchSlash"  => 10, "StabAction" => 18, _ => -1,
    };
    private static bool LongTail(string a) => a is "GroundSlash3" or "StabAction";
    public int   ReactFrames = 6;     // the brain's perception delay (BulldogBot.ReactionFrames)
    public float ShortTailGap = 75f;  // farthest gap worth a Slash 1/2 punish

    // An opposing spike anywhere on the floor/body band between x0 and x1 (terrain is
    // on screen: allowed live).
    private static bool SpikesBetween(BotView v, float x0, float x1)
    {
        var ch = v.Sim.Chunks;
        int a = BotView.Cell(new Microsoft.Xna.Framework.Vector2(MathF.Min(x0, x1), v.Me.Y)).X;
        int b = BotView.Cell(new Microsoft.Xna.Framework.Vector2(MathF.Max(x0, x1), v.Me.Y)).X;
        int y = BotView.Cell(v.Me).Y;
        for (int gx = a; gx <= b; gx++)
        for (int gy = y - 2; gy <= y + 2; gy++)
            if (ch.GetCellState(gx, gy) == TileState.Spike && SpikeContactSystem.Opposes(ch.GetCellTeam(gx, gy), v.Self.Team)) return true;
        return false;
    }

    public override float Score(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        if (!v.Grounded || !v.TargetGrounded || v.TargetHelpless) return 0f;
        if (gap < MinGap || gap > MaxGap || MathF.Abs(v.Dy) > 20f) return 0f;
        if (!v.LineOfSight()) return 0f;
        return Utility;
    }

    // Bet (M4): bait at stab range, DashSlash a seen whiff: 0.5 HP, ~1 punish per bait lands
    // (0.5); the bait eats a stab now and then (0.1); bait ~60 f + dash/cut ~30 f. Value 0.40,
    // just under an in-range string (0.45), as Score 0.9 sat under 1.0.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.5f, 0.1f, 90);
    }

    public override void Begin(BotView v)
    {
        _phase = Phase.Bait; _t = 0; _dir = 1; Baits++;
        _inDash = false; _clicked = false;
        _lastSeen = v.TargetAction; _age = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _age = v.TargetAction == _lastSeen ? _age + 1 : 0;
        _lastSeen = v.TargetAction;
        int whiff = WhiffAt(v.TargetAction);
        // Seen `_age` frames into it, it is really `_age + ReactFrames` in.
        bool whiffed = whiff > 0 && _age + ReactFrames == whiff + 1;

        if (_phase == Phase.Bait)
        {
            float gap = MathF.Abs(v.Dx);
            if (whiffed && gap > MinGap && v.Grounded
                && (LongTail(v.TargetAction) || gap <= ShortTailGap)
                && !SpikesBetween(v, v.Me.X, v.TargetPos.X))
            {
                _phase = Phase.Punish; _t = 0; Punishes++; _hpAtPunish = v.TargetHealth;
                TriggerGap[Math.Min(15, (int)(gap / 10f))]++;
            }
            else
            {
                if (gap < MinGap || gap > MaxGap + 20f || ++_t > BaitFrames) return false;
                if (_dir > 0 && (gap <= BaitIn || SpikesBetween(v, v.Me.X, v.Me.X + v.Toward * 2f * Chunk.TileSize))) _dir = -1;
                else if (_dir < 0 && gap >= BaitOut) _dir = 1;
                BotInput.Move(ref input, _dir * v.Toward);
                return true;
            }
        }

        // Punish: chord on frame 0, hold Shift on frame 1 (DashManeuver's shape).
        bool dashing = v.Self.CurrentStateName == "DashState";
        if (dashing) _inDash = true;
        if (_t == 0) { input.Space = true; input.Shift = true; }
        else if (_t == 1) input.Shift = true;
        if (_inDash)
        {
            if (!dashing)
            {
                float dmg = MathF.Max(0f, _hpAtPunish - v.TargetHealth);
                PunishDamage += dmg;
                if (dmg > 0f) PunishHit++; else if (!_clicked) PunishNoClick++;
                return false;
            }
            BotInput.Move(ref input, v.Toward);
            if (!_clicked && v.Dist <= ClickDist) { input.LeftClick = true; _clicked = true; }
        }
        else if (_t > 12) { PunishNoDash++; return false; }
        _t++;
        return true;
    }
}
