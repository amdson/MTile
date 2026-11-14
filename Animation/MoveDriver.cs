using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// ─── The move-driver API ────────────────────────────────────────────────────────────────
//
// One driver per MOVEMENT SITUATION (not per clip — the mapping is many-to-many: Parkour
// and LedgePull both played one shared clip; grounded locomotion fans out over five clips). A driver
// owns all the per-move animation policy that used to be scattered across SelectClip /
// IsPhaseDriven / ResolveMovementOverlays / ResolveMovementPins:
//
//   Matches     — is this the active situation? First match in registry order wins. Judged on
//                 the sample plus AnimHistory, the core's move-agnostic memory of earlier
//                 frames (how long the body has been down, last frame's sample) — so a
//                 situation defined by an EDGE, like a touchdown, is a driver like any other.
//   Select      — which clip to play, how its sample time is produced (TimeMode), and
//                 optionally where to start it on entry (StartT; -1 = default). Re-run
//                 every frame, so intra-move switches (Walk→Run, Crouch→DuckUnder) stay
//                 per-frame reactive; StartT is consulted only on the frame the clip CHANGES.
//   Contribute  — this frame's contributions to the solve inputs: overlay requests, pins,
//                 and (future) whole ISolveConstraint blocks. Everything contributed is
//                 FROZEN before the solve runs, like every other solve input.
//
// The animator core keeps everything move-agnostic: phase advance + the cadence/static
// solves, the overlay compositor, the smoothing/emit path, and the Action overlay (slot 0),
// which is orthogonal to movement and stays outside the driver system.

// How a clip's sample time t ∈ [0,1] is produced each frame.
public enum ClipTimeMode
{
    CadencePhase,   // the locomotion phase, advanced by the cadence solve (or the legacy rate)
    IdleBob,        // the phase, advanced at the fixed idle-breathing rate
    Clock,          // normalized elapsed ClipTime (one-shots; held at the end)
    Progress,       // the movement's spatial progress (sample.MovementProgress), clamped to [0,1]
    Hold,           // the phase, FROZEN — a held pose from a cycle clip (pre-run airborne).
                    // No cadence, no contacts; the phase resumes advancing when the driver
                    // switches the mode back (same clip ⇒ no entry reset, so it's seamless).
}

public struct ClipChoice
{
    public AnimClip     Clip;
    public ClipTimeMode Time;
    // Normalized start time applied on the frame the clip changes: phase for phase modes,
    // fraction of the clip's Duration for Clock. -1 (default) keeps the existing convention
    // (ClipTime resets to 0; the locomotion phase persists across switches).
    public float        StartT;
    // On the frame the clip changes, initialize the phase to the clip cycle's BEST MATCH
    // against the previously emitted pose (the core scans candidate phases and picks the
    // closest — CharacterAnimator.BestMatchingPhase). Wins over StartT. Phase modes only.
    // This is the continuity tool for entering a cycle from an arbitrary pose (a fall
    // settling into the run's flight arc, a future run→vault alignment in reverse).
    public bool         MatchPose;

    public ClipChoice(AnimClip clip, ClipTimeMode time, float startT = -1f, bool matchPose = false)
    { Clip = clip; Time = time; StartT = startT; MatchPose = matchPose; }
}

// Everything a driver can contribute to one frame's solve, gathered before the solve runs.
// Pins feed the core's FixedPointConstraint block (they ARE constraints — this list is just
// its per-frame parameters); Constraints is the open door for a driver to ship its own
// residual block (paired with the core's fixed head/tail — see the composite assembly in
// CharacterAnimator.Update). Cleared and refilled by the core each frame.
public sealed class FrameInputs
{
    public readonly List<OverlayRequest>            Overlays    = new();
    public readonly List<(int Bone, Vector2 Target)> Pins       = new();
    public readonly List<ISolveConstraint>          Constraints = new();
    // This frame's EFFECTIVE solver config. The animator refreshes it from
    // AnimSolverConfig.Current (CopyFrom) at the top of every Update, BEFORE the driver's
    // Contribute runs, so a driver can override any knob programmatically for this frame
    // only — `dst.Solver.ComWeightY = …` — and the override evaporates next frame. Every
    // solve-side read (constraint rows, box limits, contact feathering) goes through this
    // copy, never through Current, which is what makes the override actually reach the
    // solve. Not cleared by Clear(): the refresh is the animator's job, so a driver that
    // touches nothing sees exactly the hot-reloaded json values.
    public readonly AnimSolverConfig                Solver      = new();
    public void Clear() { Overlays.Clear(); Pins.Clear(); Constraints.Clear(); }
}

