using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The gym/corridor scenarios promoted to ASSERTING tests (CONSOLIDATION_PLAN §5)
// plus the elective-refusal contract (§4 / Plans/ELECTIVE_REFUSAL_NOTE.md).
// All run the fall+stand-restricted player (the corridor-stage harness): every
// behavior here is the ambient fold's — no jump, no crouch, no climb family.
public class FoldScenarioTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const float T = Chunk.TileSize;

    private static (PlayerCharacter player, List<PhysicsBody> bodies, Controller ctrl, ChunkMap terrain)
        Harness(string ascii, Vector2 start, int originTileY = 0)
    {
        var terrain = SimTerrain.FromAscii(ascii, originTileX: 0, originTileY: originTileY);
        var player = new PlayerCharacter(start);
        player.RestrictToFallAndStand();
        return (player, new List<PhysicsBody> { player.Body }, new Controller(), terrain);
    }

    private static SimFrame Step(PlayerCharacter p, List<PhysicsBody> bodies, Controller ctrl,
                                 ChunkMap terrain, PlayerInput input, int f)
    {
        ctrl.InjectInput(input);
        p.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
        PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
        return new SimFrame(f, f * Dt, p.Body.Position.X, p.Body.Position.Y,
                            p.Body.Velocity.X, p.Body.Velocity.Y, 0f, 0f,
                            p.CurrentStateName, false);
    }

    // ── Rest is motionless ───────────────────────────────────────────────────
    [Fact]
    public void RestOnFlat_NoInput_Motionless()
    {
        // Floor row 8; spawn a little above the floor top so the body has
        // settled to its resting hover well before the assertion window.
        float floorTopY = 8 * T;
        var (p, bodies, ctrl, terrain) = Harness(
            string.Join("\n", Enumerable.Repeat("OOOOOOOOOOOOOOOO", 8).Append("XXXXXXXXXXXXXXXX")),
            new Vector2(100f, floorTopY - 22f));

        var trace = new List<SimFrame>();
        for (int f = 0; f < 120; f++) trace.Add(Step(p, bodies, ctrl, terrain, default, f));

        // After settling, the body holds station: no drift, no bobbing.
        foreach (var fr in trace.Skip(60))
        {
            Assert.True(MathF.Abs(fr.Vx) < 1f, $"f={fr.Frame}: drifting, vx={fr.Vx:F2}");
            Assert.True(MathF.Abs(fr.Vy) < 2.5f, $"f={fr.Frame}: bobbing, vy={fr.Vy:F2}");
        }
        float ySpread = trace.Skip(60).Max(fr => fr.Y) - trace.Skip(60).Min(fr => fr.Y);
        Assert.True(ySpread < 0.5f, $"settled y wanders {ySpread:F3}px");
    }

    // ── 1-high climb succeeds (the elective R1 path) ─────────────────────────
    [Fact]
    public void OneHighLedge_HoldRight_ClimbsAndContinues()
    {
        // Floor row 8; ledge cols 10.. row 7 (one tile tall). Widened to 40
        // cols (vs. the old 24) so there's a generous plateau to sample
        // after the climb transient, now that a tile is only 11px.
        const int Cols = 40;
        var rows = new string[9];
        for (int r = 0; r < 9; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < Cols; c++)
                sb.Append(r == 8 || (r == 7 && c >= 10) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTopY = 8 * T;
        float ledgeTopY = 7 * T;
        var (p, bodies, ctrl, terrain) = Harness(string.Join("\n", rows), new Vector2(40f, floorTopY - 22f));

        var trace = new List<SimFrame>();
        for (int f = 0; f < 240; f++)
            trace.Add(Step(p, bodies, ctrl, terrain, new PlayerInput { Right = true }, f));

        // Measure well past the ledge (clear of the climb transient) and
        // well before the map's far edge — the body legitimately runs off
        // the far edge afterward.
        float ledgeStartX = 10 * T;
        float mapEndX = Cols * T;
        var onTop = trace.Where(fr => fr.X > ledgeStartX + 60f && fr.X < mapEndX - 60f).ToArray();
        // Riding the upper floor should sit well above (numerically below)
        // the midpoint between the two floor tops — a one-tile difference
        // regardless of the corrector's absolute hover offset.
        float midY = (floorTopY + ledgeTopY) / 2f;
        output.WriteLine($"final x={trace[^1].X:F1}; plateau frames={onTop.Length} " +
                         $"minY={(onTop.Length > 0 ? onTop.Min(fr => fr.Y) : float.NaN):F1} (ledge top={ledgeTopY:F1}, midY={midY:F1})");
        Assert.True(onTop.Length > 10, $"did not climb + continue: final x={trace[^1].X:F1}");
        Assert.True(onTop.Count(fr => fr.Y < midY) > 10,
            $"never rode the upper floor: plateau y min={onTop.Min(fr => fr.Y):F1}");
    }

    // ── 2-high wall bonks — emergent (climb band), no net height gain ────────
    [Fact]
    public void TwoHighWall_HoldRight_BonksNoHeightGain()
    {
        var rows = new string[9];
        for (int r = 0; r < 9; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < 24; c++)
                sb.Append(r == 8 || (r >= 6 && c >= 10) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTopY = 8 * T;
        var (p, bodies, ctrl, terrain) = Harness(string.Join("\n", rows), new Vector2(40f, floorTopY - 22f));

        var trace = new List<SimFrame>();
        for (int f = 0; f < 240; f++)
            trace.Add(Step(p, bodies, ctrl, terrain, new PlayerInput { Right = true }, f));

        float minY = trace.Min(fr => fr.Y);
        var last = trace[^1];
        float wallFaceX = 10 * T;
        // No height gain: must not rise more than half a tile above the
        // floor's resting height.
        float noRiseFloor = floorTopY - T / 2f;
        output.WriteLine($"final x={last.X:F1} minY={minY:F1} (wall face at {wallFaceX:F1}, floor top {floorTopY:F1})");
        Assert.True(last.X < wallFaceX, $"passed through a 2-high wall: x={last.X:F1}");
        Assert.True(minY > noRiseFloor, $"gained height against a 2-high wall: minY={minY:F1}");
    }

    // ── Elective refusal: a climbable ledge under a low ceiling ──────────────
    // R1 binds the 1-high step, but the ceiling makes the climb undeliverable —
    // the corrected rollout can't track the stepped reference. The elective set
    // must drop AS A SET (R0): the body runs into the step and stalls at full
    // hover, with NO half-scramble (the L2-graceful hover-against-the-wall).
    [Fact]
    public void OneHighLedgeUnderCeiling_HoldRight_HonestBonk_NoHalfScramble()
    {
        // Ledge at row 7 cols 10.., ceiling at row 5 over the ledge region:
        // gap above the ledge top is 1 tile (rows 5 and 7 are two rows
        // apart) — nothing can stand there, regardless of tile size.
        var rows = new string[9];
        for (int r = 0; r < 9; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < 24; c++)
                sb.Append(r == 8 || (r == 7 && c >= 10) || (r == 5 && c >= 9) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTopY = 8 * T;
        var (p, bodies, ctrl, terrain) = Harness(string.Join("\n", rows), new Vector2(40f, floorTopY - 22f));

        var trace = new List<SimFrame>();
        for (int f = 0; f < 240; f++)
            trace.Add(Step(p, bodies, ctrl, terrain, new PlayerInput { Right = true }, f));

        var tail = trace.Skip(120).ToArray();   // stalled regime
        float minY = tail.Min(fr => fr.Y);
        float maxY = tail.Max(fr => fr.Y);
        var last = trace[^1];
        float stepX = 10 * T;
        // No half-scramble: must not rise more than half a tile above the
        // floor's resting height.
        float noRiseFloor = floorTopY - T / 2f;
        output.WriteLine($"final x={last.X:F1}; stalled-tail y ∈ [{minY:F1}, {maxY:F1}] (floor top {floorTopY:F1})");
        Assert.True(last.X < stepX, $"climbed through the ceiling gap: x={last.X:F1}");
        // No half-scramble: the stalled body holds its ground-level hover — it
        // does not ride partway up the step and hover against the wall.
        Assert.True(minY > noRiseFloor, $"half-scramble: stalled body lifted to y={minY:F1}");
        Assert.True(maxY - minY < 6f, $"stall oscillates {maxY - minY:F1}px");
    }

    // ── Lip step-off falls ballistically (no down-force during the fall) ─────
    [Fact]
    public void LipStepOff_FallIsBallistic()
    {
        // Upper floor cols 0..9 (top at row 7), lower floor row 8+ none —
        // open pit after the lip, floor far below at row 14.
        var rows = new string[15];
        for (int r = 0; r < 15; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < 24; c++)
                sb.Append((r == 7 && c <= 9) || r == 14 ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float upperFloorTopY = 7 * T;
        float lowerFloorTopY = 14 * T;
        var (p, bodies, ctrl, terrain) = Harness(string.Join("\n", rows), new Vector2(100f, upperFloorTopY - 22f));

        var trace = new List<SimFrame>();
        for (int f = 0; f < 180; f++)
            trace.Add(Step(p, bodies, ctrl, terrain, new PlayerInput { Right = true }, f));

        // Find the unbound fall: frames past the lip (clear of the edge
        // taper) still well above the lower floor's support reach.
        float lipEdgeX = 10 * T;
        float clearOfLowerFloorY = lowerFloorTopY - 34f;
        var falling = trace.Where(fr => fr.X > lipEdgeX + 10f && fr.Y < clearOfLowerFloorY && fr.Vy > 5f).ToArray();
        Assert.True(falling.Length >= 5, "never got a clean falling window");
        for (int i = 1; i < falling.Length; i++)
        {
            if (falling[i].Frame != falling[i - 1].Frame + 1) continue;
            float dvy = falling[i].Vy - falling[i - 1].Vy;
            // Pure gravity is +10/frame at 60fps; assert no downward assist
            // beyond it and no hover pull-back either.
            Assert.True(dvy > 6f && dvy < 14f,
                $"fall not ballistic at f={falling[i].Frame}: Δvy={dvy:F2} (gravity step = 10)");
        }
        Assert.True(trace[^1].Y > clearOfLowerFloorY, "never landed in the pit");
    }

    // ── Corridor endurance: the bumpy tunnel is traversed at speed ───────────
    [Fact]
    public void BumpyCorridor_HoldRight_TraversesAtSpeed()
    {
        const int W = 64;
        var rows = new string[7];
        var b = new char[7, W];
        for (int r = 0; r < 7; r++)
        for (int c = 0; c < W; c++)
        {
            bool tunnel = c >= 16;
            b[r, c] = r switch
            {
                6 => 'X',
                2 when tunnel => 'X',
                3 when tunnel && c % 4 == 3 => 'X',
                5 when tunnel && c % 4 == 1 => 'X',
                _ => 'O',
            };
        }
        for (int r = 0; r < 7; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++) sb.Append(b[r, c]);
            rows[r] = sb.ToString();
        }
        var (p, bodies, ctrl, terrain) = Harness(string.Join("\n", rows), new Vector2(24f, 74f));

        // The corridor sentinel pins the "ref" engine (the production fold for
        // this regime); qp's corridor performance is no longer a sentinel.
        var prevEngine = MovementConfig.Current.FoldEngine;
        MovementConfig.Current.FoldEngine = "ref";
        var trace = new List<SimFrame>();
        try
        {
            for (int f = 0; f < 800; f++)
                trace.Add(Step(p, bodies, ctrl, terrain, new PlayerInput { Right = true }, f));
        }
        finally { MovementConfig.Current.FoldEngine = prevEngine; }

        var last = trace[^1];
        float avg = (last.X - 24f) / (800 * Dt);
        output.WriteLine($"avg {avg:F1} px/s, final x={last.X:F1} of {W * 16} (mouth at 256)");
        // The tunnel is 3 tiles high with alternating floor/ceiling bumps —
        // every crossing is a solver-planned hop or duck. The bar is coarse:
        // sustained progress well past the mouth at a meaningful fraction of
        // walk speed, no stall.
        Assert.True(avg > 55f, $"tunnel traversal too slow: {avg:F1} px/s");
        Assert.True(last.X > 600f, $"stalled in the tunnel at x={last.X:F1}");
    }
}
