using System;
using Microsoft.Xna.Framework;

namespace MTile;

// THE TIMING STAGE (Plans/ANIMATION_TIMING_STAGE.md, workplan chunk 5; runtime plan §4, §5) —
// the one owner of the locomotion phase (ANIMATION_OWNERSHIP_CONTRACT.md, Rule A). Produces
// this frame's phase advance BEFORE the pose solve, from the body's actual travel and the
// clip's authored stride, so contacts, the planner and the joint solve all evaluate at the
// resulting phase and the solve fits root offset + limbs with Δφ locked.
//
// Pure per-frame function of explicit inputs (same discipline as StepPlanner): no reference
// to CharacterAnimator, headless-testable. The stopping policy (§5) is a three-state machine
// whose state the caller carries in TimingInputs/TimingResult:
//
//   Traveling ──(grounded, speed < SettleSpeed and not rising)──▶ Settling
//   Settling  ──(the nearest landing reached AND speed < idle band)──▶ SupportedIdle
//   Settling / SupportedIdle ──(speed > SettleExitSpeed)──▶ Traveling   (restart: phase continues)
//
// Settling advances the phase by max(travel, a time schedule to the nearest touchdown) —
// residual travel is still honored, and a foot never freezes mid-swing; SupportedIdle holds
// the phase. The landing itself belongs to the foot's owner (Rule B): the planner / the
// self-plant capture react to the phase this stage produces.
public enum TimingState { Traveling, Settling, SupportedIdle }

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
    // Stopping policy
    public TimingState State;            // carried from last frame's result
    public float   SettleRemaining;      // cycles left to the landing being finished (Settling)
    public float   SettleTimeLeft;       // seconds left on the settle schedule (Settling)
    public float   Speed, PrevSpeed;     // |vx| now / last frame (px/s)
    public bool    Grounded;
    public float   SettleSpeed, SettleExitSpeed, IdleSpeed, SettleTime;   // AnimSolverConfig + the driver's idle band
}

public struct TimingResult
{
    public float  DeltaPhase;            // this frame's advance (unwrapped, ≥ 0)
    public float  Rate;                  // cycles/s this frame
    public float  Travel;                // body travel along the authored direction (px)
    public float  CycleDistance;         // authored body travel per cycle (px, signed; 0 = none)
    public string Source;                // where the stride came from (diagnostics)
    public TimingState State;
    public float  SettleRemaining, SettleTimeLeft;
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

        // ── Travel-based advance (§4) ──────────────────────────────────────────────────
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
        dphi = MathF.Min(dphi, maxStep);

        // ── Stopping policy (§5) ───────────────────────────────────────────────────────
        var state = inp.State;
        float remaining = inp.SettleRemaining, timeLeft = inp.SettleTimeLeft;
        bool restart = inp.Speed > inp.SettleExitSpeed;
        if (state != TimingState.Traveling && restart) state = TimingState.Traveling;
        if (state == TimingState.Traveling && WantsSettle(inp.Grounded, inp.Speed, inp.PrevSpeed, inp.SettleSpeed))
        {
            state = TimingState.Settling;
            remaining = NearestLanding(inp.Gait, inp.Phase);
            timeLeft  = inp.SettleTime;
        }
        if (state == TimingState.Settling)
        {
            if (remaining <= 1e-4f)
            {
                if (inp.Speed < inp.IdleSpeed) state = TimingState.SupportedIdle;
                else { remaining = NearestLanding(inp.Gait, inp.Phase); timeLeft = inp.SettleTime; }   // still moving: the next step
            }
        }
        if (state == TimingState.Settling)
        {
            // Finish the nearest landing on the schedule, never slower than travel demands.
            float finish = remaining * dt / MathF.Max(timeLeft, dt);
            dphi = MathF.Min(MathF.Max(dphi, finish), remaining);
            remaining -= dphi;
            timeLeft  -= dt;
        }
        else if (state == TimingState.SupportedIdle) dphi = 0f;   // hold the settled support

        r.State = state; r.SettleRemaining = remaining; r.SettleTimeLeft = timeLeft;
        r.DeltaPhase = dphi;
        r.Rate = dphi / dt;
        return r;
    }

    // Settle entry: grounded, under the settle speed and not accelerating. Shared with the
    // animator's clip-choice override, which must apply it on the frame the stop happens.
    public static bool WantsSettle(bool grounded, float speed, float prevSpeed, float settleSpeed)
        => grounded && speed < settleSpeed && speed <= prevSpeed + 1e-3f;

    // Cycles from `phase` to the nearest touchdown of any foot currently in swing (0 when
    // no tracked foot is swinging: the stance already supports the stop).
    public static float NearestLanding(ClipStrideTrack gait, float phase)
    {
        if (gait == null) return 0f;
        float best = 0f; bool any = false;
        foreach (var f in gait.Feet)
        {
            int si = f.SwingAt(phase, out float u);
            if (si < 0) continue;
            var w = f.Swings[si];
            float left = (1f - u) * (w.End - w.Start);
            if (!any || left < best) { best = left; any = true; }
        }
        return any ? best : 0f;
    }
}
