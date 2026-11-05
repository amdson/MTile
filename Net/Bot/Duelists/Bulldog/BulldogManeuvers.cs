using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Bulldog's close-range kit. Each is a raw-input script, like the stock maneuvers; the
// opponent facts they read are BotView's stale ones or BulldogSense's (also stale).
internal static class BulldogKit
{
    // Our own recovery countdown is (nearly) up and nothing holds us: an opener pressed
    // now fires. A press up to a few frames early buffers onto the end of recovery.
    public static bool CanAttack(BotView v, int early = 3)
    {
        var c = v.Self.Combat;
        if (c.HitstunActive || c.StunActive || c.GrabbedActive) return false;
        var cond = v.Self.Abilities.Condition;
        if (cond.RecoveryActive && cond.RecoveryExpireFrame - v.SelfFrame > early) return false;
        string a = v.Self.CurrentActionName;
        return a is "NullAction" or "RecoveryAction" or "None";
    }

    public static bool Guarding(BotView v) => v.TargetAction == "GuardAction";
}

// The slash string: S1 → S2 → S3 on the ground (AirSlash1 → 2 in the air). One tap
// starts it; after that a tap every TapGap frames keeps a Click in the 20-frame intent
// buffer, so each combo step fires the moment its gap opens (S2 ~17 f, S3 ~32 f after
// S1). Stopping early costs the same 36 f tail as finishing, so once a swing is out we
// finish the string unless it plainly whiffed and the opponent left reach.
public sealed class BulldogString : BotManeuver
{
    public float Reach = 40f, VerticalReach = 28f;
    public float FollowReach = 56f;
    public int   Tap2 = 13, Tap3 = 28, MaxFrames = 60;

    private readonly BulldogSense _s;
    private int _t;
    private float _hp0;
    private bool _sawFinisher;

    public BulldogString(BulldogSense s) => _s = s;
    public override int Cooldown => 4;

    public override float Score(BotView v)
    {
        if (MathF.Abs(v.Dx) > Reach || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!BulldogKit.CanAttack(v)) return 0f;
        if (BulldogKit.Guarding(v)) return 0.3f;
        return _s.TargetHelpless ? 1.3f : 1.0f;
    }

    // Bet (M4): string = 3 taps (0/13/28) + the 36 f tail ~ 55 f; 1.5 HP when it lands.
    //   flinching target: S1 lands, S2/S3 drop out of halved hitstun ext. ~0.9; tail in face 0.3;
    //   neutral: ~45% vs a guard-happy opponent (0.7), trading S1s ~0.25;
    //   into a guard: the hit-confirmed opener only, 0.2 / 0.1 over ~30 f.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        if (s >= 1.2f) return new Bet(0.9f, 0.3f, 55);
        if (s >= 0.9f) return new Bet(0.7f, 0.25f, 55);
        return new Bet(0.2f, 0.1f, 30);
    }

    public override void Begin(BotView v) { _t = 0; _hp0 = _s.TargetHp; _sawFinisher = false; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        string a = v.Self.CurrentActionName;
        if (a is "GroundSlash3" or "AirSlash2") _sawFinisher = true;
        if (_sawFinisher) return false;
        if (_t > MaxFrames) return false;

        bool connected = _s.TargetHp < _hp0;
        // Got parried / thrown off: the string is over.
        if (v.Self.Combat.HitstunActive || v.Self.Combat.StunActive || v.Self.Combat.GrabbedActive) return false;

        // Taps: the opener, then one just before each combo gap opens (S2 ~17 f, S3 ~32 f),
        // so the press is still in the intent buffer when the step becomes legal. Each
        // follow-up is hit-confirmed: a whiffed opener into a guard is not continued.
        bool tap = _t == 0 || _t == Tap2 || _t == Tap3;
        if (tap && _t > 0)
        {
            bool inReach = MathF.Abs(v.Dx) <= FollowReach && MathF.Abs(v.Dy) <= 40f;
            if (!connected && (BulldogKit.Guarding(v) || !inReach)) return false;
            if (!inReach && !_s.TargetHelpless) return false;
        }
        if (_t > Tap3) return _t < Tap3 + 8;
        if (tap && !v.Previous.LeftClick)
        {
            input.LeftClick = true;
            if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        }
        // Stay in their face through the string (S1/S2 barely push).
        if (MathF.Abs(v.Dx) > 14f) BotInput.Move(ref input, v.Toward);
        _t++;
        return true;
    }
}