public enum RunEntry : byte { None, Start, Turn }

// What the animator core remembers about the frames BEFORE this one, handed to every driver
// alongside the current sample. Some clip policy keys on an EDGE or a duration rather than on
// a single frame — a touchdown, a stop — and the sample, which is a pure snapshot of now,
// cannot express one. That memory used to be kept per-move in the core (CharacterAnimState
// .LandTime, a landing countdown the core armed and consumed); it lives here instead so the
// driver that cares owns its own policy.
//
// The core only MEASURES here. Every window, threshold and band over these measurements is a
// driver's business (LandingDriver's 0.25s), which is what keeps this struct move-agnostic:
// add a field when a driver needs a fact about the past it cannot read off one sample, never
// a timer for one move.
public struct AnimHistory
{
    public bool                 HasPrev;       // false only on the very first Update
    public CharacterAnimSample  Prev;          // last frame's sample
    // Seconds the body has been continuously grounded — 0 on the touchdown frame itself and
    // throughout flight, so `GroundedTime < w` IS "touched down within the last w seconds".
    public float                GroundedTime;
    // Downward speed (px/s) on the last airborne frame before the most recent touchdown —
    // how hard that landing was. Kept until the next touchdown overwrites it.
    public float                LandSpeed;
    // Seconds of the current grounded RUN stretch (run speed along the facing; 0 when not in
    // one), and how that stretch began: from a standstill, or out of a reversal of facing.
    public float                RunTime;
    public RunEntry             RunEntry;
    // Seconds the body has been continuously grounded in the idle band, and the ground speed
    // it had just before it stopped (a short decaying peak — the body brakes within a frame).
    public float                StillTime;
    public float                StopSpeed;

    // A character first observed already standing has been there forever as far as the
    // animator knows: seeding large is what keeps a grounded spawn from playing a landing.
    private const float Settled = 1e9f;
    // A heavy touchdown overshoots the float spring and the FSM reads "airborne" for a frame
    // or two on the rebound. That is one landing, not two: an airborne stretch shorter than
    // this is bridged — GroundedTime carries on across it and LandSpeed keeps the real impact.
    private const float BlipSeconds = 0.06f;
    private float _airTime;          // seconds of the current / last airborne stretch
    private float _groundedAtLeave;  // GroundedTime when that stretch began
    private float _sinceRun;             // seconds since the last frame of a grounded run stretch (seeded Settled)
    private int   _lastRunFacing;
    private float _speedPeak;            // |vx| peak, decaying at PeakDecay px/s²
    private const float PeakDecay    = 300f;
    private const float TurnGap      = 0.15f;   // a new run stretch this soon after the last, facing flipped = a turn

    // Called at the top of Update, BEFORE any driver runs, so a driver reads this frame's
    // measurements against last frame's retained sample.
    public void BeginFrame(in CharacterAnimSample s, float dt)
    {
        if (!HasPrev) { GroundedTime = s.Grounded ? Settled : 0f; StillTime = Settled; _sinceRun = Settled; return; }
        MeasureGroundSpeed(in s, dt);
        if (!s.Grounded)
        {
            if (Prev.Grounded) { _groundedAtLeave = GroundedTime; _airTime = 0f; }
            _airTime += dt;
            GroundedTime = 0f;
        }
        else if (Prev.Grounded) GroundedTime += dt;
        else if (_airTime < BlipSeconds) GroundedTime = _groundedAtLeave + _airTime + dt;   // a rebound blip: same landing
        else { GroundedTime = 0f; LandSpeed = Prev.Velocity.Y; }                            // a real touchdown
    }

