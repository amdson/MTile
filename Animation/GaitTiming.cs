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
    // Foot-synchronized servo (T6). ObservedPhase is what the planted feet say the phase is
    // (Observe); the servo tracks it as a bounded RATE change, never a phase jump, unless
    // |error| > ReentryError. ServoMaxRate is a fraction of max(feedforward, authored) rate;
    // RateSlew (cycles/s²) bounds the whole rate's change per frame, 0 = off.
    public bool    HasObserved;
    public float   ObservedPhase;
    public float   ServoGain, ServoMaxRate, RateSlew, ReentryError;
}

public struct TimingResult
{
    public float  DeltaPhase;            // this frame's advance (unwrapped, ≥ 0)
    public float  Rate;                  // cycles/s this frame
    public float  Travel;                // body travel along the authored direction (px)
    public float  CycleDistance;         // authored body travel per cycle (px, |D|; 0 = none)
    public Vector2 Direction;            // unit authored direction in the facing frame (+x forward, +y down)
    public string Source;                // where the stride came from (diagnostics)
    public TimingState State;
    public float  SettleRemaining, SettleTimeLeft;
    // Servo diagnostics: the wrapped observed-minus-phase error this frame (0 without an
    // observation), the rate correction applied (cycles/s), and whether the phase re-entered.
    public float  PhaseError, ServoRate;
    public bool   Reentered;
}

public static class GaitTiming
{
    // The legacy global stride: 0.010 cycles/s per px/s ⇒ 100 px per cycle. The per-clip
    // nominal fallback for clips that author neither a scene path nor contact labels.
    public const float NominalCycleDistance = 100f;
    // Below this authored travel the clip is treated as stationary (time-driven playback).
    private const float StationaryCycle = 1f;   // px per cycle
    private const float DefaultMaxStep  = 0.5f; // half a cycle per frame — a teleport guard, not a tuning

    // Authored body displacement per cycle, a VECTOR in the facing frame (+x forward at the
    // canonical facing, +y down; a backpedal cycle points −x, a stair cycle rises). First
    // available of:
    //   1. the clip's scene path (BodyPath): D = p(1) − p(0), scaled — the only source that
    //      carries a RISE: a scene path is authored intent (chunk 7's step-up pilot bakes one);
    //   2. the gait track's stance sweeps (ClipStrideTrack.CycleDisplacement), the run only —
    //      a flat cycle's stance Y sweep is the body's bob, not a direction to pace along;
    //   3. nothing authored: NominalCycleDistance along +x (the legacy constant), reported as such.
    public static Vector2 CycleDisplacement(AnimationDocument clip, ClipStrideTrack gait, float scale, out string source)
    {
        if (BodyPath.TryCycleDisplacement(clip, out var d))
        {
            source = "body_path";
            return d * scale;
        }
        if (gait != null && gait.HasTravel)
        {
            source = "gait_track";
            return new Vector2(gait.CycleDisplacement.X * scale, 0f);
        }
        source = "nominal";
        return new Vector2(NominalCycleDistance, 0f);
    }

    // The run component of CycleDisplacement (legacy scalar form; a stair cycle's rise is
    // not in it — callers pacing a 2-D cycle use the vector).
    public static float CycleDistance(AnimationDocument clip, ClipStrideTrack gait, float scale, out string source)
        => CycleDisplacement(clip, gait, scale, out source).X;

