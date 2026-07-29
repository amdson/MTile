using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Anchor scenarios for the ballistic corrector (BALLISTIC_CORRECTOR_PLAN step 5):
// tunnel-vault (the design's motivating case), trigger-by-feasibility refusal,
// slalom scheduling, and smoothness — the fixtures the plan says to write before
// the code they exercise earns trust.
public class CorrectorAnchorTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private static readonly Vector2 Up = new(0f, -1f);

    private SimFrame[] Run(ChunkMap terrain, Vector2 start, int frames)
    {
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = start,
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = frames,
            Dt            = Dt,
            Gravity       = new Vector2(0f, 600f),
        };
        var result = SimRunner.Run(cfg);
        foreach (var f in result)
            if (f.Frame % 5 == 0 || f.Transition)
                output.WriteLine($"{f.Frame,3} {f.State,-24} x={f.X,7:F2} y={f.Y,6:F2} vx={f.Vx,7:F2} vy={f.Vy,7:F2}");
        return result;
    }

    // ── Tunnel vault: overhang corners clip the authored arc by a small margin ──
    // 1-block step inside a corridor whose ceiling (bottom y=48) sits 3px inside
    // the authored arc's apex. The corrector must flatten the arc: cleared lip,
    // cleared ceiling, landed in the (crouch-raised) gate. Redirect-tier only.
    [Fact]
    public void TunnelVault_FlattenedArc_ClearsBothAndLandsInGate()
    {
        var terrain = SimTerrain.FromAscii(@"
            XXXXXXXXXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 2);
        // Floor top 96 (rest y ≈ 71.5..72), step top 80 (lip x=128), ceiling bottom 48.
        var frames = Run(terrain, new Vector2(12f, 72f), 120);

        Assert.True(frames.Any(f => f.State.Contains("Parkour")), "expected the corrector vault");
        Assert.True(frames.Any(f => f.X > 140f && f.Y < 69f),
            "expected delivery onto the step (crouch gate ≈ 62)");
        // The ceiling is never clipped: body top (y − 12) stays below y=48, modulo
        // the solver Epsilon. The AUTHORED arc alone would poke ~3px into it.
        float minY = frames.Min(f => f.Y);
        Assert.True(minY >= 59.4f, $"arc clipped the tunnel ceiling: min y={minY:F2} (limit 59.4)");
        // And the vault actually got airborne off the floor rather than stalling.
        Assert.True(minY < 66f, $"arc never rose: min y={minY:F2}");
    }

    // ── Refusal: the same trigger, geometry the arc cannot thread ──
    // Direct mechanism pin on trigger-by-feasibility: a slab over the body's own
    // columns blocks the climb volume. The precondition's full-arc solve must
    // refuse; removing the slab must accept. (The sim-level behavior — honest bonk,
    // no corner jam — is pinned by ArcJumpStateTests.LowLipOverApproach.)
    [Fact]
    public void TriggerFeasibility_SlabOverApproach_RefusesAndAcceptsWithoutIt()
    {
        var blocked = SimTerrain.FromAscii(@"
            OOOOOOXXOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var open = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 1);

        Assert.False(Precondition(blocked), "slab over the approach must refuse the arc");
        Assert.True(Precondition(open), "same approach without the slab must accept");

        static bool Precondition(ChunkMap terrain)
        {
            // Running + Up: the arc's deliberate-entry gate (movement_todo #6)
            // must be satisfied so this test isolates the slab feasibility.
            var body = new PhysicsBody(Polygon.CreateRegular(PlayerCharacter.Radius, 6),
                                       new Vector2(116f, 26f))
                       { Velocity = new Vector2(100f, 0f) };
            var ctx = new EnvironmentContext
            {
                Chunks    = terrain,
                Body      = body,
                Dt        = Dt,
                Gravity   = new Vector2(0f, 600f),
                Corrector = new CorrectorScratch(),
                Intent    = new InputIntent { HeldHorizontal = 1, CurrentHorizontal = 1 },
                Input     = new PlayerInput { Right = true, Up = true },
                Modifiers = MovementModifiers.Identity,
            };
            return new ArcJumpState(1).CheckPreConditions(ctx, new PlayerAbilityState());
        }
    }

    // ── Slalom: opposed deflections at different event ticks ──
    // A ceiling row early and a floor row late demand a scheduled profile (down
    // early, up late) — the case a single-Δv design cannot represent. Solver-level,
    // at the entry-feasibility iteration budget: opposed near-parallel rows are
    // ill-conditioned for the per-tick 4-iteration budget (they zigzag), which is
    // exactly why full-arc entry solves run deeper fixed counts.
    [Fact]
    public void Slalom_OpposedRows_ScheduledProfile_BothCleared()
    {
        const int H = 10;
        const float m = 2f;
        var p = new CorrectionProblem
        {
            H = H, Dt = 1f / 60f,
            CoastVel = new Vector2[H],
            Rows = new[]
            {
                new ClearanceRow { Tick = 4, Normal = new Vector2(0f, 1f), Depth = m, HingeScale = 1f },   // duck early
                new ClearanceRow { Tick = 9, Normal = Up,                  Depth = m, HingeScale = 1f },   // rise late
            },
            RowCount = 2,
            Channels = new[] { new ChannelDef { Lever = LeverKind.Force, Weight = 1e-4f, Cap = 1e9f, ActiveFrom = 0, ActiveTo = H } },
            ChannelCount = 1,
            PrevApplied = new Vector2[1],
            DeltaWeight = 0f, HingeWeight = 1e6f,
            InnerIterations = 128,
        };
        var z = new Vector2[H]; var scratch = new Vector2[H];
        CorrectionSolver.Solve(p, z, scratch);

        Assert.True(CorrectionSolver.RowSlack(p, z, 0) < 0.15f * m, $"ceiling row slack {CorrectionSolver.RowSlack(p, z, 0)}");
        Assert.True(CorrectionSolver.RowSlack(p, z, 1) < 0.15f * m, $"floor row slack {CorrectionSolver.RowSlack(p, z, 1)}");
        // Scheduled, not averaged: net down-force before tick 4, net up-force after.
        float early = 0f, late = 0f;
        for (int k = 0; k <= 3; k++) early += z[k].Y;
        for (int k = 7; k <= 9; k++) late  += z[k].Y;
        Assert.True(early > 0f, $"early ticks should push down (y-down), got {early}");
        Assert.True(late  < 0f, $"late ticks should push up, got {late}");
    }

    // ── Smoothness: single-corner graze ⇒ bounded peak, no sign flips ──
    [Fact]
    public void SingleCornerGraze_PeakBounded_NoSignFlips()
    {
        const int H = 10, T = 9;
        const float m = 2f;
        var p = new CorrectionProblem
        {
            H = H, Dt = 1f / 60f,
            CoastVel = new Vector2[H],
            Rows = new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = m, HingeScale = 1f } },
            RowCount = 1,
            Channels = new[] { new ChannelDef { Lever = LeverKind.Force, Weight = 1e-4f, Cap = 1e9f, ActiveFrom = 0, ActiveTo = H } },
            ChannelCount = 1,
            PrevApplied = new Vector2[1],
            DeltaWeight = 5f, HingeWeight = 1e6f,
        };
        var z = new Vector2[H]; var scratch = new Vector2[H];
        CorrectionSolver.Solve(p, z, scratch);

        float tc = H * (1f / 60f);
        float bound = 1.6f * 2f * m / (tc * tc);
        for (int k = 0; k < H; k++)
        {
            Assert.True(z[k].Length() <= bound, $"k={k}: force {z[k].Length():F1} exceeds c·2m/t_c² {bound:F1}");
            Assert.True(Vector2.Dot(z[k], Up) >= -1e-3f, $"k={k}: sign flip — correction opposes the clearance");
        }
    }
}