    private void MeasureGroundSpeed(in CharacterAnimSample s, float dt)
    {
        float speed = MathF.Abs(s.Velocity.X);
        bool running = s.Grounded && GroundLocomotionDriver.IsRunning(in s);
        if (running)
        {
            if (RunTime <= 0f)   // a stretch begins: classify how
                RunEntry = _sinceRun < TurnGap && _lastRunFacing != s.Facing ? RunEntry.Turn
                         : _sinceRun >= TurnGap && Prev.Grounded ? RunEntry.Start
                         : RunEntry.None;
            RunTime += dt; _sinceRun = 0f; _lastRunFacing = s.Facing;
        }
        else { RunTime = 0f; _sinceRun += dt; }

        bool still = s.Grounded && speed <= GroundLocomotionDriver.WalkSpeedThreshold;
        if (!still) StillTime = 0f;
        else { if (StillTime <= 0f) StopSpeed = _speedPeak; StillTime += dt; }
        _speedPeak = MathF.Max(s.Grounded ? speed : 0f, _speedPeak - PeakDecay * dt);
    }

    // Called at the very bottom of Update — Prev stays last frame's for the whole body.
    public void EndFrame(in CharacterAnimSample s) { Prev = s; HasPrev = true; }
}

public interface IMoveDriver
{
    bool       Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h);
    ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h);
    // `t` is the clip's current normalized sample time (pre-advance) for drivers that need it.
    // History is here for contributions keyed on an edge (the run-landing dip after a touchdown).
    void       Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst);
}

// ─── The default registry ───────────────────────────────────────────────────────────────

public static class MoveDrivers
{
    // Registry order IS selection priority (replaces the old SelectClip if-chain's ordering):
    // tag-matched situations first — they're mutually exclusive (one tag per sample), but all
    // must precede the generic drivers because several tagged states are also airborne
    // (Tumble/WallJump/DoubleJump are strict subsets of !Grounded) or carry walk-band speed
    // (Stunned slides while grounded). LandingDriver sits below the tagged situations AND below
    // CrouchDriver — a tagged or crouched touchdown keeps its own clip, exactly as the old
    // core-side override did by only ever replacing an Idle choice. Airborne precedes ground
    // locomotion; ground locomotion is the terminal catch-all.
    public static IMoveDriver[] CreateDefault(Skeleton rig,
                                              IReadOnlyDictionary<string, AnimationDocument> actionClips)
        => new IMoveDriver[]
        {
            // Stairs (two or more risers ahead) outranks the single StepUp — the sample builder
            // tags at most one of them, so the order here only documents the intent.
            new TagClipDriver(AnimTag.Stairs, AnimClip.Stairs, ClipTimeMode.CadencePhase),
            new TagClipDriver(AnimTag.StepUp, AnimClip.StepUp, ClipTimeMode.CadencePhase),
            new ParkourDriver(rig, actionClips),
            new CrouchDriver(),
            new TagClipDriver(AnimTag.WallSlide,  AnimClip.WallSlide),
            new TagClipDriver(AnimTag.LedgePull,  AnimClip.LedgePull),
            new TagClipDriver(AnimTag.LedgeGrab,  AnimClip.Hang),    // stable vs. the grab spring's Vy ringing
            new LaunchClipDriver(AnimTag.LedgeJump, AnimClip.LedgeJump, LaunchClipDriver.LedgeJumpWindow),
            new TagClipDriver(AnimTag.Dropdown,   AnimClip.Dropdown),
            new TagClipDriver(AnimTag.Stunned,    AnimClip.Hitstun),
            new TagClipDriver(AnimTag.Tumble,     AnimClip.Tumble),
            new LaunchClipDriver(AnimTag.WallJump,  AnimClip.WallJumpKick, LaunchClipDriver.WallJumpWindow),
            new LaunchClipDriver(AnimTag.DoubleJump, AnimClip.DoubleJump, LaunchClipDriver.DoubleJumpWindow),
            new LaunchClipDriver(AnimTag.Dash,      AnimClip.Dash,       LaunchClipDriver.DashWindow),
            new SkidDriver(),
            new LandingDriver(),
            new RunStopDriver(),
            new AirborneDriver(),
            new GroundLocomotionDriver(actionClips),
        };
}

