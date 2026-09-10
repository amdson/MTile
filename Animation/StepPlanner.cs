using System;
using Microsoft.Xna.Framework;

namespace MTile;

// THE STEP PLANNER (Plans/ANIMATION_STEP_PLANNER_IMPL.md §3) — render-only, per
// character. Reads the compiled stride track, the body's actual/predicted motion, and
// finite treads (SupportQuery); owns each planned foot's contact lifecycle: where the
// next landing goes, the swing trajectory toward it, when stance establishes and
// releases. It never touches the sim and holds no reference to CharacterAnimator —
// the animator adapts its internals into PlannerInputs and consumes FootPlans.
//
// Everything here is a structural invariant or a single scalar score:
//     score = |candidate − preferred| − (hysteresis if it's the held tread)
// with candidates hard-gated by reach (rig-derived, FootStrideTrack.MaxReachRig) and
// by swing-path clearance (sampled against solid cells). No terrain-class or gait
// conditionals — infeasibility is reported (Reject), not special-cased.
//
// Knob budget (AnimSolverConfig): PlannerEnabled, PlannerHysteresis,
// PlannerLateSwingLock. Weight ramps reuse ContactEngageTime/ContactReleaseTime.

public enum FootPlanState { Unplanned, Swing, Stance }

public enum StepReject { None, NoSupport, Unreachable, SwingBlocked }

public struct FootPlan
{
    public int           Bone;
    public FootPlanState State;
    public Vector2       Target;     // swing: this frame's trajectory point; stance: the fixed support point
    public SupportSegment Support;   // valid when State == Stance, or during swing once a landing is held
    public bool          HasSupport;
    public float         Weight;     // planner-owned engage/release ramp, [0,1]
    public StepReject    Reject;     // why Unplanned (or why the landing is a fallback)
    public Vector2       Preferred;  // debug/overlay: the authored-offset landing wish
    public float         SwingU;     // debug/overlay: swing progress [0,1]
}

public struct PlannerInputs
{
    public Vector2  BodyPos, BodyVel;
    public int      Facing;          // -1 / +1
    public float    Scale;           // rig scale (world px per rig unit)
    public float    Phase;           // locomotion phase [0,1)
    public float    NominalRate;     // dφ/dt, cycles/s — pace for time-to-touchdown estimates
    public float    Dt;
    public ClipStrideTrack Track;
    public ChunkMap Chunks;
    public Func<float, Vector2> PredictAt;   // dt-ahead body prediction; null → velocity fallback
}

public sealed class StepPlanner
{
    public const int MaxFeet = 4;
    private const int MaxTreads = 32;
    private const float QueryRadius = 2.5f * Chunk.TileSize;   // around the preferred landing
    private const float MaxLookahead = 0.6f;                   // s — cap on touchdown prediction
    private const float SwingProbeLift = 3f;                   // px — sub-tile dip tolerance in the swing check

    public readonly FootPlan[] Plans = new FootPlan[MaxFeet];
    public int FeetCount;

    private struct FootState
    {
        public FootPlanState State;
        public float   Weight;
        public long    SupportId;      // stance: held tread; swing: selected landing tread
        public bool    HasSupport;
        public Vector2 SupportPoint;   // the fixed world point on it
        public Vector2 Takeoff;        // swing start point (previous stance point, or authored fallback)
    }
    private readonly FootState[] _feet = new FootState[MaxFeet];

    public void Reset()
    {
        FeetCount = 0;
        Array.Clear(_feet, 0, _feet.Length);
        Array.Clear(Plans, 0, Plans.Length);
    }

