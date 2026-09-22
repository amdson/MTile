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
// Knob budget (AnimSolverConfig): PlannerEnabled, PlannerHysteresis, PlannerLateSwingLock,
// PlannerReplanDistance. Weight ramps reuse ContactEngageTime/ContactReleaseTime.
//
// CONTINUOUS REPLANNING (runtime plan §6, workplan chunk 6): a swing COMMITS its landing on
// first selection and keeps it. It replans only for a material change — the predicted
// landing wish moved more than PlannerReplanDistance from the wish it committed at (before
// the late lock), the committed path became obstructed, or the support died / fell out of
// reach. The emitted swing target stays continuous across every source change (replan,
// fall back to the clip, re-acquire): the new nominal path is offset by the gap to the
// carried position (last target + last velocity) and that offset decays to zero at
// touchdown, so the path bends toward the new landing instead of jumping. Candidate landings
// are gated by the clearance of the REPLACEMENT path — from where the foot is now, over the
// remaining swing — not the path from takeoff.

public enum FootPlanState { Unplanned, Swing, Stance }

public enum StepReject { None, NoSupport, Unreachable, SwingBlocked }

// Why a swing's target source changed this frame (diagnostics; None on a quiet frame).
public enum StepReplan { None, Prediction, Obstruction, InvalidSupport, Fallback, Reacquired }

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
    public Vector2       Landing;    // swing: the committed landing point (valid when HasSupport)
    public StepReplan    Replan;     // swing: the source change that happened this frame
    public int           Replans;    // swing: source changes since liftoff
    public int           Shape;      // swing: index into StepPlanner.Shapes (0 = the authored path as is)
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
    public LoopBackPlan? LoopBack;          // optional same-clip continuation for stance lookahead
}