// One tag → one clock-driven clip, no contributions — the shape of most maneuver one-shots.
public sealed class TagClipDriver : IMoveDriver
{
    private readonly AnimTag _tag; private readonly AnimClip _clip; private readonly ClipTimeMode _time;
    public TagClipDriver(AnimTag tag, AnimClip clip, ClipTimeMode time = ClipTimeMode.Clock)
    { _tag = tag; _clip = clip; _time = time; }
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => s.Tag == _tag;
    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => new(_clip, _time);
    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst) { }
}

// Grounded locomotion — the terminal catch-all. Owns the speed/facing fan-out over the
// Idle/Walk/WalkBack/Run/RunTurn clip family. The first fraction of a second after a
// touchdown is LandingDriver's, which sits above this one and matches the same idle band.
public sealed class GroundLocomotionDriver : IMoveDriver
{
    // Touching down INTO a walk or run keeps the leg cycle (LandingDriver stays out of it), so
    // the impact is carried by the upper body alone: the LandRun overlay — torso pitching
    // forward, arms swinging down, then back up into the stride — over the first RunLandWindow
    // seconds. Only after a real drop (RunLandSpeed): the float spring and slope lips leave the
    // ground for a frame or two all the time, and those must not dip.
    private const string LandRunClipName = "LandRun";
    public const float RunLandWindow = 0.25f;   // keep == land_run.json Duration
    public const float RunLandSpeed  = 120f;    // px/s touchdown speed that earns the dip
    private readonly AnimationDocument _landRun;   // null if the clip isn't authored on this rig

    // The body reaches run speed in a frame and reverses in two or three, so a start or a turn
    // has no deceleration to show and the legs are cycling from the first frame. Both are told
    // by the upper body over the opening RunBurstWindow of the run stretch (AnimHistory.RunTime
    // / RunEntry): RunStart drops the torso into a sprint drive from standing; RunPivot throws
    // it back against the old momentum, then whips it forward into the new direction.
    private const string RunStartClipName = "RunStart", RunPivotClipName = "RunPivot";
    public const float RunBurstWindow = 0.25f;   // keep == run_start.json / run_pivot.json Duration
    private readonly AnimationDocument _runStart, _runPivot;

    public GroundLocomotionDriver(IReadOnlyDictionary<string, AnimationDocument> actionClips = null)
    {
        if (actionClips == null) return;
        actionClips.TryGetValue(LandRunClipName, out _landRun);
        actionClips.TryGetValue(RunStartClipName, out _runStart);
        actionClips.TryGetValue(RunPivotClipName, out _runPivot);
    }