    public void Update(in PlannerInputs inp)
    {
        var track = inp.Track;
        FeetCount = track == null ? 0 : Math.Min(track.Feet.Length, MaxFeet);
        if (FeetCount == 0 || inp.Chunks == null) { FeetCount = 0; return; }

        var cfg = AnimSolverConfig.Current;
        int dir = inp.Facing == 0 ? 1 : inp.Facing;
        float engage  = inp.Dt / MathF.Max(1e-3f, cfg.ContactEngageTime);
        float release = inp.Dt / MathF.Max(1e-3f, cfg.ContactReleaseTime);

        Span<SupportSegment> treads = stackalloc SupportSegment[MaxTreads];

        for (int fi = 0; fi < FeetCount; fi++)
        {
            var ft = track.Feet[fi];
            ref var st = ref _feet[fi];
            var plan = new FootPlan { Bone = ft.Bone, Reject = StepReject.None };
            float maxReach = ft.MaxReachRig * inp.Scale;

            // Where in the cycle is this foot?
            int stanceIdx = FindStance(ft, inp.Phase, out _);
            int swingIdx = stanceIdx >= 0 ? -1 : FindSwing(ft, inp.Phase, out plan.SwingU);

            if (stanceIdx >= 0)
            {
                var stance = ft.Stances[stanceIdx];
                if (st.State != FootPlanState.Stance)
                {
                    // Touchdown: land on the support selected during swing; if none (or it
                    // died), try an immediate selection around the authored offset — do not
                    // pin a distant point just because phase crossed the event.
                    if (!st.HasSupport || !SupportQuery.Revalidate(inp.Chunks, MakeSegment(st.SupportId, inp.Chunks)))
                    {
                        Vector2 wish = inp.BodyPos + Place(stance.TdOffset, dir, inp.Scale);
                        st.HasSupport = TrySelect(inp, treads, wish, inp.BodyPos, maxReach,
                                                  held: -1, out var seg, out var pt, out plan.Reject);
                        if (st.HasSupport) { st.SupportId = seg.Id; st.SupportPoint = pt; }
                    }
                    st.State = st.HasSupport ? FootPlanState.Stance : FootPlanState.Unplanned;
                }
                if (st.HasSupport)
                {
                    // Held stance: fixed point, subject to terrain validity and reach.
                    var seg = MakeSegment(st.SupportId, inp.Chunks);
                    if (!SupportQuery.Revalidate(inp.Chunks, seg))
                    { st.HasSupport = false; plan.Reject = StepReject.NoSupport; }
                    else if ((st.SupportPoint - inp.BodyPos).Length() > maxReach)
                    { st.HasSupport = false; plan.Reject = StepReject.Unreachable; }
                    if (!st.HasSupport) st.State = FootPlanState.Unplanned;
                }
                st.Weight = st.HasSupport ? MathF.Min(1f, st.Weight + engage)
                                          : MathF.Max(0f, st.Weight - release);
                plan.State = st.State = st.HasSupport ? FootPlanState.Stance : FootPlanState.Unplanned;
                plan.Target = st.SupportPoint;
                plan.Preferred = inp.BodyPos + Place(stance.TdOffset, dir, inp.Scale);
                if (st.HasSupport) { plan.Support = MakeSegment(st.SupportId, inp.Chunks); plan.HasSupport = true; }
            }
            else if (swingIdx >= 0)
            {
                var swing = ft.Swings[swingIdx];
                var landStance = ft.Stances[(swingIdx + 1) % ft.Stances.Length];
                if (st.State != FootPlanState.Swing)
                {
                    // Liftoff (or a cold start mid-swing): remember where the foot actually
                    // was — the swing's takeoff. From stance that is the planted point; from
                    // anything else, the authored swing start at the current body.
                    st.Takeoff = st.State == FootPlanState.Stance && st.HasSupport
                        ? st.SupportPoint
                        : inp.BodyPos + Place(SampleAuthored(ft, swingIdx, 0f), dir, inp.Scale);
                    st.HasSupport = false;   // reused below as "landing selected"
                }
                st.State = FootPlanState.Swing;
                st.Weight = MathF.Max(0f, st.Weight - release);

                // Predict the body at authored touchdown and place the authored offset there.
                float rate = MathF.Max(inp.NominalRate, 0.25f);
                float dtTd = MathHelper.Clamp((1f - plan.SwingU) * (swing.End - swing.Start) / rate,
                                              0f, MaxLookahead);
                Vector2 bodyAtTd = inp.PredictAt?.Invoke(dtTd) ?? (inp.BodyPos + inp.BodyVel * dtTd);
                plan.Preferred = bodyAtTd + Place(landStance.TdOffset, dir, inp.Scale);

                // Select (or keep) the landing. Late in the swing a still-valid target is
                // frozen; otherwise reselect each frame with hysteresis toward the held tread.
                bool heldValid = st.HasSupport
                    && SupportQuery.Revalidate(inp.Chunks, MakeSegment(st.SupportId, inp.Chunks))
                    && (st.SupportPoint - bodyAtTd).Length() <= maxReach;
                if (plan.SwingU > cfg.PlannerLateSwingLock && heldValid)
                {
                    plan.HasSupport = true;
                }
                else
                {
                    plan.HasSupport = TrySelect(inp, treads, plan.Preferred, bodyAtTd, maxReach,
                                                held: st.HasSupport ? st.SupportId : long.MinValue,
                                                out var seg, out var pt, out plan.Reject,
                                                swing, st.Takeoff, dir);
                    if (plan.HasSupport) { st.SupportId = seg.Id; st.SupportPoint = pt; }
                    st.HasSupport = plan.HasSupport;
                }

                if (plan.HasSupport)
                {
                    plan.Support = MakeSegment(st.SupportId, inp.Chunks);
                    plan.HasSupport = true;
                    plan.Target = Vector2.Lerp(st.Takeoff, st.SupportPoint, plan.SwingU)
                                  + Place(Residual(swing, plan.SwingU), dir, inp.Scale);
                }
                else
                {
                    // Fallback: the authored motion at the current body — degrade to the
                    // clip rather than fabricating support (design doc's fallback policy).
                    plan.Target = inp.BodyPos + Place(SampleAuthored(ft, swingIdx, plan.SwingU), dir, inp.Scale);
                }
                plan.State = plan.HasSupport ? FootPlanState.Swing : FootPlanState.Unplanned;
            }
            else
            {
                // Outside every stance and swing (non-loop tail, malformed phase): no plan.
                st.State = FootPlanState.Unplanned;
                st.HasSupport = false;
                st.Weight = MathF.Max(0f, st.Weight - release);
                plan.State = FootPlanState.Unplanned;
            }

            plan.Weight = st.Weight;
            Plans[fi] = plan;
        }
    }