// Grab and throw (stock GrabManeuver's gesture), as the answer to a guard and as one
// branch of the close-range mix. Throw into the nearest wall, else into the floor.
// "Have I got them" is our own GrabAction's victim slot, not the opponent's flags.
public sealed class BulldogGrab : BotManeuver
{
    public float Reach = 34f, VerticalReach = 24f, WallSearch = 140f;
    public double MixShare = 0.0;

    private const int   FaceFrames = 4, WindupFrames = 22, CarryFrames = 8, SwipeFrames = 5;
    private const float SwipeStep  = 14f;

    private enum Phase { Face, Press, Carry, Swipe }
    private readonly BulldogSense _s;
    private Phase _phase;
    private int _t;
    private Vector2 _dir, _swipeFrom;

    public BulldogGrab(BulldogSense s) => _s = s;
    public override int Cooldown => 60;

    private static bool Holding(BotView v)
        => v.Self.CurrentActionName == "GrabAction" && v.Self.CurrentActionVars.GrabVictim != EntityId.None;

    public override float Score(BotView v)
    {
        if (MathF.Abs(v.Dx) > Reach || MathF.Abs(v.Dy) > VerticalReach) return 0f;
        if (!BulldogKit.CanAttack(v)) return 0f;
        if (BulldogKit.Guarding(v)) return 1.6f;
        if (_s.TargetHelpless) return 0f;            // a flinching target eats the string instead
        return _s.AllMixArms || _s.Mix < MixShare ? 1.05f : 0f;
    }

    // Bet (M4): throw into a wall / floor ~1 HP; vs a planted guard the 20 f startup lands ~80%
    // (0.8, ~0 taken); as a blind mix-up ~60% and a whiffed grab eats a punish (0.13).
    // 4 face + 22 windup + 8 carry + 5 swipe + 18 end lag ~ 60 f.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return s >= 1.5f ? new Bet(0.8f, 0.05f, 60) : new Bet(0.6f, 0.13f, 60);
    }

    public override void Begin(BotView v) { _phase = Phase.Face; _t = 0; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_phase == Phase.Face)
        {
            if (v.Facing != v.Toward && _t <= FaceFrames) { BotInput.Move(ref input, v.Toward); return true; }
            _phase = Phase.Press; _t = 1;
        }
        switch (_phase)
        {
            case Phase.Press:
                input.Shift = true; input.LeftClick = true;
                if (Holding(v)) { _dir = ChooseThrow(v); _phase = Phase.Carry; _t = 0; return true; }
                return _t <= WindupFrames + 4;
            case Phase.Carry:
                input.Shift = true; input.LeftClick = true;
                input.MouseWorldPosition = v.Me + _dir * 30f;
                if (_t >= CarryFrames) { _phase = Phase.Swipe; _t = 0; _swipeFrom = input.MouseWorldPosition; }
                return Holding(v);
            default:
                input.MouseWorldPosition = _swipeFrom + _dir * (SwipeStep * _t);
                if (_t >= SwipeFrames) return false;
                input.Shift = true; input.LeftClick = true;
                return true;
        }
    }

    // Throw from where we are holding them (our own body: the victim is in our hands).
    private Vector2 ChooseThrow(BotView v)
    {
        var p = v.Me;
        float best = WallSearch + 1f; int side = 0;
        for (int s = -1; s <= 1; s += 2)
        {
            var hit = v.FirstSolidAlong(p, p + new Vector2(s * WallSearch, 0f));
            if (hit.HasValue)
            {
                float d = MathF.Abs(hit.Value.X - p.X);
                if (d < best) { best = d; side = s; }
            }
        }
        if (side != 0) return Vector2.Normalize(new Vector2(side, -0.15f));
        if (v.FirstSolidAlong(p, p + new Vector2(0f, 3 * Chunk.TileSize)).HasValue) return new Vector2(0f, 1f);
        return Vector2.Normalize(new Vector2(v.Toward, -0.15f));
    }
}

// Stab: the launcher. Thrown from just outside slash reach (its lunge closes the gap) and
// as the finisher on a low-HP opponent.
public sealed class BulldogStab : BotManeuver
{
    public float MinGap = 36f, MaxGap = 80f, VerticalReach = 26f, FinisherHp = 1.0f;
    public double MixShare = 0.6;

    private readonly BulldogSense _s;
    private int _i;

    public BulldogStab(BulldogSense s) => _s = s;
    public override int Cooldown => 30;