    public const float WalkSpeedThreshold = 12f;   // px/s before Idle -> Walk
    public const float RunSpeedThreshold  = 40f;   // px/s before Walk -> Run (MaxWalkSpeed is 100)
    // Physical float above rest height (sample.GroundGap, px) beyond which a "grounded"
    // locomotion frame is treated as PRE-CONTACT: the FSM's StandingState precondition is
    // deliberately permissive (engages with the floor up to GroundChecker.ProbeSlack = 20px
    // away), so the body can be visibly airborne while the state — and therefore the old
    // clip choice — says running. Small slack so the normal float-spring ripple (±~1px
    // around rest) never triggers it.
    public const float PreContactGap = 2f;

    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h) => true;

    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
    {
        float speed = MathF.Abs(s.Velocity.X);
        if (speed > WalkSpeedThreshold)
        {
            // Moving against facing = backpedal at walk speeds, but ABOVE run speed it's a
            // direction-reversal skid (facing mirrors instantly on input; momentum still
            // carries the old way): play the RunTurn one-shot until velocity crosses zero
            // (→ Idle band) or realigns with facing (→ Run). Below run speed it's a
            // deliberate backpedal, which keeps WalkBack.
            if (Math.Sign(s.Velocity.X) != s.Facing)
                return speed > RunSpeedThreshold
                    ? new ClipChoice(AnimClip.RunTurn, ClipTimeMode.Clock)
                    : new ClipChoice(AnimClip.WalkBack, ClipTimeMode.CadencePhase);
            AnimClip clip = speed > RunSpeedThreshold ? AnimClip.Run : AnimClip.Walk;
            // PRE-CONTACT run/walk: the FSM is already in its ground state but the feet are
            // still descending toward the floor. Hold the SAME cycle clip at a frozen phase
            // (no cadence, no in-air leg cycling) until the gap closes. Entering from an air
            // clip (Fall/Jump) is a clip change, so MatchPose picks the phase whose pose is
            // closest to the pose already on screen (typically the run's flight arc); running
            // off a short ledge lip is NOT a clip change, so the cycle simply freezes where it
            // was and resumes on contact — phase-continuous both ways, because Hold and
            // CadencePhase share _state.Phase.
            if (s.GroundGap > PreContactGap)
                return new ClipChoice(clip, ClipTimeMode.Hold, matchPose: true);
            return new ClipChoice(clip, ClipTimeMode.CadencePhase);
        }
        return new ClipChoice(AnimClip.Idle, ClipTimeMode.IdleBob);
    }

    // Is this sample in the RUN band Select picks the Run clip for — run speed, travelling
    // with facing (a reversal skid is RunTurn, a backpedal is WalkBack)? Same test as Select
    // so Contribute's per-clip overrides can't drift from the clip choice.
    public static bool IsRunning(in CharacterAnimSample s)
        => MathF.Abs(s.Velocity.X) > RunSpeedThreshold && Math.Sign(s.Velocity.X) == s.Facing;

    // EXPERIMENT: a run squeezed under a low ceiling (LowCeiling — a solid tile right over
    // the head, the 2-high corridor case) runs with a softer vertical com tie, so the
    // ground-hold rows can lift the rig root off the low-riding com baseline instead of
    // mashing the authored full-height cycle into the floor. Per-frame override of the
    // effective config (FrameInputs.Solver) — gone the frame the roof clears. Only the Run
    // clip (not Walk/Idle) and only while physically supported (a pre-contact Hold frame has
    // no cadence to fix). See AnimSolverConfig.LowCeilingRunComWeightY.
    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst)
    {
        if (_landRun != null && s.Grounded && h.GroundedTime < RunLandWindow && h.LandSpeed >= RunLandSpeed
            && MathF.Abs(s.Velocity.X) > WalkSpeedThreshold)
            dst.Overlays.Add(new OverlayRequest(LandRunClipName, _landRun, h.GroundedTime / RunLandWindow));
        else if (h.RunTime > 0f && h.RunTime < RunBurstWindow && s.Grounded)
        {
            if (h.RunEntry == RunEntry.Start && _runStart != null)
                dst.Overlays.Add(new OverlayRequest(RunStartClipName, _runStart, h.RunTime / RunBurstWindow));
            else if (h.RunEntry == RunEntry.Turn && _runPivot != null)
                dst.Overlays.Add(new OverlayRequest(RunPivotClipName, _runPivot, h.RunTime / RunBurstWindow));
        }
        if (s.LowCeiling && s.GroundGap <= PreContactGap && IsRunning(in s))
            dst.Solver.ComWeightY = dst.Solver.LowCeilingRunComWeightY;
    }
}

// Knockback along the floor. A grounded hit leaves the FSM in Standing/Crouched (it only
// mutes self-control — PlayerCharacter's PreserveExternalVelocity), so the body slides at well
// over walking speed while the state still says "walking"; read as locomotion, the cadence
// solve spins the stride to match and the feet tip-toe. Instead the shove plays a held,
// clock-timed skid pose: Skid standing, CrouchSkid crouched (CrouchDriver).
//
// Entry is the sample's Knockback flag (hitstun muting control / a guard push's slide) with the
// body actually travelling. The skid then HOLDS past the flag for as long as the body is still
// faster than that stance can move itself (MaxWalkSpeed / CrouchMaxWalkSpeed, plus a margin) —
// hitstun ends long before the slide does, and dropping into Run at 2x the run speed is the
// exact tip-toe this exists to remove. Below that, the walk cycle can honestly carry the speed.
public static class SkidMotion
{
    // Hold margin over the stance's own speed cap, so the float-spring/accel ripple right at
    // the cap doesn't flicker skid ↔ walk on the way out.
    public const float HoldSpeedMargin = 1.1f;

