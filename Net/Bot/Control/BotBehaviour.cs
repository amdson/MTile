using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// The uniform bot behaviour format (Plans/BOT_BEHAVIOR_ARCHITECTURE.md). A behaviour
// reads the game state through BotView, BETS on the HP outcome of running, and when the
// controller grants it a resource it drives that resource. It never looks at other
// behaviours; everything shared goes through BotController.
//
// Resources: Move (Left/Right/Space/Down) and Act (LMB/RMB/Shift/cursor/number keys).
// Categories are defined by which resource a behaviour holds and for how long.

public enum Category
{
    Positioning,   // a Stance: holds Move softly, long horizon, never commits
    Reflex,        // holds Move exclusively, short; preempts a stance AND a committed action
    Action,        // holds Act; plus a Move claim (None / Forbid / Exclusive)
    Constraint,    // holds nothing; masks the final input
}

[Flags]
public enum MoveKeys : byte
{
    None  = 0,
    Left  = 1,
    Right = 2,
    Space = 4,
    Down  = 8,
    All   = Left | Right | Space | Down,
}

// What a behaviour needs from the Move resource while it runs.
//   None       - don't touch Move; the stance keeps driving.
//   Exclusive  - I drive Move; the stance is suspended.
//   Forbid(k)  - I don't drive Move, but keys k must not be pressed. Forbid(All) is
//                "hold still": the stance is suspended and Move is left empty.
public readonly struct MoveClaim
{
    public enum Mode : byte { None, Exclusive, Forbid }

    public readonly Mode     Kind;
    public readonly MoveKeys Forbidden;

    private MoveClaim(Mode kind, MoveKeys forbidden) { Kind = kind; Forbidden = forbidden; }

    public static readonly MoveClaim None      = new(Mode.None, MoveKeys.None);
    public static readonly MoveClaim Exclusive = new(Mode.Exclusive, MoveKeys.None);
    public static readonly MoveClaim Still     = new(Mode.Forbid, MoveKeys.All);
    public static MoveClaim Forbid(MoveKeys keys) => keys == MoveKeys.None ? None : new(Mode.Forbid, keys);

    // True when the stance must stop driving while this claim is held.
    public bool SuspendsStance => Kind == Mode.Exclusive || (Kind == Mode.Forbid && (Forbidden & (MoveKeys.Left | MoveKeys.Right)) == (MoveKeys.Left | MoveKeys.Right));

    public void Mask(ref PlayerInput input)
    {
        if (Kind != Mode.Forbid) return;
        if ((Forbidden & MoveKeys.Left)  != 0) input.Left  = false;
        if ((Forbidden & MoveKeys.Right) != 0) input.Right = false;
        if ((Forbidden & MoveKeys.Space) != 0) input.Space = false;
        if ((Forbidden & MoveKeys.Down)  != 0) input.Down  = false;
    }

    public override string ToString() => Kind == Mode.Forbid ? $"Forbid({Forbidden})" : Kind.ToString();
}

// A behaviour's prediction of the HP outcome of running it now: HP it expects to deal
// and to take over the next Horizon frames. Horizon <= 0 means not applicable this frame
// (today's Score <= 0). Bets are in HP so they are comparable across behaviours: a stock
// in-range slash is about (0.5, 0.1, 30). The controller's BetLedger adds a learned
// per-(behaviour, situation, channel) offset; the corrected bet is what selection uses.
public readonly struct Bet
{
    public readonly float Dealt, Taken;
    public readonly int   Horizon;

    public Bet(float dealt, float taken, int horizon) { Dealt = dealt; Taken = taken; Horizon = horizon; }

    public static readonly Bet Pass = new(0f, 0f, 0);
    public bool Applicable => Horizon > 0;

    public override string ToString() => Applicable ? $"({Dealt:F2}, {Taken:F2}, {Horizon}f)" : "pass";
}

public abstract class BotBehaviour
{
    public virtual string   Name => GetType().Name;
    public abstract Category Category { get; }