    public override float Score(BotView v)
    {
        float adx = MathF.Abs(v.Dx);
        if (MathF.Abs(v.Dy) > VerticalReach || adx > MaxGap) return 0f;
        if (!v.Grounded || !BulldogKit.CanAttack(v)) return 0f;
        bool finisher = _s.TargetHp <= FinisherHp;
        if (adx >= MinGap) return _s.AllMixArms || _s.Mix < MixShare ? 1.2f : 0f;
        return finisher && _s.TargetHelpless ? 1.4f : 0f;
    }

    // Bet (M4): stab = launcher, 0.75 HP + crush into terrain; 14 f hold + 36 f stab ~ 50 f.
    //   finisher on a flinching <=1 HP target: (0.75, 0.09); lunge from 36-80 px ~70%: (0.72, 0.15).
    // Values 0.66 / 0.57 top their Score ties (1.4 / 1.2), as list order did.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return s >= 1.3f ? new Bet(0.75f, 0.09f, 50) : new Bet(0.72f, 0.15f, 50);
    }

    public override void Begin(BotView v) => _i = 0;

    public override bool Step(BotView v, ref PlayerInput input)
    {
        var dir = StabGesture.Aim(v);
        if (dir.Y > 0.3f) dir = Vector2.Normalize(new Vector2(dir.X, 0.3f));
        if (v.Facing != v.Toward) BotInput.Move(ref input, v.Toward);
        bool done = StabGesture.Drive(v, ref input, _i, dir);
        _i++;
        return !done;
    }
}

// Cross-up: hop over the opponent and whip-kick back at them (stock AirWhipKick's
// gesture), one branch of the mix — it beats a guard facing the old side.
public sealed class BulldogCrossUp : BotManeuver
{
    public float LaunchRangeX = 50f, KickReachX = 44f;
    public int   JumpHoldFrames = 30, MaxFrames = 80;
    public double MixLo = 0.22, MixHi = 0.36;

    private readonly BulldogSense _s;
    private int _t, _gi, _facing;
    private bool _inGesture;

    public BulldogCrossUp(BulldogSense s) => _s = s;
    public override int Cooldown => 60;
    public override bool OwnsLocomotion => true;

    public override float Score(BotView v)
    {
        if (!BulldogKit.CanAttack(v)) return 0f;
        if (!v.Grounded)
            return v.Toward != v.Facing && MathF.Abs(v.Dx) <= KickReachX && MathF.Abs(v.Dy) <= 40f ? 1.2f : 0f;
        if (_s.TargetHelpless) return 0f;
        bool roll = _s.AllMixArms || (_s.Mix >= MixLo && _s.Mix < MixHi);
        return roll && MathF.Abs(v.Dx) <= LaunchRangeX && MathF.Abs(v.Dx) >= 16f && MathF.Abs(v.Dy) <= 20f ? 1.05f : 0f;
    }

    // Bet (M4): back whip-kick 0.5 + launch (~0.75) at a turned opponent ~80% -> 0.6.
    //   already airborne behind them: ~30 f kick, ~0 taken (0.56); from the ground: hop ~30 f + kick,
    //   a read hop eats ~0.13, 60 f.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return s >= 1.1f ? new Bet(0.6f, 0.04f, 30) : new Bet(0.6f, 0.13f, 60);
    }

    public override void Begin(BotView v) { _t = 0; _gi = 0; _inGesture = false; _facing = v.Facing; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        if (_t > MaxFrames) return false;
        input.MouseWorldPosition = v.TargetPos;
        if (!_inGesture)
        {
            if (v.Grounded)
            {
                if (_t <= JumpHoldFrames) input.Space = true;
                BotInput.Move(ref input, v.Toward);
                return _t <= 8;
            }
            BotInput.Move(ref input, v.Toward);
            float lead = MathF.Max(10f, MathF.Abs(v.MyVel.X) * StabGesture.HoldFrames * Simulation.FixedDt);
            if (v.Toward != v.Facing || MathF.Abs(v.Dx) > lead) return true;
            _inGesture = true; _gi = 0; _facing = v.Facing;
        }
        BotInput.Move(ref input, v.Toward);
        float back = -_facing;
        var dir = Vector2.Normalize(new Vector2(back, Math.Clamp(v.Dy / 60f, -0.3f, 0.3f)));
        bool done = StabGesture.Drive(v, ref input, _gi, dir);
        _gi++;
        return !done;
    }
}