    public static bool IsSkidding(in CharacterAnimSample s, in CharacterAnimState st, bool crouched)
    {
        if (!s.Grounded || s.GroundGap > GroundLocomotionDriver.PreContactGap) return false;
        float speed = MathF.Abs(s.Velocity.X);
        if (speed <= GroundLocomotionDriver.WalkSpeedThreshold) return false;
        if (s.Knockback) return true;
        if (st.Clip is not (AnimClip.Skid or AnimClip.CrouchSkid)) return false;
        var mc = MovementConfig.Current;
        float selfMax = crouched ? mc.CrouchMaxWalkSpeed : mc.MaxWalkSpeed;
        return speed > selfMax * HoldSpeedMargin;
    }
}

public sealed class SkidDriver : IMoveDriver
{
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => s.Tag != AnimTag.Crouch && SkidMotion.IsSkidding(in s, in st, crouched: false);
    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => new(AnimClip.Skid, ClipTimeMode.Clock);
    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst) { }
}

// The touchdown one-shot: a landing that settles into the IDLE BAND plays the authored Land
// clip for a moment instead of dropping straight onto Idle. Touching down into a walk or a run
// is deliberately excluded — the feet are already cycling and a one-shot would stutter them —
// which is the same band GroundLocomotionDriver picks Idle for, tested here against the same
// threshold so the two cannot drift apart.
//
// This is the driver that needed AnimHistory: "just landed" is an edge, invisible in any single
// sample. It was CharacterAnimState.LandTime — a countdown the core armed on the airborne→
// grounded edge and consumed by overriding the chosen clip — until 2026-09-20.
public sealed class LandingDriver : IMoveDriver
{
    // Seconds the one-shot owns the idle band after touchdown. Keep == the clip's Duration:
    // the clips are Clock-timed and hold their last pose, so a longer window freezes on the
    // final frame rather than playing more animation.
    public const float LandWindow     = 0.30f;   // land.json
    public const float HardLandWindow = 0.50f;   // land_hard.json
    // Touchdown speed (px/s) from which the heavy landing plays: a fall of ~75 px at world
    // gravity. A full-height jump back onto the level it left lands well under this.
    public const float HardLandSpeed  = 300f;

    private static float WindowOf(AnimClip c) => c == AnimClip.LandHard ? HardLandWindow : LandWindow;

    // A landing clip already playing keeps the band for its window while the body stays at
    // standing height — including the frame or two the FSM calls airborne on the float spring's
    // rebound (AnimHistory bridges the same blip). A real jump leaves standing height at once.
    private static bool Holding(in CharacterAnimSample s, in CharacterAnimState st)
        => st.Clip is AnimClip.Land or AnimClip.LandHard && st.ClipTime < WindowOf(st.Clip)
           && (s.Grounded || (s.GroundGap <= GroundLocomotionDriver.PreContactGap && s.Velocity.Y > -AirborneDriver.ApexSpeed));

    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
    {
        if (MathF.Abs(s.Velocity.X) > GroundLocomotionDriver.WalkSpeedThreshold) return false;
        if (Holding(in s, in st)) return true;
        return s.Grounded && h.GroundedTime < (h.LandSpeed >= HardLandSpeed ? HardLandWindow : LandWindow);
    }

    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => new(Holding(in s, in st) ? st.Clip : h.LandSpeed >= HardLandSpeed ? AnimClip.LandHard : AnimClip.Land,
               ClipTimeMode.Clock);

    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst) { }
}

// The stop one-shot: a body that was running a moment ago and is now standing in the idle band
// plays the authored RunStop — braced, the torso carried forward over the front knee, rocking
// back upright — instead of cutting to Idle. The body itself stops within a frame or two, so
// this is the only place the momentum can be shown. Sits below LandingDriver: a touchdown is a
// landing, not a stop.
public sealed class RunStopDriver : IMoveDriver
{
    public const float StopWindow = 0.30f;   // keep == run_stop.json Duration

    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => s.Grounded && s.Tag == AnimTag.None
           && MathF.Abs(s.Velocity.X) <= GroundLocomotionDriver.WalkSpeedThreshold
           && h.StillTime < StopWindow && h.StopSpeed > GroundLocomotionDriver.RunSpeedThreshold;

    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => new(AnimClip.RunStop, ClipTimeMode.Clock);

    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst) { }
}

