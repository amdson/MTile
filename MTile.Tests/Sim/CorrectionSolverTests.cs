using System;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// Solver oracle suite (BALLISTIC_CORRECTOR_PLAN build step 3): analytic special
// cases with closed-form optima. These are the permanent debugging oracle for the
// QP — if a solver change breaks one, the change is wrong, not the test.
public class CorrectionSolverTests
{
    private const float Dt = 1f / 60f;

    private static readonly Vector2 Up = new(0f, -1f);

    private static CorrectionProblem Problem(int h, ChannelDef[] channels, ClearanceRow[] rows,
                                             float deltaWeight = 0f, float hinge = 1e6f,
                                             Vector2[] coastVel = null, int iterations = CorrectionSolver.DefaultInnerIterations)
    {
        var cv = coastVel ?? new Vector2[h];
        return new CorrectionProblem
        {
            H = h, Dt = Dt,
            CoastVel = cv,
            Rows = rows, RowCount = rows.Length,
            Channels = channels, ChannelCount = channels.Length,
            PrevApplied = new Vector2[channels.Length],
            DeltaWeight = deltaWeight, HingeWeight = hinge,
            InnerIterations = iterations,
        };
    }

    private static (Vector2[] z, float residual) Solve(CorrectionProblem p)
    {
        var z = new Vector2[p.ChannelCount * p.H];
        var scratch = new Vector2[z.Length];
        float r = CorrectionSolver.Solve(p, z, scratch);
        return (z, r);
    }