    // ---- helpers -------------------------------------------------------------

    private static Vector2 Place(Vector2 rigOffset, int dir, float scale)
        => new(dir * scale * rigOffset.X, scale * rigOffset.Y);

    private static float Wrap(float x) => x - MathF.Floor(x);

    private static int FindStance(FootStrideTrack ft, float phase, out float u) => ft.StanceAt(phase, out u);
    private static int FindSwing(FootStrideTrack ft, float phase, out float u)  => ft.SwingAt(phase, out u);

    // A CLIP SWITCH (ANIMATION_OWNERSHIP_CONTRACT.md §4, planner P4): rebind to the incoming
    // clip's track. Feet the caller transferred (`adopted`: bone, world support point, weight)
    // keep their stance support — the planner takes the point as its fixed support on the
    // tread that carries it — so a run → walk switch mid-stance does not re-select and pop.
    // Everything else starts Unplanned (a foot never transfers into an incoming swing: the
    // caller only offers feet that are in stance at the entry phase).
    public void Rebind(ClipStrideTrack track, ReadOnlySpan<(int Bone, Vector2 Target, float Weight)> adopted, ChunkMap chunks)
    {
        Reset();
        if (track == null || chunks == null) return;
        FeetCount = Math.Min(track.Feet.Length, MaxFeet);
        Span<SupportSegment> treads = stackalloc SupportSegment[MaxTreads];
        for (int fi = 0; fi < FeetCount; fi++)
        {
            int bone = track.Feet[fi].Bone;
            foreach (var a in adopted)
            {
                if (a.Bone != bone) continue;
                int n = SupportQuery.QueryTreads(chunks, a.Target, Chunk.TileSize, treads);
                for (int i = 0; i < n; i++)
                {
                    var t = treads[i];
                    if (a.Target.X < t.X0 || a.Target.X > t.X1 || MathF.Abs(t.Y - a.Target.Y) > SolveProblem.ContactSupportBand) continue;
                    _feet[fi] = new FootState { State = FootPlanState.Stance, HasSupport = true,
                                                SupportId = t.Id, SupportPoint = a.Target, Weight = a.Weight };
                    Plans[fi] = new FootPlan { Bone = bone, State = FootPlanState.Stance, Target = a.Target,
                                               Support = t, HasSupport = true, Weight = a.Weight };
                    break;
                }
                break;
            }
        }
    }

