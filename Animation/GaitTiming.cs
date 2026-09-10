using System;
using Microsoft.Xna.Framework;

namespace MTile;

// THE TIMING STAGE (Plans/ANIMATION_TIMING_STAGE.md, workplan chunk 5; runtime plan §4) —
// the one owner of the locomotion phase (ANIMATION_OWNERSHIP_CONTRACT.md, Rule A). Produces
// this frame's phase advance BEFORE the pose solve, from the body's actual travel and the
// clip's authored stride, so contacts, the planner and the joint solve all evaluate at the
// resulting phase and the solve fits root offset + limbs with Δφ locked.
//
// Pure per-frame function of explicit inputs (same discipline as StepPlanner): no reference
// to CharacterAnimator, headless-testable. T1 = travel-based timing; T2 (bounded 1-D
// refinement) and T3 (the stopping policy) layer on top of this result.
public struct TimingInputs
{
    public AnimationDocument Clip;
    public ClipStrideTrack   Gait;       // any-source stride track (timing only; may be null)
    public float   Phase;                // entry phase [0,1)
    public float   PrevRate;             // last frame's rate, cycles/s
    public float   Dt;
    public Vector2 Pos, PrevPos;         // body position now / last frame (world px)
    public int     Facing;               // -1 / +1
    public float   Scale;                // world px per rig unit
    public float   MaxStep;              // cap on one frame's advance (cycles; 0 = default)
}

public struct TimingResult
{
    public float  DeltaPhase;            // this frame's advance (unwrapped, ≥ 0)
    public float  Rate;                  // cycles/s this frame
    public float  Travel;                // body travel along the authored direction (px)
    public float  CycleDistance;         // authored body travel per cycle (px, signed; 0 = none)
    public string Source;                // where the stride came from (diagnostics)
}

public static class GaitTiming
{
    // The legacy global stride: 0.010 cycles/s per px/s ⇒ 100 px per cycle. The per-clip
    // nominal fallback for clips that author neither a scene path nor contact labels.
    public const float NominalCycleDistance = 100f;
    // Below this authored travel the clip is treated as stationary (time-driven playback).
    private const float StationaryCycle = 1f;   // px per cycle
    private const float DefaultMaxStep  = 0.5f; // half a cycle per frame — a teleport guard, not a tuning

    // Authored body travel per cycle, SIGNED in the facing frame (negative = the feet move
    // forward under the body, i.e. a backpedal cycle). First available of:
    //   1. the clip's scene path (BodyPath): D = p(1) − p(0), scaled;
    //   2. the gait track's stance sweeps (ClipStrideTrack.CycleTravel), scaled;
    //   3. nothing authored: NominalCycleDistance (the legacy constant), reported as such.
    public static float CycleDistance(AnimationDocument clip, ClipStrideTrack gait, float scale, out string source)
    {
        if (BodyPath.TryCycleDisplacement(clip, out var d))
        {
            source = "body_path";
            return d.X * scale;
        }
        if (gait != null && gait.HasTravel)
        {
            source = "gait_track";
            return gait.CycleTravel * scale;
        }
        source = "nominal";
        return NominalCycleDistance;
    }

    public static TimingResult Advance(in TimingInputs inp)
    {
        var r = new TimingResult();
        float dt = MathF.Max(inp.Dt, 1e-5f);
        float maxStep = inp.MaxStep > 0f ? inp.MaxStep : DefaultMaxStep;
        int dir = inp.Facing == 0 ? 1 : inp.Facing;
        float dx = inp.Pos.X - inp.PrevPos.X;
        r.CycleDistance = CycleDistance(inp.Clip, inp.Gait, inp.Scale, out r.Source);

        float dphi;
        if (r.Source == "nominal")
        {
            // No authored direction: direction-agnostic, like the legacy speed·PhasePerPixel.
            r.Travel = MathF.Abs(dx);
            dphi = r.Travel / NominalCycleDistance;
        }
        else if (MathF.Abs(r.CycleDistance) < StationaryCycle)
        {
            // A cycle that does not travel (a shuffle in place): play it at its authored rate.
            r.Travel = 0f;
            float dur = inp.Clip.Duration <= 1e-4f ? 1f : inp.Clip.Duration;
            dphi = dt / dur;
        }
        else
        {
            // Travel along the authored direction, at one constant rate per cycle. Backward
            // travel against it never plays the cycle in reverse (clamped — the stopping
            // policy owns what happens then).
            r.Travel = dir * dx;
            dphi = MathF.Max(0f, r.Travel / r.CycleDistance);
        }

        r.DeltaPhase = MathF.Min(dphi, maxStep);
        r.Rate = r.DeltaPhase / dt;
        return r;
    }
}