    [Fact]
    public void SingleRow_SingleForceRoot_UniformProfileOnConstraintSurface()
    {
        // One force channel, one row at T. Under the per-variable row-sum
        // preconditioner the first step lands ON the constraint surface with a
        // near-CONSTANT profile: every variable steps g_k/L_k = m/S (S = the
        // row's total lever mass), the hinge dies at slack 0, and the
        // vestigial ‖z‖² weight walks the surface toward min-norm too slowly
        // to matter at any real budget. Both profiles clear the row exactly;
        // the uniform one is the solver's honest output (and is what kills
        // bang-bang structurally — see the wΔ oracle). Pinned here: the row is
        // achieved, and the profile is uniform at amplitude m/S.
        const int H = 10, T = 9;
        const float m = 4f;
        var p = Problem(H,
            new[] { new ChannelDef { Lever = LeverKind.Force, Weight = 1e-4f, Cap = 1e9f, ActiveFrom = 0, ActiveTo = H } },
            new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = m, HingeScale = 1f } });

        var (z, residual) = Solve(p);

        float S = 0f;
        for (int k = 0; k <= T; k++) S += (T - k + 1) * Dt * Dt;
        float uniform = m / S;
        for (int k = 0; k <= T; k++)
        {
            float got = Vector2.Dot(z[k], Up);
            Assert.True(MathF.Abs(got - uniform) < 0.05f * uniform + 1e-3f,
                $"k={k}: {got} vs uniform m/S = {uniform}");
        }
        Assert.True(residual < 0.01f * m, $"residual {residual}");
    }

    [Fact]
    public void SingleTickVelocityRoot_NeededDeltaV_IsDepthOverLever()
    {
        // Velocity root active only at tick 0, row at T: δv·n̂ = m / ((T+1)·dt).
        const int H = 6, T = 5;
        const float m = 2f;
        var p = Problem(H,
            new[] { new ChannelDef { Lever = LeverKind.VelocityUpdate, Weight = 1e-4f, Cap = 1e9f, ActiveFrom = 0, ActiveTo = 1 } },
            new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = m, HingeScale = 1f } });

        var (z, residual) = Solve(p);

        float lever = (T + 1) * Dt;
        float expected = m / lever;   // 20 px/s
        float got = Vector2.Dot(z[0], Up);
        Assert.True(MathF.Abs(got - expected) < 0.02f * expected, $"{got} vs {expected}");
        Assert.True(residual < 0.01f * m, $"residual {residual}");
    }

    [Fact]
    public void SpreadCorrection_PeakAmplitude_NearTwoMOverTcSquared()
    {
        // Spreading a depth-m clearance over t_c of constant acceleration needs
        // a = 2m/t_c²; the min-norm triangular profile peaks ~1.3× that. Sanity-
        // pin the amplitude ORDER — the physics, not the exact shape.
        const int H = 10, T = 9;
        const float m = 4f;
        var p = Problem(H,
            new[] { new ChannelDef { Lever = LeverKind.Force, Weight = 1e-4f, Cap = 1e9f, ActiveFrom = 0, ActiveTo = H } },
            new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = m, HingeScale = 1f } });

        var (z, _) = Solve(p);

        float tc = H * Dt;
        float constantSpread = 2f * m / (tc * tc);   // 288 px/s²
        float peak = 0f;
        for (int k = 0; k < H; k++) peak = MathF.Max(peak, z[k].Length());
        Assert.True(peak > 0.9f * constantSpread && peak < 1.6f * constantSpread,
            $"peak {peak} vs 2m/t_c² {constantSpread}");
    }

    [Fact]
    public void Passivity_RedirectCannotMeetUpwardDemand_ResidualFlags()
    {
        // Pure redirect against a horizontal coast: the Thales disc bounds the
        // upward velocity change by ‖v̂‖, so a 5px next-tick clearance is
        // structurally out of reach — least-violation output + honest residual,
        // and never any added speed.
        const int H = 2;
        var coast = new[] { new Vector2(10f, 0f), new Vector2(10f, 0f) };
        var p = Problem(H,
            new[] { new ChannelDef { Lever = LeverKind.VelocityUpdate, Weight = 1e-6f, Redirect = true, ActiveFrom = 0, ActiveTo = H } },
            new[] { new ClearanceRow { Tick = 0, Normal = Up, Depth = 5f, HingeScale = 1f } },
            coastVel: coast);

        var (z, residual) = Solve(p);

        Assert.True(residual > 4.5f, $"residual {residual} should flag infeasibility");
        for (int k = 0; k < H; k++)
        {
            // Admissible: inside the Thales disc…
            var center = -coast[k] * 0.5f;
            Assert.True((z[k] - center).Length() <= coast[k].Length() * 0.5f + 1e-3f,
                $"k={k}: z outside the redirect disc");
            // …which means the corrected velocity never gains speed.
            Assert.True((coast[k] + z[k]).Length() <= coast[k].Length() + 1e-3f,
                $"k={k}: redirect added speed");
        }
    }

    [Fact]
    public void DeltaWeight_NoBangBang_AndNeverWorsensSmoothness()
    {
        // Historical note: with the global-η step, wΔ = 0 slammed the profile
        // from the zero anchor straight to cap on tick 0 and wΔ was the repair.
        // The per-variable preconditioner removed that pathology STRUCTURALLY —
        // its natural first step is the uniform profile (see the uniform-profile
        // oracle), well under cap. This oracle now pins: (a) no bang-bang even
        // at wΔ = 0, (b) wΔ never INCREASES the worst step (non-regression of
        // its smoothing role), (c) clearance holds under smoothing.
        const int H = 10, T = 9;
        const float m = 4f, cap = 350f;
        ChannelDef Ch() => new() { Lever = LeverKind.Force, Weight = 1e-4f, Cap = cap, ActiveFrom = 0, ActiveTo = H };
        var row = new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = m, HingeScale = 1f } };

        var (zSharp, rSharp)    = Solve(Problem(H, new[] { Ch() }, row, deltaWeight: 0f));
        var (zSmooth, rSmooth)  = Solve(Problem(H, new[] { Ch() }, row, deltaWeight: 5f));
        var (zSmoother, _)      = Solve(Problem(H, new[] { Ch() }, row, deltaWeight: 10f));

        float MaxStep(Vector2[] z)
        {
            float worst = z[0].Length();   // step from the zero anchor
            for (int k = 1; k < H; k++) worst = MathF.Max(worst, (z[k] - z[k - 1]).Length());
            return worst;
        }

        Assert.True(rSharp < 0.01f * m, $"sharp residual {rSharp}");
        Assert.True(MaxStep(zSharp) < 0.9f * cap,
            $"preconditioned step should not bang toward cap: max step {MaxStep(zSharp)} vs cap {cap}");
        Assert.True(MaxStep(zSmooth) <= MaxStep(zSharp) * 1.001f,
            $"wΔ must not worsen smoothness: {MaxStep(zSmooth)} vs {MaxStep(zSharp)}");
        Assert.True(MaxStep(zSmoother) <= MaxStep(zSmooth) * 1.001f,
            $"heavier wΔ must not worsen smoothness: {MaxStep(zSmoother)} vs {MaxStep(zSmooth)}");
        Assert.True(rSmooth < 0.1f * m, $"smoothed residual {rSmooth}");
    }

    [Fact]
    public void ForceCap_IsRespected_EveryTick()
    {
        const int H = 8, T = 7;
        const float cap = 100f;
        var p = Problem(H,
            new[] { new ChannelDef { Lever = LeverKind.Force, Weight = 1e-4f, Cap = cap, ActiveFrom = 0, ActiveTo = H } },
            new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = 50f, HingeScale = 1f } });   // far beyond reach

        var (z, residual) = Solve(p);

        for (int k = 0; k < H; k++)
            Assert.True(z[k].Length() <= cap + 1e-3f, $"k={k}: ‖z‖ {z[k].Length()} > cap");
        Assert.True(residual > 0f, "unreachable depth must leave a residual");
    }

    [Fact]
    public void Solve_IsDeterministic_BitwiseRepeatable()
    {
        const int H = 10;
        var coast = new Vector2[H];
        for (int k = 0; k < H; k++) coast[k] = new Vector2(120f - 3f * k, 40f + k);
        var p = Problem(H,
            new[]
            {
                new ChannelDef { Lever = LeverKind.VelocityUpdate, Weight = 1e-6f, Redirect = true, ActiveFrom = 0, ActiveTo = H },
                new ChannelDef { Lever = LeverKind.Force, Weight = 1f, Cap = 400f, ActiveFrom = 0, ActiveTo = 5 },
            },
            new[]
            {
                new ClearanceRow { Tick = 4, Normal = Up, Depth = 3f, HingeScale = 1f },
                new ClearanceRow { Tick = 8, Normal = new Vector2(1f, 0f), Depth = 2f, HingeScale = 1f },
            },
            deltaWeight: 2f, coastVel: coast);

        var (z1, r1) = Solve(p);
        var (z2, r2) = Solve(p);

        Assert.Equal(r1, r2);
        for (int i = 0; i < z1.Length; i++) Assert.Equal(z1[i], z2[i]);
    }
}