// An airborne launch whose state is shorter than its clip. DoubleJumpingState and
// WallJumpingState last at most their MaxHoldTime (0.12 s; a tap releases them within a frame
// or two), and LedgeJumpState only until the legs clear the corner — all shorter than the
// tuck-and-push they play. So once started, the clip keeps the untagged airborne band until it
// has played out, the way LandingDriver keeps the idle band after a touchdown. Any tagged state
// (wall slide, ledge grab, …) still takes over: the hold only ever matches an untagged sample.
public sealed class LaunchClipDriver : IMoveDriver
{
    // Keep each == the clip's Duration: the clips are Clock-timed and hold their last pose.
    public const float DoubleJumpWindow = 0.36f;
    public const float WallJumpWindow   = 0.35f;
    public const float LedgeJumpWindow  = 0.40f;
    public const float DashWindow       = 0.36f;

    private readonly AnimTag _tag; private readonly AnimClip _clip; private readonly float _window;
    public LaunchClipDriver(AnimTag tag, AnimClip clip, float window)
    { _tag = tag; _clip = clip; _window = window; }

    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => s.Tag == _tag
           || (s.Tag == AnimTag.None && !s.Grounded
               && st.Clip == _clip && st.ClipTime < _window);

    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => new(_clip, ClipTimeMode.Clock);

    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst) { }
}

// Generic airborne (no tag): rising plays Jump, falling plays Fall, and the slow top of a rise
// plays the Apex float between them. Apex is ENTERED only while still rising (slower than
// ApexSpeed) and then held until the body is falling faster than ApexSpeed — so walking off a
// ledge (never rising) goes straight to Fall, and the float cannot flicker back to Jump.
public sealed class AirborneDriver : IMoveDriver
{
    // px/s. At world gravity (600 px/s²) the band is 2·75/600 = 0.25 s — keep ≈ apex.json Duration:
    // the clip is Clock-timed and holds its last pose.
    public const float ApexSpeed = 75f;
    // Seconds the Leap owns a running jump from the top of its rise. Keep == leap.json Duration.
    public const float LeapWindow = 0.65f;
    // Seconds of the Jump clip a running jump plays before the Leap takes over: the crouch and
    // the leg extension (jump.json's keys up to 0.10-0.15 s), not the long straight-legged rise.
    public const float LeapAfterTakeoff = 0.12f;

    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => !s.Grounded;
    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
    {
        float vy = s.Velocity.Y;
        // A RUNNING jump has no float: where a standing jump tucks into Apex, it opens straight
        // out of the take-off into the Leap — a stride split sailing forward — and holds it for
        // LeapWindow before settling into the generic Fall. Going Jump → Leap directly is the
        // point: through Apex the legs tucked and then split and the arms swapped sides, two
        // moves where the eye wants one. It takes over as soon as the Jump's push-off has played
        // (LeapAfterTakeoff) and opens gradually through the rest of the rise, so the split is
        // never a snap; out of any other launch it enters like Apex (still rising, slower than
        // ApexSpeed). Only at run speed along the facing; dropping the speed or turning round
        // hands back to Apex / Fall.
        if (GroundLocomotionDriver.IsRunning(in s)
            && (st.Clip == AnimClip.Leap ? st.ClipTime < LeapWindow
                : st.Clip == AnimClip.Jump ? vy < 0f && st.ClipTime >= LeapAfterTakeoff
                : vy < 0f && vy > -ApexSpeed && st.Clip != AnimClip.Apex))
            return new(AnimClip.Leap, ClipTimeMode.Clock);
        bool apex = vy < 0f ? vy > -ApexSpeed : st.Clip == AnimClip.Apex && vy < ApexSpeed;
        return new(apex ? AnimClip.Apex : vy < 0f ? AnimClip.Jump : AnimClip.Fall, ClipTimeMode.Clock);
    }
    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst) { }
}

