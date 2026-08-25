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
//   Matches     — is this the active situation? First match in registry order wins.
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

public interface IMoveDriver
{
    bool       Matches(in CharacterAnimSample s, in CharacterAnimState st);
    ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st);
    // `t` is the clip's current normalized sample time (pre-advance) for drivers that need it.
    void       Contribute(in CharacterAnimSample s, float t, FrameInputs dst);
}

// ─── The default registry ───────────────────────────────────────────────────────────────

public static class MoveDrivers
{
    // Registry order IS selection priority (replaces the old SelectClip if-chain's ordering):
    // tag-matched situations first — they're mutually exclusive (one tag per sample), but all
    // must precede the generic drivers because several tagged states are also airborne
    // (Tumble/WallJump/DoubleJump are strict subsets of !Grounded) or carry walk-band speed
    // (Stunned slides while grounded). Airborne precedes ground locomotion; ground locomotion
    // is the terminal catch-all.
    public static IMoveDriver[] CreateDefault(Skeleton rig,
                                              IReadOnlyDictionary<string, AnimationDocument> actionClips)
        => new IMoveDriver[]
        {
            new ParkourDriver(rig, actionClips),
            new CrouchDriver(),
            new TagClipDriver(AnimTag.WallSlide,  AnimClip.WallSlide),
            new TagClipDriver(AnimTag.LedgePull,  AnimClip.LedgePull),
            new TagClipDriver(AnimTag.LedgeGrab,  AnimClip.Hang),    // stable vs. the grab spring's Vy ringing
            new TagClipDriver(AnimTag.LedgeJump,  AnimClip.LedgeJump),
            new TagClipDriver(AnimTag.Dropdown,   AnimClip.Dropdown),
            new TagClipDriver(AnimTag.Stunned,    AnimClip.Hitstun),
            new TagClipDriver(AnimTag.Tumble,     AnimClip.Tumble),
            new TagClipDriver(AnimTag.WallJump,   AnimClip.WallJumpKick),
            new TagClipDriver(AnimTag.DoubleJump, AnimClip.DoubleJumpFlip),
            new AirborneDriver(),
            new GroundLocomotionDriver(),
        };
}

// One tag → one clock-driven clip, no contributions — the shape of most maneuver one-shots.
public sealed class TagClipDriver : IMoveDriver
{
    private readonly AnimTag _tag; private readonly AnimClip _clip; private readonly ClipTimeMode _time;
    public TagClipDriver(AnimTag tag, AnimClip clip, ClipTimeMode time = ClipTimeMode.Clock)
    { _tag = tag; _clip = clip; _time = time; }
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st) => s.Tag == _tag;
    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st) => new(_clip, _time);
    public void Contribute(in CharacterAnimSample s, float t, FrameInputs dst) { }
}

// Grounded locomotion — the terminal catch-all. Owns the speed/facing fan-out over the
// Idle/Walk/WalkBack/Run/RunTurn clip family (the Land override on a near-idle touchdown
// stays core-side: it's cross-move memory of the airborne→grounded edge).
public sealed class GroundLocomotionDriver : IMoveDriver
{
    public const float WalkSpeedThreshold = 12f;   // px/s before Idle -> Walk
    public const float RunSpeedThreshold  = 40f;   // px/s before Walk -> Run (MaxWalkSpeed is 100)
    // Physical float above rest height (sample.GroundGap, px) beyond which a "grounded"
    // locomotion frame is treated as PRE-CONTACT: the FSM's StandingState precondition is
    // deliberately permissive (engages with the floor up to GroundChecker.ProbeSlack = 20px
    // away), so the body can be visibly airborne while the state — and therefore the old
    // clip choice — says running. Small slack so the normal float-spring ripple (±~1px
    // around rest) never triggers it.
    public const float PreContactGap = 2f;

    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st) => true;

    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st)
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
    public void Contribute(in CharacterAnimSample s, float t, FrameInputs dst)
    {
        if (s.LowCeiling && s.GroundGap <= PreContactGap && IsRunning(in s))
            dst.Solver.ComWeightY = dst.Solver.LowCeilingRunComWeightY;
    }
}

// Generic airborne (no tag): rising plays Jump, falling plays Fall.
public sealed class AirborneDriver : IMoveDriver
{
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st) => !s.Grounded;
    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st)
        => new(s.Velocity.Y < 0f ? AnimClip.Jump : AnimClip.Fall, ClipTimeMode.Clock);
    public void Contribute(in CharacterAnimSample s, float t, FrameInputs dst) { }
}

// Crouching: a moving crouch plays the CrouchWalk shuffle cycle (same cadence machinery as
// Walk); a still crouch under a solid ceiling right overhead plays DuckUnder (tucked head,
// braced hand — LowCeiling comes from the same CeilingChecker query CrouchedState uses,
// read-only); otherwise the generic static Crouch.
public sealed class CrouchDriver : IMoveDriver
{
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st) => s.Tag == AnimTag.Crouch;
    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st)
    {
        if (MathF.Abs(s.Velocity.X) > GroundLocomotionDriver.WalkSpeedThreshold)
            return new ClipChoice(AnimClip.CrouchWalk, ClipTimeMode.CadencePhase);
        return new ClipChoice(s.LowCeiling ? AnimClip.DuckUnder : AnimClip.Crouch, ClipTimeMode.Clock);
    }
    public void Contribute(in CharacterAnimSample s, float t, FrameInputs dst) { }
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

    // Serves all three climb states. They share the hands overlay and the grip pin — the
    // reach-and-push-off is the same gesture whatever the entry speed — but each selects its
    // own base clip so the speed vault, the flush climb and the two-block arc can diverge.
    public bool Matches(in CharacterAnimSample s, in CharacterAnimState st)
        => s.Tag is AnimTag.Parkour or AnimTag.Mantle or AnimTag.ArcJump;

    public ClipChoice Select(in CharacterAnimSample s, in CharacterAnimState st)
        => new(s.Tag switch
        {
            AnimTag.Mantle  => AnimClip.Mantle,
            AnimTag.ArcJump => AnimClip.ArcJump,
            _               => AnimClip.Parkour,
        }, ClipTimeMode.Clock);

    public void Contribute(in CharacterAnimSample s, float t, FrameInputs dst)
    {
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
