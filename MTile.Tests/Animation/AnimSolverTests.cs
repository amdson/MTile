using System;
using System.IO;
using Microsoft.Xna.Framework;
using MTile;
using Xunit;

namespace MTile.Tests;

public class AnimSolverTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _o;
    public AnimSolverTests(Xunit.Abstractions.ITestOutputHelper o) => _o = o;

    // --- the least-squares core, in isolation -------------------------------

    // Unconstrained: minimize (x-3)² + (y+1)² → (3, -1). Proves the LM loop, normal
    // equations, and the Cholesky solve on a 2-var / 2-residual problem.
    [Fact]
    public void LeastSquares_FindsUnconstrainedMinimum()
    {
        var solver = new LeastSquaresSolver(maxVars: 4, maxRes: 4);
        int Resid(ReadOnlySpan<float> x, Span<float> r)
        {
            r[0] = x[0] - 3f;
            r[1] = x[1] + 1f;
            return 2;
        }
        var x  = new float[] { 0f, 0f };
        var lo = new float[] { -10f, -10f };
        var hi = new float[] {  10f,  10f };
        float cost = solver.Minimize(Resid, x.AsSpan(0, 2), lo.AsSpan(0, 2), hi.AsSpan(0, 2));

        Assert.Equal(3f, x[0], 3);
        Assert.Equal(-1f, x[1], 3);
        Assert.True(cost < 1e-5f, $"cost {cost}");
    }

    // The box bound must hold the optimum at the wall when the unconstrained min lies
    // outside it: minimize (x-5)² with x ∈ [0,1] → x = 1.
    [Fact]
    public void LeastSquares_RespectsBox()
    {
        var solver = new LeastSquaresSolver(maxVars: 2, maxRes: 2);
        int Resid(ReadOnlySpan<float> x, Span<float> r) { r[0] = x[0] - 5f; return 1; }
        var x  = new float[] { 0f };
        float _ = solver.Minimize(Resid, x.AsSpan(0, 1),
                                  new float[] { 0f }, new float[] { 1f });
        Assert.Equal(1f, x[0], 3);
    }

    // --- Phase 2: angle corrections + pose prior -----------------------------

    // Well-posedness of the Δθ channel under in-solve smoothing (polish item 1 changed this
    // test's premise). Δθ is no longer ~0 on unconstrained bones: the smoothness rows use it
    // to EASE every bone's deviation, and with the timing stage owning the phase (chunk 5)
    // the planted foot's residual stance mismatch lands in a small leg trim that rises and
    // falls with every stance. So the proof is: corrections stay BOUNDED (well inside the
    // box — no drift to the wall = still well-posed) and do NOT ACCUMULATE (the stance
    // pattern repeats at the same amplitude cycle after cycle; a growing offset would show
    // as the later cycles' peak exceeding the earlier ones').
    [Theory]
    [InlineData("walk", 25f, +1)]
    [InlineData("walk", 25f, -1)]
    [InlineData("run",  90f, +1)]
    [InlineData("run",  90f, -1)]
    public void Solver_AngleCorrections_StayBoundedAndDecay_InSteadyLocomotion(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[] { clip });

        const int frames = 90;
        float dt = 1f / 30f, vx = speed * facing, x = 0f, maxAll = 0f;
        float prev = anim.State.Phase, totalPhase = 0f;
        float earlyPeak = 0f, latePeak = 0f;   // max |Δθ| over the middle third vs the last third
        for (int i = 0; i < frames; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt));
            float frameMax = 0f; int maxBone = -1;
            for (int b = 0; b < anim.Skeleton.Count; b++)
                if (MathF.Abs(anim.AngleCorrection(b)) > frameMax) { frameMax = MathF.Abs(anim.AngleCorrection(b)); maxBone = b; }
            maxAll = MathF.Max(maxAll, frameMax);
            _o.WriteLine($"f{i,2}: max|Δθ| {frameMax:0.0000} ({(maxBone >= 0 ? anim.Skeleton.Bones[maxBone].Name : "-")})  Δφ {anim.PhaseStep:0.0000} φ {anim.State.Phase:0.000}  d=({anim.HorizontalOffset:0.00},{anim.VerticalOffset:0.00})  contacts {anim.ContactCount}");
            if (i >= frames / 3 && i < 2 * frames / 3) earlyPeak = MathF.Max(earlyPeak, frameMax);
            if (i >= 2 * frames / 3)                   latePeak  = MathF.Max(latePeak,  frameMax);
            float p = anim.State.Phase, d = p - prev; if (d < -0.5f) d += 1f;
            totalPhase += d; prev = p;
        }

        // The cadence path actually ran (phase advanced — not a vacuous all-flight pass)...
        Assert.True(totalPhase > 0.2f, $"cadence didn't advance ({totalPhase:0.000})");
        // ...corrections stay well inside the box (no drift to the wall — well-posed)...
        float box = AnimSolverConfig.Current.AngleCorrLimit;
        Assert.True(maxAll < 0.5f * box, $"max |Δθ| = {maxAll:0.0000} rad — approaching the box ({box})");
        // ...and they do not ACCUMULATE: the last cycles peak no higher than the earlier ones.
        Assert.True(latePeak <= 1.25f * earlyPeak + 0.01f,
            $"|Δθ| peak grew from {earlyPeak:0.0000} to {latePeak:0.0000} rad — corrections accumulating?");
    }

    // --- Phase 3: solved vertical offset δ (ComOffset + vertical ground) -----

    // δ is the body's vertical bob: a hard per-contact ground row holds the planted foot
    // at its plant height (δ ≠ 0), and a soft com row pulls δ → 0 when no foot pins it.
    // Run has both stance and no-contact (flight) windows, so over a cycle δ must BOTH
    // engage (stance) and release (flight → body eases back to the com baseline, both feet
    // free to leave the ground), and never pin to the box. Release is an EASE, not a snap
    // (2026-08-26): on a no-solve frame δ decays toward 0 by the base ease factor — |δ| is
    // non-increasing across every flight frame — instead of reading 0 the instant the
    // contact drops (the one-frame root pop at each stride's stance → flight edge).
    [Theory]
    [InlineData("run", 90f, +1)]
    [InlineData("run", 90f, -1)]
    public void Solver_VerticalOffset_EngagesInStance_ReleasesInFlight(string clipName, float speed, int facing)
    {
        var clip = AnimationStore.LoadAll(StatesDir()).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{clipName}.json not found");
        var skel = SkeletonExamples.Biped();
        var anim = new CharacterAnimator(skel, 0.6f, new[] { clip });

        float dt = 1f / 30f, vx = speed * facing, x = 0f, maxAbs = 0f, prevAbs = 0f;
        int flightFrames = 0, stanceBobFrames = 0, flightGrew = 0;
        for (int i = 0; i < 60; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState", "", dt));
            float d = anim.VerticalOffset, a = MathF.Abs(d);
            maxAbs = MathF.Max(maxAbs, a);
            bool flight = !anim.SolvedThisFrame;               // no solve this frame → flight, easing to baseline
            if (flight)
            {
                flightFrames++;
                if (a > prevAbs + 1e-4f) flightGrew++;         // must decay, never grow, while easing
            }
            else if (a > 0.05f) stanceBobFrames++;             // foot pinned → body bobbed off baseline
            prevAbs = a;
        }

        Assert.True(flightFrames > 0, "δ never released — no flight frame (body always pinned)");
        Assert.Equal(0, flightGrew);
        Assert.True(stanceBobFrames > 0, "δ never engaged — the vertical ground hold did nothing");
        Assert.True(maxAbs < 23f, $"δ pinned near the box wall ({maxAbs:0.0} px) — over-correcting");
    }

    // --- Phase 5: analytic Jacobian (replaces finite differences) ------------

    // The cadence solve now drives LM with the closed-form §3.3 Jacobian instead of finite
    // differences. The two must agree wherever the residual is differentiable: this runs the
    // solver path over a stride and, each frame a solve ran, compares the analytic Jacobian
    // to a central finite difference of the same residual (the Δφ column is skipped only at a
    // keyframe boundary, where ∂/∂φ genuinely jumps — the §3.5 kink). Sign of the facing-flip
    // lever arm is covered by running both directions. A wrong column shows up as O(1) error.
    //
    // biped_rabbit clips animate Stretch (the hip struts foreshorten, and swing past the depth
    // axis to negative length), so their sampled pose moves by local TRANSLATION as well as
    // rotation — the Δφ column must carry that channel too. The overlay rows paint an upper-
    // body slash over the run, so the base-clip columns carry the Π(1−w) attenuation.
    [Theory]
    [InlineData("biped",        "walk", 25f, +1, null)]
    [InlineData("biped",        "walk", 25f, -1, null)]
    [InlineData("biped",        "run",  90f, +1, null)]
    [InlineData("biped",        "run",  90f, -1, null)]
    [InlineData("biped_rabbit", "walk", 25f, +1, null)]
    [InlineData("biped_rabbit", "walk", 25f, -1, null)]
    [InlineData("biped_rabbit", "run",  90f, +1, null)]
    [InlineData("biped_rabbit", "run",  90f, -1, null)]
    [InlineData("biped_rabbit", "run",  90f, +1, "groundslash1")]
    [InlineData("biped_rabbit", "run",  90f, -1, "groundslash1")]
    public void Solver_AnalyticJacobian_MatchesFiniteDifference(string rig, string clipName, float speed,
                                                                int facing, string overlayName)
    {
        var clips = AnimationStore.LoadAll(StatesDir(rig));
        var clip = clips.Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{rig}/{clipName}.json not found");
        var overlay = overlayName == null ? null : clips.Find(d => d.Name == overlayName);
        Assert.True(overlayName == null || overlay != null, $"{rig}/{overlayName}.json not found");
        var skel = SkeletonExamples.Load(rig);
        var anim = new CharacterAnimator(skel, 0.6f, overlay == null ? new[] { clip } : new[] { clip, overlay });

        float dt = 1f / 30f, vx = speed * facing, x = 0f, worst = 0f;
        int checks = 0, wc = -1, wr = -1; float wfd = 0f, wan = 0f;
        for (int i = 0; i < 60; i++)
        {
            x += vx * dt;
            anim.Update(new CharacterAnimSample(
                new Vector2(x, 0f), new Vector2(vx, 0f), facing, true, "WalkState",
                overlay?.Type ?? "", dt, actionProgress: overlay == null ? -1f : (i % 30) / 30f));
            float e = anim.MaxJacobianError();
            if (e >= 0f) { checks++; if (e > worst) { worst = e; wc = anim.DbgWorstCol; wr = anim.DbgWorstRow; wfd = anim.DbgFd; wan = anim.DbgAnal; } }
        }

        Assert.True(checks > 0, "no cadence solve ran — nothing validated");
        // Relative agreement: ~0.1% is the float32 oracle's noise floor; a real structural
        // error in any column would be orders of magnitude larger.
        Assert.True(worst < 5e-3f, $"analytic Jacobian disagrees with finite differences by {worst:0.000000} (rel) at col {wc} row {wr} (fd {wfd:0.0000} vs anal {wan:0.0000})");
    }

    // --- One placement model (runtime plan §3 / workplan chunk 2) -----------------------

    // The pose the solver optimized must be the pose drawn, IN WORLD SPACE: on every solve
    // frame each solved tip (BuildSolvePose at the accepted x, plus d) equals the rendered tip
    // under the host's RigRoot. Two ways this used to break: the solve root anchored com at the
    // ENTRY phase while the draw samples it at the advanced phase (run: com.Y bobs with phase),
    // and the solve root ignored com.X while the draw subtracts it (rabbit crouchwalk authors a
    // moving com.X). The FD oracle rides along — the root's ∂/∂φ term is only right if the
    // anchor derivative is.
    [Theory]
    [InlineData("biped",        "run",        90f, +1, false)]
    [InlineData("biped",        "run",        90f, -1, false)]
    [InlineData("biped_rabbit", "run",        90f, +1, false)]
    [InlineData("biped_rabbit", "crouchwalk", 40f, +1, true)]
    [InlineData("biped_rabbit", "crouchwalk", 40f, -1, true)]
    public void Solver_SolvedPose_IsTheRenderedPose(string rig, string clipName, float speed, int facing, bool crouch)
    {
        const float scale = 0.6f;
        var clip = AnimationStore.LoadAll(StatesDir(rig)).Find(d => d.Name == clipName);
        Assert.True(clip != null, $"{rig}/{clipName}.json not found");
        var anim = new CharacterAnimator(SkeletonExamples.Load(rig), scale, new[] { clip });

        float dt = 1f / 60f, vx = speed * facing, x = 0f, worstGap = 0f, worstJac = 0f;
        int solves = 0; string jacWhere = "";
        for (int i = 0; i < 90; i++)
        {
            x += vx * dt;
            var pos = new Vector2(x, 0f);
            anim.Update(new CharacterAnimSample(pos, new Vector2(vx, 0f), facing, true,
                crouch ? "CrouchedState" : "WalkState", "", dt, tag: crouch ? AnimTag.Crouch : AnimTag.None));
            if (!anim.SolvedThisFrame) continue;
            solves++;
            var root = Affine2.FromTRS(AttackGlowSystem.RigRoot(pos, facing, anim, scale), 0f,
                                       new Vector2(facing * scale, scale));
            var world = anim.Pose.ComputeWorld(root);
            for (int b = 0; b < anim.Skeleton.Count; b++)
                worstGap = MathF.Max(worstGap, (world[b].Translation - anim.SolvedBoneTipWorld(b)).Length());
            float e = anim.MaxJacobianError();
            if (e > worstJac) { worstJac = e; jacWhere = $"col {anim.DbgWorstCol} row {anim.DbgWorstRow} [{anim.DbgWorstBlock}] fd {anim.DbgFd:0.0000} vs anal {anim.DbgAnal:0.0000} at frame {i}"; }
        }

        Assert.True(solves > 0, "no solve ran — nothing validated");
        Assert.True(worstGap < 1e-2f, $"solved vs rendered tip disagree by {worstGap:0.0000} px");
        Assert.True(worstJac < 5e-3f, $"analytic Jacobian disagrees with finite differences by {worstJac:0.000000} (rel): {jacWhere}");
    }

    private static string StatesDir(string rig = "biped")
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string c = Path.Combine(d.FullName, "SkeletonStates", rig);
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return "SkeletonStates/" + rig;
    }
}