// Low poke: crouch, then tap — CrouchSlash has the longest reach of the normals and only
// a 9 f tail, so it is the safe branch of the mix (and the answer to a low target).
public sealed class BulldogLowPoke : BotManeuver
{
    public float Reach = 46f;
    // CrouchSlash's arc is 1.45 x the base radius: it connects out to ~70 px centre to
    // centre, past every standing normal — the long poke at the edge of their reach.
    public float PokeMin = 50f, PokeMax = 64f;
    public float PokeUtility = 1.15f;
    public double MixLo = 0.36, MixHi = 0.52;
    private const int CrouchFrames = 3;

    private readonly BulldogSense _s;
    private int _t;

    public BulldogLowPoke(BulldogSense s) => _s = s;
    public override int Cooldown => 30;

    public override float Score(BotView v)
    {
        if (!v.Grounded || !BulldogKit.CanAttack(v)) return 0f;
        float ax = MathF.Abs(v.Dx);
        if (ax >= PokeMin && ax <= PokeMax && MathF.Abs(v.Dy) <= 20f) return PokeUtility;
        if (ax > Reach) return 0f;
        if (v.Dy > 6f && v.Dy < 30f) return 1.1f;
        if (MathF.Abs(v.Dy) > 20f || _s.TargetHelpless) return 0f;
        return _s.AllMixArms || (_s.Mix >= MixLo && _s.Mix < MixHi) ? 1.05f : 0f;
    }

    // Bet (M4): CrouchSlash 0.5, 3 f crouch + 14 f slash (9 f tail) ~ 20 f.
    //   long poke at 50-64 px, past every standing normal: ~90%, ~0 taken (0.45);
    //   a low target only it reaches: ~all (0.5); mix branch: 0.5, short tail 0.03.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        if (s >= 1.13f) return new Bet(0.45f, 0f, 20);
        if (s >= 1.08f) return new Bet(0.5f, 0f, 20);
        return new Bet(0.5f, 0.03f, 20);
    }

    public override void Begin(BotView v) => _t = 0;

    public override bool Step(BotView v, ref PlayerInput input)
    {
        input.Down = true;
        input.Left = input.Right = false;
        if (_t == CrouchFrames) input.LeftClick = true;
        _t++;
        return _t <= CrouchFrames + 1;
    }
}

// Combo breaker. Hit by an opener (S1 / AirSlash1, as we saw the opponent swing it),
// the follow-up arrives on the combo rhythm: ~16 frames after the first hit. Guard may
// come up with up to 0.2 s of hitstun left, so raising the stance at hit+BreakAt puts
// the 7-frame perfect window over the second hit, and the parry arms GuardRetaliate.
// Keyed on our OWN hit clock plus the stale view of their action — a rhythm read, not
// a reaction to the second swing.
public sealed class BulldogBreaker : BotManeuver
{
    public int BreakAt = 10, HoldFrames = 12, RetaliateFrames = 10;
    public float Range = 70f;

    private int _t;
    private bool _ret;

    public override int Cooldown => 30;
    public override bool OwnsLocomotion => true;
    public override MoveClaim Move => MoveClaim.Still;   // parry-timing guard raise; writes no Left/Right/Space/Down

    private static bool Opener(string a) => a is "GroundSlash1" or "GroundSlash2" or "AirSlash1";

    public override float Score(BotView v)
    {
        var c = v.Self.Combat;
        if (!c.HitstunActive || c.StunActive || c.GrabbedActive || c.GuardBroken) return 0f;
        if (v.FramesSinceHit != BreakAt && v.FramesSinceHit != BreakAt + 1) return 0f;
        if (c.GuardOnCooldown(v.SelfFrame)) return 0f;
        if (!Opener(v.TargetAction) || v.Dist > Range) return 0f;
        return 5f;
    }

    // Bet (M4): parry the combo's 2nd hit -> retaliate 0.5 ~80% (0.4); skips the rest of a
    // 1.5 HP string (~1.1 avoided); 12 f hold + 10 retaliate + slack. Second only to EscapeGrab.
    public override Bet Bet(BotView v)
    {
        float s = Score(v);
        if (s <= 0f) return Net.Bet.Pass;
        return new Bet(0.4f, -1.1f, 25);
    }

    public override void Begin(BotView v) { _t = 0; _ret = false; }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        var c = v.Self.Combat;
        if (!_ret && c.GuardCharged) { _ret = true; _t = 0; }
        if (_ret)
        {
            if (!c.GuardCharged) return false;
            input.LeftClick = _t % 5 == 0;
            _t++;
            return _t < RetaliateFrames;
        }
        input.Shift = true;
        _t++;
        return _t < HoldFrames;
    }
}