    // The authored body-relative offset at swing progress u (chord between the authored
    // endpoints + residual) — the no-plan fallback and the takeoff fallback.
    private static Vector2 SampleAuthored(FootStrideTrack ft, int swingIdx, float u)
    {
        var prev = ft.Stances[swingIdx].LoOffset;
        var next = ft.Stances[(swingIdx + 1) % ft.Stances.Length].TdOffset;
        return Vector2.Lerp(prev, next, u) + Residual(ft.Swings[swingIdx], u);
    }

    private static Vector2 Residual(in StrideSwing swing, float u)
    {
        var r = swing.Residuals;
        float x = MathHelper.Clamp(u, 0f, 1f) * (r.Length - 1);
        int i = Math.Min((int)x, r.Length - 2);
        return Vector2.Lerp(r[i], r[i + 1], x - i);
    }

    private static bool SwingBlocked(in StrideSwing swing, Vector2 takeoff, Vector2 landing,
                                     int dir, float scale, ChunkMap chunks)
    {
        // Sample the reconstructed path's interior; a sample inside a solid cell blocks it.
        // (Toe-point check only in this slice — shin/knee clearance arrives with the joint
        // limits, Plans/ANIMATION_STEP_PLANNER_IMPL.md P3.)
        int n = ClipStrideTrack.SwingSampleCount;
        float ts = Chunk.TileSize;
        for (int i = 1; i < n - 1; i++)
        {
            float u = i / (float)(n - 1);
            Vector2 p = Vector2.Lerp(takeoff, landing, u) + Place(Residual(swing, u), dir, scale);
            // Probe above the toe path by a sub-tile tolerance: both endpoints SIT on
            // tread tops, so a low authored swing legitimately grazes — or dips a few
            // px into — the surface line, which is the solve's ground-hold/δ business,
            // not a blocked landing. Only tile-scale obstruction (a real block in the
            // way, ≥ TileSize) should reject the plan.
            if (chunks.GetCellState((int)MathF.Floor(p.X / ts), (int)MathF.Floor((p.Y - SwingProbeLift) / ts))
                == TileState.Solid) return true;
        }
        return false;
    }

    // Pick the best feasible tread around `wish`: lowest |clamped point − wish|, a
    // hysteresis bonus for the held tread, hard reach gate against `bodyRef`, and —
    // when a swing is in flight (`swing` non-null) — a swing-path clearance gate, so
    // an over-ambitious wish degrades to the best CLEARABLE tread rather than to no
    // plan at all.
    private bool TrySelect(in PlannerInputs inp, Span<SupportSegment> treads, Vector2 wish,
                           Vector2 bodyRef, float maxReach, long held,
                           out SupportSegment best, out Vector2 point, out StepReject reject,
                           StrideSwing? swing = null, Vector2 takeoff = default, int dir = 1)
    {
        best = default; point = default;
        int n = SupportQuery.QueryTreads(inp.Chunks, wish, QueryRadius, treads);
        if (n == 0) { reject = StepReject.NoSupport; return false; }
        float hysteresis = AnimSolverConfig.Current.PlannerHysteresis;
        float bestScore = float.MaxValue;
        bool anyInReach = false, anyClear = false;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = treads[i].Clamp(wish.X);
            if ((p - bodyRef).Length() > maxReach) continue;
            anyInReach = true;
            float score = (p - wish).Length() - (treads[i].Id == held ? hysteresis : 0f);
            if (score >= bestScore) continue;
            if (swing.HasValue && SwingBlocked(swing.Value, takeoff, p, dir, inp.Scale, inp.Chunks))
                continue;
            anyClear = true;
            bestScore = score; best = treads[i]; point = p;
        }
        reject = !anyInReach ? StepReject.Unreachable
               : !anyClear && swing.HasValue ? StepReject.SwingBlocked
               : StepReject.None;
        return swing.HasValue ? anyClear : anyInReach;
    }

    private static SupportSegment MakeSegment(long id, ChunkMap chunks)
    {
        var (gtx, gty) = SupportSegment.UnpackId(id);
        float ts = Chunk.TileSize;
        return new SupportSegment { X0 = gtx * ts, X1 = (gtx + 1) * ts, Y = gty * ts, Id = id };
    }
}