    public static TimingResult Advance(in TimingInputs inp)
    {
        var r = new TimingResult();
        float dt = MathF.Max(inp.Dt, 1e-5f);
        float maxStep = inp.MaxStep > 0f ? inp.MaxStep : DefaultMaxStep;
        int dir = inp.Facing == 0 ? 1 : inp.Facing;
        // The body's motion in the facing frame (+x = forward for this facing, +y down).
        Vector2 move = new(dir * (inp.Pos.X - inp.PrevPos.X), inp.Pos.Y - inp.PrevPos.Y);
        Vector2 D = CycleDisplacement(inp.Clip, inp.Gait, inp.Scale, out r.Source);
        r.CycleDistance = D.Length();
        // Per-component division (Vector2/float multiplies by a reciprocal): a flat cycle's
        // direction is exactly ±1 along x, so its pacing is the same float it always was.
        r.Direction = r.CycleDistance > 1e-6f ? new Vector2(D.X / r.CycleDistance, D.Y / r.CycleDistance) : Vector2.UnitX;

        // ── Travel-based advance (§4) ──────────────────────────────────────────────────
        float dphi;
        if (r.Source == "nominal")
        {
            // No authored direction: direction-agnostic, like the legacy speed·PhasePerPixel.
            r.Travel = MathF.Abs(move.X);
            dphi = r.Travel / NominalCycleDistance;
        }
        else if (r.CycleDistance < StationaryCycle)
        {
            // A cycle that does not travel (a shuffle in place): play it at its authored rate.
            r.Travel = 0f;
            float dur = inp.Clip.Duration <= 1e-4f ? 1f : inp.Clip.Duration;
            dphi = dt / dur;
        }
        else
        {
            // Travel PROJECTED onto the authored direction, at one constant rate per cycle
            // (chunk 7: a stair cycle advances on the climb as well as the run, so the sim's
            // hop-then-run over a riser reads as one continuous step rather than a stall and a
            // jump). Travel against the direction never plays the cycle in reverse (clamped —
            // the stopping policy owns what happens then); motion across it is not travel.
            r.Travel = Vector2.Dot(move, r.Direction);
            dphi = MathF.Max(0f, r.Travel / r.CycleDistance);
        }
        dphi = MathF.Min(dphi, maxStep);

        // ── Foot-synchronized servo (T6) ───────────────────────────────────────────────
        // The advance above integrates travel and drifts against what the feet actually do
        // (the sim's hop rhythm on stairs is not the com path). A planted foot is a fixed
        // world point, so the body's offset from it reads the phase directly (Observe); the
        // servo removes the drift as a bounded change of RATE — the phase never jumps — so
        // the clip merely plays a little faster or slower for a few frames. Past ReentryError
        // the clip and the feet disagree outright: the phase re-enters AT the observation and
        // the smoothness prior bridges the pose, exactly as on a clip switch. Traveling only —
        // the stopping policy owns the phase while settling. The slew bounds the whole rate's
        // change per frame (feedforward included): that is the anti-jerk term.
        float ffRate = dphi / dt;
        float rate   = ffRate;
        if (inp.HasObserved && inp.State == TimingState.Traveling && r.CycleDistance >= StationaryCycle)
        {
            float err = WrapHalf(inp.ObservedPhase - inp.Phase);
            r.PhaseError = err;
            if (inp.ReentryError > 0f && MathF.Abs(err) > inp.ReentryError)
            {
                r.Reentered  = true;
                r.DeltaPhase = err;        // signed: the phase lands ON the observation
                r.Rate       = ffRate;     // the next frame's slew starts from the feedforward
                r.State = inp.State; r.SettleRemaining = inp.SettleRemaining; r.SettleTimeLeft = inp.SettleTimeLeft;
                return r;
            }
            float dur   = inp.Clip.Duration <= 1e-4f ? 1f : inp.Clip.Duration;
            float bound = inp.ServoMaxRate * MathF.Max(ffRate, 1f / dur);
            r.ServoRate = MathHelper.Clamp(inp.ServoGain * err, -bound, bound);
            rate = MathF.Max(0f, ffRate + r.ServoRate);
        }
        if (inp.RateSlew > 0f)
        {
            float lim = inp.RateSlew * dt;
            rate = MathF.Max(0f, MathHelper.Clamp(rate, inp.PrevRate - lim, inp.PrevRate + lim));
        }
        dphi = MathF.Min(rate * dt, maxStep);

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

    // Wrap a phase difference to [-0.5, 0.5).
    public static float WrapHalf(float d) => d - MathF.Floor(d + 0.5f);
    private static float Wrap01(float x) => x - MathF.Floor(x);

    // Below this authored sweep (rig units) a stance offers no phase reading.
    private const float MinObservableSweep = 2f;

    // (T6) THE PHASE THE PLANTED FEET IMPLY. During a stance the foot's body-relative offset
    // sweeps TdOffset → LoOffset (ClipStrideTrack), so with the support point S fixed in the
    // world the observed offset o = (S − body) in the facing frame projects onto that sweep
    // at a progress u, and the foot says the phase is Touchdown + u·(Liftoff − Touchdown).
    // Each planner-held stance (FootPlan.State == Stance with a support) votes, weighted by
    // its engage/release ramp, and the votes combine as wrapped differences from the current
    // phase so a foot swap hands over softly. The stance a foot is read against is the one
    // nearest the current phase (a foot may plant twice per cycle — the stairs — and the two
    // sweeps look alike; anything further off than that is a re-entry's job, not a reading).
    // `residual` reports the worst perpendicular miss from the sweep (rig units, diagnostic:
    // the sim hovers the body, so a vertical miss is expected and not gated on).
    public static bool Observe(ClipStrideTrack track, FootPlan[] plans, int feetCount,
                               Vector2 body, int facing, float scale, float phase,
                               out float observed, out float residual)
    {
        observed = phase; residual = 0f;
        if (track == null || plans == null || feetCount <= 0 || scale <= 1e-6f) return false;
        int dir = facing == 0 ? 1 : facing;
        float sumW = 0f, sumErr = 0f, worst = 0f;
        int n = Math.Min(feetCount, Math.Min(plans.Length, track.Feet.Length));
        for (int i = 0; i < n; i++)
        {
            var plan = plans[i];
            if (plan.State != FootPlanState.Stance || !plan.HasSupport || plan.Weight <= 1e-3f) continue;
            var ft = track.Feet[i];
            Vector2 o = new((plan.Target.X - body.X) / (dir * scale), (plan.Target.Y - body.Y) / scale);

            int best = -1; float bestDist = float.MaxValue;
            for (int k = 0; k < ft.Stances.Length; k++)
            {
                var st = ft.Stances[k];
                if (st.Persistent) continue;
                if ((st.LoOffset - st.TdOffset).LengthSquared() < MinObservableSweep * MinObservableSweep) continue;
                // Distance from the current phase to the stance's interval, on the circle.
                float span = st.Liftoff - st.Touchdown;
                float du = Wrap01(phase - st.Touchdown);
                float dist = du < span ? 0f : MathF.Min(du - span, 1f - du);
                if (dist < bestDist) { bestDist = dist; best = k; }
            }
            if (best < 0) continue;
            var s = ft.Stances[best];
            Vector2 chord = s.LoOffset - s.TdOffset;
            float u   = MathHelper.Clamp(Vector2.Dot(o - s.TdOffset, chord) / chord.LengthSquared(), 0f, 1f);
            float res = (o - s.TdOffset - u * chord).Length();
            float phi = s.Touchdown + u * (s.Liftoff - s.Touchdown);
            sumW += plan.Weight; sumErr += plan.Weight * WrapHalf(phi - phase);
            worst = MathF.Max(worst, res);
        }
        if (sumW <= 0f) return false;
        observed = Wrap01(phase + sumErr / sumW);
        residual = worst;
        return true;
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