public sealed class StepPlanner
{
    public const int MaxFeet = 4;
    private const int MaxTreads = 32;
    private const float QueryRadius = 2.5f * Chunk.TileSize;   // around the preferred landing
    private const float MaxLookahead = 0.6f;                   // s — cap on touchdown prediction
    private const float SwingProbeLift = 3f;                   // px — sub-tile dip tolerance in the swing check
    private const float MaxLandingMiss = 1.5f * Chunk.TileSize; // px — a tread farther than this from the wish is no plan

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
        public Vector2 CommitWish;     // swing: the landing wish the current commitment was made at
        public Vector2 Blend;          // swing: continuity offset captured at the last source change…
        public float   BlendU0;        // …at this progress; decays linearly to 0 at u = 1
        public Vector2 LastTarget, LastVel;   // last emitted swing target and its per-frame step
        public bool    HasLast, HasLastVel;
        public int     Replans;
        public int     Shape;          // the committed landing's path shape (Shapes index)
    }

    // SWING SHAPES (workplan chunk 7, runtime plan §7 — authored intent mapped to the real
    // obstacle): the authored swing residual rides a chord from takeoff to landing. When that
    // path is obstructed — a foot planted at the base of a riser cannot travel forward before
    // it has risen — the chord is reshaped, in this order, until the toe path clears:
    //   K     front-loads the chord's RISE (a rising landing only): r(u) = 1 − (1 − u)^K,
    //         so most of the climb happens before most of the run (the knee lifts first);
    //   Bump  adds an extra lift of Bump·TileSize·sin(πu) over the whole swing.
    // Shape 0 is the plain reconstruction; nothing else changes for a flat step. The chosen
    // shape is committed with the landing and reported in FootPlan.Shape.
    public static readonly (float K, float BumpTiles)[] Shapes = { (1f, 0f), (6f, 0f), (6f, 0.5f), (6f, 1f) };

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
                float remaining = StanceTime(inp, ft, stanceIdx, inp.Phase);
                var reach = PredictReach(inp, 0, remaining);
                // Establish the plant during the first half of even a short stance.
                float engage = inp.Dt / MathF.Max(inp.Dt,
                    MathF.Min(cfg.ContactEngageTime, remaining * .5f));
                if (st.State != FootPlanState.Stance)
                {
                    // Touchdown: land on the support selected during swing; if none (or it
                    // died), try an immediate selection around the authored offset — do not
                    // pin a distant point just because phase crossed the event.
                    if (!st.HasSupport || !SupportQuery.Revalidate(inp.Chunks, MakeSegment(st.SupportId, inp.Chunks))
                        || !reach.Contains(st.SupportPoint, maxReach))
                    {
                        Vector2 wish = inp.BodyPos + Place(stance.TdOffset, dir, inp.Scale);
                        st.HasSupport = TrySelect(inp, treads, wish, reach, maxReach,
                                                  held: -1, out var seg, out var pt, out plan.Reject,
                                                  corner: stance.Corner);
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
                    st.HasSupport = false;   // reused below as "landing committed"
                    st.Blend = Vector2.Zero; st.BlendU0 = 0f;
                    st.HasLast = st.HasLastVel = false;
                    st.Replans = 0; st.Shape = 0;
                }
                st.State = FootPlanState.Swing;
                st.Weight = MathF.Max(0f, st.Weight - release);
                float u = plan.SwingU;

                // Predict the body at authored touchdown and place the authored offset there.
                float rate = MathF.Max(inp.NominalRate, 0.25f);
                float dtTd = MathHelper.Clamp((1f - u) * (swing.End - swing.Start) / rate,
                                              0f, MaxLookahead);
                Vector2 bodyAtTd = inp.PredictAt?.Invoke(dtTd) ?? (inp.BodyPos + inp.BodyVel * dtTd);
                var reach = PredictReach(inp, dtTd,
                    StanceTime(inp, ft, (swingIdx + 1) % ft.Stances.Length, landStance.Touchdown));
                plan.Preferred = bodyAtTd + Place(landStance.TdOffset, dir, inp.Scale);

                // Where the foot is now (the carried position: last target plus its per-frame
                // step) — the start of any replacement path, and the anchor of the continuity
                // offset. It needs a velocity: one frame of history alone would place the foot
                // one step behind the path (a fast-rising shape moves 3 px in that frame) and
                // read as an obstruction. Until then the whole reconstruction is judged.
                bool    haveCarried = st.HasLastVel;
                Vector2 carried = haveCarried ? st.LastTarget + st.LastVel : st.Takeoff;
                float   fromU   = haveCarried ? u : 0f;
                bool wasCommitted = st.HasSupport;
                var replan = StepReplan.None;

                // 1. Is the commitment still good? Invalid support / out of reach drop it;
                //    obstruction of the committed path or a material wish change reselect.
                bool reselect = !wasCommitted;
                if (wasCommitted)
                {
                    if (!SupportQuery.Revalidate(inp.Chunks, MakeSegment(st.SupportId, inp.Chunks)))
                    { st.HasSupport = false; reselect = true; replan = StepReplan.InvalidSupport; plan.Reject = StepReject.NoSupport; }
                    else if (!reach.Contains(st.SupportPoint, maxReach))
                    { st.HasSupport = false; reselect = true; replan = StepReplan.InvalidSupport; plan.Reject = StepReject.Unreachable; }
                    else if (SwingBlocked(swing, st.Takeoff, st.SupportPoint, dir, inp.Scale, inp.Chunks, fromU, carried, st.Shape))
                    { reselect = true; replan = StepReplan.Obstruction; }
                    else if (u <= cfg.PlannerLateSwingLock
                             && (plan.Preferred - st.CommitWish).Length() > cfg.PlannerReplanDistance)
                    { reselect = true; replan = StepReplan.Prediction; }
                }

                // 2. (Re)select: hysteresis toward the held tread; candidates gated by the
                //    clearance of the path from the carried position. A reselect that lands on
                //    the same point is a quiet re-commit, not a replan.
                if (reselect)
                {
                    bool found = TrySelect(inp, treads, plan.Preferred, reach, maxReach,
                                           held: st.HasSupport ? st.SupportId : long.MinValue,
                                           out var seg, out var pt, out var rej, out int shape,
                                           swing, st.Takeoff, dir, fromU, carried, landStance.Corner);
                    if (found)
                    {
                        bool same = st.HasSupport && seg.Id == st.SupportId && (pt - st.SupportPoint).LengthSquared() < 1e-6f
                                    && shape == st.Shape;
                        if (!same && replan == StepReplan.None) replan = wasCommitted ? StepReplan.Prediction : StepReplan.Reacquired;
                        if (same) replan = StepReplan.None;
                        st.SupportId = seg.Id; st.SupportPoint = pt; st.HasSupport = true; st.Shape = shape;
                        st.CommitWish = plan.Preferred;
                        plan.Reject = StepReject.None;
                    }
                    else
                    {
                        // Nothing clearable elsewhere: an obstructed commitment drops to the
                        // clip's motion; a merely-moved wish keeps the commitment it has.
                        if (replan == StepReplan.Obstruction) st.HasSupport = false;
                        if (st.HasSupport || !wasCommitted) replan = StepReplan.None;
                        else if (replan != StepReplan.InvalidSupport) replan = StepReplan.Fallback;
                        if (!st.HasSupport && plan.Reject == StepReject.None) plan.Reject = rej;
                    }
                }
                plan.HasSupport = st.HasSupport;

                // 3. The nominal path of the current source, and the continuity offset: a
                //    source change re-captures it at the carried position; it fades to zero by
                //    touchdown so the emitted target bends onto the new path.
                Vector2 nominal = plan.HasSupport
                    ? Chord(st.Takeoff, st.SupportPoint, u, st.Shape) + Place(Residual(swing, u), dir, inp.Scale)
                    : inp.BodyPos + Place(SampleAuthored(ft, swingIdx, u), dir, inp.Scale);
                if (replan != StepReplan.None && st.HasLast)
                {
                    if (haveCarried) { st.Blend = carried - nominal; st.BlendU0 = u; }
                    st.Replans++;
                }
                plan.Target = nominal + st.Blend * Decay(u, st.BlendU0);
                if (plan.HasSupport) { plan.Support = MakeSegment(st.SupportId, inp.Chunks); plan.Landing = st.SupportPoint; }
                plan.State = plan.HasSupport ? FootPlanState.Swing : FootPlanState.Unplanned;
                plan.Replan = replan; plan.Replans = st.Replans; plan.Shape = plan.HasSupport ? st.Shape : 0;

                if (st.HasLast) { st.LastVel = plan.Target - st.LastTarget; st.HasLastVel = true; }
                st.LastTarget = plan.Target; st.HasLast = true;
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

    // Linear fade of a continuity offset captured at u0: 1 there, 0 at touchdown.
    private static float Decay(float u, float u0)
        => u0 >= 1f - 1e-4f ? 0f : MathHelper.Clamp((1f - u) / (1f - u0), 0f, 1f);

    // The chord from takeoff to landing at progress u under a shape (see Shapes).
    private static Vector2 Chord(Vector2 takeoff, Vector2 landing, float u, int shape)
    {
        var (k, bump) = Shapes[Math.Clamp(shape, 0, Shapes.Length - 1)];
        Vector2 d = landing - takeoff;
        float ry = u;
        if (k > 1f && d.Y < -1f) ry = 1f - MathF.Pow(1f - u, k);   // rising landing: climb first
        Vector2 p = new(takeoff.X + d.X * u, takeoff.Y + d.Y * ry);
        if (bump > 0f) p.Y -= bump * Chunk.TileSize * MathF.Sin(MathF.PI * u);
        return p;
    }

    // The path a swing follows from progress u0 (where the foot is at `from`) to `landing`:
    // the chord+residual reconstruction, offset by the gap at u0 and its fade. At u0 = 0 with
    // `from` = takeoff this is the plain reconstruction.
    private static Vector2 PathAt(in StrideSwing swing, Vector2 takeoff, Vector2 landing, int dir, float scale,
                                  float u, float u0, Vector2 from, int shape)
    {
        Vector2 nominal = Chord(takeoff, landing, u, shape) + Place(Residual(swing, u), dir, scale);
        if (u0 <= 0f) return nominal;
        Vector2 at0 = Chord(takeoff, landing, u0, shape) + Place(Residual(swing, u0), dir, scale);
        return nominal + (from - at0) * Decay(u, u0);
    }

    private readonly record struct StanceReach(Vector2 Start, Vector2 Middle, Vector2 End)
    {
        public bool Contains(Vector2 point, float radius) =>
            Vector2.DistanceSquared(point, Start) <= radius * radius &&
            Vector2.DistanceSquared(point, Middle) <= radius * radius &&
            Vector2.DistanceSquared(point, End) <= radius * radius;
    }

    private static StanceReach PredictReach(in PlannerInputs inp, float touchdown, float duration)
    {
        // Without a terrain-aware trajectory, velocity extrapolation is only the existing
        // touchdown estimate. Extending it through stance can predict motion through walls
        // or off ledges and reject otherwise valid plants in lightweight callers.
        if (inp.PredictAt == null)
        {
            var atTouchdown = inp.BodyPos + inp.BodyVel * touchdown;
            return new StanceReach(atTouchdown, atTouchdown, atTouchdown);
        }
        float mid = MathF.Min(MaxLookahead, touchdown + duration * .5f);
        float end = MathF.Min(MaxLookahead, touchdown + duration);
        return new StanceReach(
            inp.PredictAt?.Invoke(touchdown) ?? inp.BodyPos + inp.BodyVel * touchdown,
            inp.PredictAt?.Invoke(mid) ?? inp.BodyPos + inp.BodyVel * mid,
            inp.PredictAt?.Invoke(end) ?? inp.BodyPos + inp.BodyVel * end);
    }

    private static float StanceTime(in PlannerInputs inp, FootStrideTrack foot, int index, float phase)
    {
        var stance = foot.Stances[index];
        if (stance.Persistent) return AnimSolverConfig.Current.ContactEngageTime * 2;
        float unwrapped = phase < stance.Touchdown ? phase + 1 : phase;
        float remaining = MathF.Max(0, stance.Liftoff - unwrapped);
        // A compatible loop-back may extend this plant into an earlier stance interval.
        if (inp.LoopBack is { Jumps: true } loop && loop.Exit >= phase &&
            foot.StanceAt(loop.Exit, out _) == index)
        {
            int entry = foot.StanceAt(loop.Entry, out _);
            if (entry >= 0)
                remaining = MathF.Max(remaining,
                    loop.Exit - phase + foot.Stances[entry].Liftoff - loop.Entry);
        }
        return remaining / MathF.Max(inp.NominalRate, .25f);
    }

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

    // Is the path from progress `u0` (foot at `from`) to `landing` obstructed? Samples the
    // REMAINING path — the one the foot would actually follow — so a mid-swing replacement is
    // judged on its own clearance, not on the takeoff→landing chord it no longer travels.
    private static bool SwingBlocked(in StrideSwing swing, Vector2 takeoff, Vector2 landing,
                                     int dir, float scale, ChunkMap chunks, float u0 = 0f, Vector2 from = default,
                                     int shape = 0)
    {
        // Sample the reconstructed path's interior; a sample inside a solid cell blocks it.
        // (Toe-point check only in this slice — shin/knee clearance arrives with the joint
        // limits, Plans/ANIMATION_STEP_PLANNER_IMPL.md P3.)
        int n = ClipStrideTrack.SwingSampleCount;
        float ts = Chunk.TileSize;
        for (int i = 1; i < n - 1; i++)
        {
            float u = i / (float)(n - 1);
            if (u <= u0) continue;
            Vector2 p = PathAt(swing, takeoff, landing, dir, scale, u, u0, from, shape);
            // Probe above the toe path by a sub-tile tolerance: both endpoints SIT on
            // tread tops, so a low authored swing legitimately grazes — or dips a few
            // px into — the surface line, which is the solve's ground-hold/δ business,
            // not a blocked landing. Only tile-scale obstruction (a real block in the
            // way, ≥ TileSize) should reject the plan.
            if (chunks.GetCellState((int)MathF.Floor(p.X / ts), (int)MathF.Floor((p.Y - SwingProbeLift) / ts))
                == TileState.Solid)
                return true;
        }
        return false;
    }

    // Pick the best feasible tread around `wish`: lowest |clamped point − wish|, a
    // hysteresis bonus for the held tread, hard reach gate against `bodyRef`, and —
    // when a swing is in flight (`swing` non-null) — a swing-path clearance gate, so
    // an over-ambitious wish degrades to the best CLEARABLE tread rather than to no
    // plan at all. A tread more than MaxLandingMiss from the wish is not a plan: the
    // planner adjusts the authored step, it does not invent a different one (on steep
    // stairs the only clearable tread can be several steps up — the clip's own motion
    // plus touchdown selection is the honest answer there).
    private bool TrySelect(in PlannerInputs inp, Span<SupportSegment> treads, Vector2 wish,
                           StanceReach reach, float maxReach, long held,
                           out SupportSegment best, out Vector2 point, out StepReject reject,
                           StrideSwing? swing = null, Vector2 takeoff = default, int dir = 1,
                           float u0 = 0f, Vector2 from = default, bool corner = false)
        => TrySelect(inp, treads, wish, reach, maxReach, held, out best, out point, out reject, out _,
                     swing, takeoff, dir, u0, from, corner);

    // CORNER PLANTS (2026-09-21). A landing whose clamp lands on a tread's lip is a corner
    // plant — the ball of the foot on the edge, which is how every stair stance is authored.
    // It gets the hysteresis-sized bonus so the tie between "this tread's lip" and "the next
    // tread's interior" resolves the same way every frame instead of flipping with the wish.
    // A span tagged Corner (ContactSpan.Corner) goes further: its candidates ARE the treads'
    // exposed corners — the nearest lip wins outright and a tread with no lip is not a plan.
    private bool TrySelect(in PlannerInputs inp, Span<SupportSegment> treads, Vector2 wish,
                           StanceReach reach, float maxReach, long held,
                           out SupportSegment best, out Vector2 point, out StepReject reject, out int shape,
                           StrideSwing? swing, Vector2 takeoff, int dir, float u0, Vector2 from,
                           bool corner = false)
    {
        best = default; point = default; shape = 0;
        int n = SupportQuery.QueryTreads(inp.Chunks, wish, QueryRadius, treads);
        if (n == 0) { reject = StepReject.NoSupport; return false; }
        float hysteresis = AnimSolverConfig.Current.PlannerHysteresis;
        float bestScore = float.MaxValue;
        bool anyInReach = false, anyClear = false;
        for (int i = 0; i < n; i++)
        {
            Vector2 p;
            if (corner) { if (!treads[i].TryNearestCorner(wish.X, out p)) continue; }
            else p = treads[i].Clamp(wish.X);
            if (!reach.Contains(p, maxReach)) continue;
            float miss = (p - wish).Length();
            if (miss > MaxLandingMiss) continue;
            anyInReach = true;
            float score = miss - (treads[i].Id == held ? hysteresis : 0f)
                               - (treads[i].IsCorner(p) ? hysteresis : 0f);
            if (score >= bestScore) continue;
            int sh = 0;
            if (swing.HasValue)
            {
                // The first shape (plain, then progressively lifted) whose toe path clears.
                while (sh < Shapes.Length && SwingBlocked(swing.Value, takeoff, p, dir, inp.Scale, inp.Chunks, u0, from, sh)) sh++;
                if (sh == Shapes.Length) continue;
            }
            anyClear = true;
            bestScore = score; best = treads[i]; point = p; shape = sh;
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