    // What this behaviour needs from Move while committed. Stances and constraints
    // ignore it (a stance always drives Move softly; a constraint never holds anything).
    public virtual MoveClaim Move => MoveClaim.None;

    // Frames after this behaviour releases before it may bet again.
    public virtual int Cooldown => 30;

    // Every frame while not committed. Must not mutate state Step relies on; Begin does setup.
    public abstract Bet Bet(BotView v);

    public virtual void Begin(BotView v) { }

    // Committed frames (Reflex / Action): write this frame's input for the held
    // resources. Return false when finished; whatever was written that frame is still
    // emitted. Stances and constraints do not use Step.
    public virtual bool Step(BotView v, ref PlayerInput input) => false;

    // Called when the controller takes the resource away early (preempted, or self got
    // hit). `input` is that frame's input so far; write a clean-up press if the behaviour
    // left sticky state behind (re-select the block picker, release a held key).
    public virtual void Aborted(BotView v, ref PlayerInput input) { }

    // Per-behaviour bookkeeping owned by the controller.
    internal int CooldownLeft;

    public override string ToString() => Name;
}

// Positioning. A stance returns a world-space target position each frame; the
// controller's MoveExecutor turns it into Move inputs (walk, stuck hop, air steer,
// route follower when the straight line needs a climb or a detour). Trade-offs between
// goals ("stay close" but "don't get cornered") are made INSIDE Target; which stance
// runs is the controller's selection. Stances never commit and are suspended, not
// aborted, when a reflex or a Move-holding action takes Move.
public abstract class Stance : BotBehaviour
{
    public sealed override Category  Category => Category.Positioning;
    public sealed override MoveClaim Move     => MoveClaim.None;
    public override int Cooldown => 0;

    public abstract Vector2 Target(BotView v);

    // Horizontal tolerance: within this of Target.X the executor stops walking.
    public virtual float Tolerance => 6f;

    // Whether the executor may hop (stuck, or the target is well above). Off for stances
    // that must stay grounded.
    public virtual bool MayJump => true;

    public virtual void Suspended(BotView v) { }
    public virtual void Resumed(BotView v) { }
}

// A constraint masks the frame's finished input. All constraints run, every frame, last.
public abstract class Constraint : BotBehaviour
{
    public sealed override Category Category => Category.Constraint;
    public sealed override Bet Bet(BotView v) => Net.Bet.Pass;

    // Edit `input` in place: clear a key, redirect a step, add a hop. Return true if the
    // constraint changed anything (for the trace).
    public abstract bool Mask(BotView v, ref PlayerInput input);
}

// The shared situation bucket for learned offsets. Behaviours may NOT define their own
// bucket: that would be a knob the learner cannot see through.
//   gap band (4) x perceived target state (3) x self grounded (2) = 24 buckets.
public static class Situation
{
    public const int Count = 24;

    public static int Bucket(BotView v)
    {
        float gap = MathF.Abs(v.Dx);
        int band  = gap < 40f ? 0 : gap < 85f ? 1 : gap < 160f ? 2 : 3;
        int state = IsAttack(v.TargetAction) ? 0 : v.TargetAction == "RecoveryAction" ? 1 : 2;
        int air   = v.Grounded ? 0 : 1;
        return (band * 3 + state) * 2 + air;
    }

    public static string Describe(int bucket)
    {
        if (bucket < 0) return "all";
        int air = bucket % 2; bucket /= 2;
        int state = bucket % 3; int band = bucket / 3;
        return $"{new[] { "<40", "40-85", "85-160", ">160" }[band]}/{new[] { "atk", "rec", "idle" }[state]}/{(air == 0 ? "gnd" : "air")}";
    }

    public static bool IsAttack(string a)
        => a != null && (a.Contains("Slash") || a is "StabAction" or "AirWhipKick" or "AirHammerAction" or "PulseAction"
                                                 or "BurstAction" or "GrabAction" or "GuardRetaliateAction");
}