// Crouching: a knockback shove plays CrouchSkid (SkidMotion). Otherwise CrouchWalk is the one
// crouch clip, moving or still. Its phase is driven by body
// travel (CadencePhase), so a still crouch simply holds/settles the shuffle cycle rather than
// switching to a separate static pose. The one exception is a still crouch under a solid
// ceiling right overhead, which plays DuckUnder (tucked head, braced hand — LowCeiling comes
// from the same CeilingChecker query CrouchedState uses, read-only). The old static Crouch
// clip is no longer selected.
public sealed class CrouchDriver : IMoveDriver
{
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => s.Tag == AnimTag.Crouch;
    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
    {
        if (SkidMotion.IsSkidding(in s, in st, crouched: true))
            return new ClipChoice(AnimClip.CrouchSkid, ClipTimeMode.Clock);
        if (s.LowCeiling && MathF.Abs(s.Velocity.X) <= GroundLocomotionDriver.WalkSpeedThreshold)
            return new ClipChoice(AnimClip.DuckUnder, ClipTimeMode.Clock);
        return new ClipChoice(AnimClip.CrouchWalk, ClipTimeMode.CadencePhase);
    }
    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst) { }
}

// The parkour vault: base clip is Parkour (clock-driven), plus two contributions synchronized
// to the maneuver's SPATIAL progress (body vs. the corner, not elapsed time):
//   · the ClimbHands UpperBody overlay (reach for the ledge → push off), τ = MovementProgress;
//   · a hard fixed-point pin holding the lead hand on the ledge corner over the grab window.
// The pinned hand is owned solely by the ClimbHands overlay — the plan invariant — so its
// post-compose Δθ bends it freely onto the corner.
public sealed class ParkourDriver : IMoveDriver
{
    private const string HandsClipName = "ClimbHands";
    private const string GripBoneName  = "arm_l_lower";   // the lead reaching hand the clip drives
    private const float  GripStart     = 0.45f;           // engage once the clip hand is near the corner…
    private const float  GripEnd       = 0.85f;           // …release before the push-off over the top

    private readonly AnimationDocument _hands;   // null if the clip isn't authored/loaded
    private readonly int               _gripBone;

    public ParkourDriver(Skeleton rig, IReadOnlyDictionary<string, AnimationDocument> actionClips)
    {
        actionClips.TryGetValue(HandsClipName, out _hands);
        _gripBone = rig.IndexOf(GripBoneName);
    }

    // Serves all three climb states for BASE clip selection, but the hands overlay and
    // the grip pin are the TWO-BLOCK arc's alone (2026-09-05, owner's call): bringing
    // the arms down onto the corner reads right for a 2-high climb — a single 1-tile
    // step (speed vault or flush mantle) should stay a legs-only stride, no arm
    // gesture. Ledge pulls keep their own hand treatment via the ledge_pull reference
    // clip (LedgeStates), untouched by this driver.
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => s.Tag is AnimTag.Parkour or AnimTag.Mantle or AnimTag.ArcJump;

    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st, in AnimHistory h)
        => new(s.Tag switch
        {
            AnimTag.Mantle  => AnimClip.Mantle,
            AnimTag.ArcJump => AnimClip.ArcJump,
            _               => AnimClip.Parkour,
        }, ClipTimeMode.Clock);

    public void Contribute(in CharacterAnimSample s, float t, in AnimHistory h, FrameInputs dst)
    {
        if (s.Tag != AnimTag.ArcJump) return;   // 1-tile climbs: legs only (see Matches)
        if (_hands != null)
            dst.Overlays.Add(new OverlayRequest(HandsClipName, _hands,
                                                MathHelper.Clamp(s.MovementProgress, 0f, 1f)));
        // Gating the pin where the clip already brings the hand near the corner keeps the lock
        // smooth (a small Δθ correction); a both-axis hard pin from progress 0 would snap it.
        if (s.HasGrip && _gripBone >= 0
            && s.MovementProgress >= GripStart && s.MovementProgress <= GripEnd)
            dst.Pins.Add((_gripBone, s.GripTarget));
    }
}
