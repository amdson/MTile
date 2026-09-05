using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// AVALANCHE_RIDING_V2 Part 1 — the ordering-side tests: the back-ignition
// repro (expected red today) and the shape-invariance scaffold that Part 3's
// reordering levers will be checked against. See Plans/AVALANCHE_RIDING_V2.md.
public class AvalancheOrderingTests(ITestOutputHelper output)
{
    private static float Ts => Chunk.TileSize;

    private static ChunkMap BuildFloorWithWall(int cols, int rows, int floorRow, int wallCol, int wallTopRow)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                bool solid = r >= floorRow || (c == wallCol && r >= wallTopRow && r < floorRow);
                sb.Append(solid ? 'X' : 'O');
            }
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // ── Back-ignition repro ───────────────────────────────────────────────────
    // SPEC TEST — expected red until AVALANCHE_RIDING_V2 Parts 2–3 land (schedule gate).
    //
    // Diagnosis carried over from v1: TileMassField's spill cascade (World/TileMassField.cs)
    // recurses synchronously within a single Deposit call, up to MaxSpillDepth hops, sharing
    // 1/4 of the remaining amount per hop. A big single-frame leak (a fresh MassBall's first
    // leak scales with its budget) can reach a solid wall many cells ahead of the ball's own
    // travelled distance in that same frame — and a free cell adjacent to a solid wall starts
    // Growing IMMEDIATELY (ChunkMap.TryRequestTile: any solid 4-neighbour promotes on the
    // spot), well before the front visibly arrives. That's "far-side static ignition."
    [Fact]
    public void BackIgnitionRepro_GrowthNeverStartsAheadOfDepositorPassage()
    {
        const int Cols = 60, Rows = 40, FloorRow = 30;
        const int OriginCol = 5;
        const int WallCol = OriginCol + 10;      // "several tiles ahead"
        const int WallTopRow = FloorRow - 15;
        const float ToleranceTiles = 2f;         // "a couple of tiles" per the doc

        var terrain = BuildFloorWithWall(Cols, Rows, FloorRow, WallCol, WallTopRow);
        var origin = new Vector2(OriginCol * Ts + Ts * 0.5f, (FloorRow - 1) * Ts + Ts * 0.5f);
        var waveDir = Vector2.UnitX;   // pure horizontal: straight at the wall
        var wave = new ScriptedWave(origin, 0f, 15f * Ts, 150f);

        var knownGrowing = new HashSet<(int, int)>();
        float ballProjSoFar = float.NegativeInfinity;
        float worstAhead = float.NegativeInfinity;
        int worstFrame = -1;
        (int gtx, int gty) worstCell = default;

        const int Frames = 300;
        for (int f = 0; f < Frames; f++)
        {
            terrain.TickSprouts(AvalancheHarness.Dt);
            terrain.Impact.Tick(AvalancheHarness.Dt);

            if (!wave.Done)
            {
                float depositProj = Vector2.Dot(wave.Position, waveDir);
                ballProjSoFar = MathF.Max(ballProjSoFar, depositProj);
                wave.Step(terrain, AvalancheHarness.Dt);
            }

            foreach (var node in terrain.Graph.Growing)
            {
                var key = (node.Gtx, node.Gty);
                if (!knownGrowing.Add(key)) continue;
                float proj = Vector2.Dot(node.CellCenter, waveDir);
                float ahead = proj - ballProjSoFar;
                if (ahead > worstAhead) { worstAhead = ahead; worstFrame = f; worstCell = key; }
            }
        }

        output.WriteLine($"worst growth-start-ahead-of-passage: {worstAhead:F1}px ({worstAhead / Ts:F1} tiles) " +
                         $"at frame {worstFrame}, cell {worstCell}, tolerance {ToleranceTiles * Ts:F1}px");
        output.WriteLine($"ball's own recorded passage reached {ballProjSoFar:F1}px ({ballProjSoFar / Ts:F1} tiles) by the end");

        Assert.True(worstAhead <= ToleranceTiles * Ts,
            $"growth started {worstAhead / Ts:F1} tiles ahead of the depositor's own recorded passage " +
            $"(cell {worstCell}, frame {worstFrame}) — far-side static ignition (back-ignition bug)");
    }

    // ── Painted stroke, then eruption: the in-game launch sequence ───────────
    // The held-RMB painter deposits UNTAGGED mass (ActionStates.Paint) along the
    // gesture before the release spawns the tagged MassBall over the same cells.
    // Buckets are first-wins, so without the None-claim rule the wave's mass
    // merges in untagged, commits ungated, and a pre-seeded bucket near far
    // terrain re-opens the back-ignition race the schedule gate closed. This
    // repro pre-seeds sub-threshold untagged mass along the stroke path (a cell
    // shy of the wall included) and demands the same front monotonicity as the
    // plain spec test above.
    [Fact]
    public void PaintedStroke_ThenWave_FrontStaysMonotone()
    {
        const int Cols = 60, Rows = 40, FloorRow = 30;
        const int OriginCol = 5;
        const int WallCol = OriginCol + 10;
        const int WallTopRow = FloorRow - 15;
        const float ToleranceTiles = 2f;

        var terrain = BuildFloorWithWall(Cols, Rows, FloorRow, WallCol, WallTopRow);

        // The stroke: untagged sub-threshold mass banked along the wave's path,
        // including the cell right beside the wall — the worst pre-seed.
        for (int c = OriginCol; c < WallCol; c++)
            terrain.Mass.Deposit(terrain, c, FloorRow - 1, 0.9f, TileType.Dirt);

        var origin = new Vector2(OriginCol * Ts + Ts * 0.5f, (FloorRow - 1) * Ts + Ts * 0.5f);
        var waveDir = Vector2.UnitX;
        var wave = new ScriptedWave(origin, 0f, 15f * Ts, 150f);

        var knownGrowing = new HashSet<(int, int)>();
        float ballProjSoFar = float.NegativeInfinity;
        float worstAhead = float.NegativeInfinity;
        int worstFrame = -1;
        (int gtx, int gty) worstCell = default;

        const int Frames = 300;
        for (int f = 0; f < Frames; f++)
        {
            terrain.TickSprouts(AvalancheHarness.Dt);
            terrain.Impact.Tick(AvalancheHarness.Dt);

            if (!wave.Done)
            {
                ballProjSoFar = MathF.Max(ballProjSoFar, Vector2.Dot(wave.Position, waveDir));
                wave.Step(terrain, AvalancheHarness.Dt);
            }

            foreach (var node in terrain.Graph.Growing)
            {
                var key = (node.Gtx, node.Gty);
                if (!knownGrowing.Add(key)) continue;
                float ahead = Vector2.Dot(node.CellCenter, waveDir) - ballProjSoFar;
                if (ahead > worstAhead) { worstAhead = ahead; worstFrame = f; worstCell = key; }
            }
        }

        output.WriteLine($"worst growth-start-ahead-of-passage: {worstAhead:F1}px ({worstAhead / Ts:F1} tiles) " +
                         $"at frame {worstFrame}, cell {worstCell}");
        Assert.True(worstAhead <= ToleranceTiles * Ts,
            $"pre-seeded untagged stroke re-opened the back-ignition race: growth started " +
            $"{worstAhead / Ts:F1} tiles ahead of passage (cell {worstCell}, frame {worstFrame})");
    }

    // ── Manual/untagged requests keep symmetric shell semantics ──────────────
    // Pins today's behavior so Part 2's WaveId tagging doesn't quietly change
    // it: a cell with two solid neighbours at once promotes on ALL of its
    // solid faces simultaneously (a shell, not a race).
    [Fact]
    public void ManualSprouts_UntaggedRequest_KeepsSymmetricShellSemantics()
    {
        const int Rows = 25, Cols = 20, FloorRow = 20;
        var sb = new StringBuilder();
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
                sb.Append(r >= FloorRow || (c <= 9 && r >= FloorRow - 2) ? 'X' : 'O');
            sb.Append('\n');
        }
        var terrain = SimTerrain.FromAscii(sb.ToString());

        var sprout = terrain.TryRequestTile(10, FloorRow - 1, TileType.Stone);
        Assert.NotNull(sprout);
        Assert.Equal(SproutFaces.Below | SproutFaces.Left, sprout.Faces);
        Assert.Equal(TileSproutStatus.Growing, sprout.Status);

        float lifetime = MovementConfig.Current.SproutLifetime;
        int frames = (int)MathF.Ceiling(lifetime / AvalancheHarness.Dt) + 2;
        for (int f = 0; f < frames; f++) terrain.TickSprouts(AvalancheHarness.Dt);

        Assert.Equal(TileState.Solid, terrain.GetCellState(10, FloorRow - 1));
    }

    // ── Shape-invariance scaffold ─────────────────────────────────────────────
    // Runs a wave to completion and returns the committed cell set. Part 3's
    // reordering levers will be checked against this: "lever on" and "lever
    // off" runs must commit the identical set. For now — before any lever
    // exists — this just pins that two identical runs are deterministic.
    private static HashSet<(int, int)> RunWaveToCompletion(int cols, int rows, int floorRow,
                                                           Vector2 origin, float angleRad, float speed, float mass,
                                                           int frames)
    {
        var terrain = AvalancheHarness.BuildFlatFloor(cols, rows, floorRow);
        var wave = new ScriptedWave(origin, angleRad, speed, mass);
        for (int f = 0; f < frames; f++)
        {
            terrain.TickSprouts(AvalancheHarness.Dt);
            terrain.Impact.Tick(AvalancheHarness.Dt);
            if (!wave.Done) wave.Step(terrain, AvalancheHarness.Dt);
        }
        return AvalancheHarness.CommittedCells(terrain, TileType.Dirt, 0, cols - 1, 0, rows - 1);
    }

    [Fact]
    public void ShapeInvariance_TwoIdenticalRuns_ProduceIdenticalCommittedSets()
    {
        const int Cols = 80, Rows = 60, FloorRow = 50;
        var origin = new Vector2(30 * Ts + Ts * 0.5f, (FloorRow - 1) * Ts + Ts * 0.5f);
        float rad = 45f * MathF.PI / 180f;

        var a = RunWaveToCompletion(Cols, Rows, FloorRow, origin, rad, 20f * Ts, 60f, 400);
        var b = RunWaveToCompletion(Cols, Rows, FloorRow, origin, rad, 20f * Ts, 60f, 400);

        output.WriteLine($"run A committed {a.Count} cells, run B committed {b.Count} cells");
        Assert.True(a.Count > 0, "the wave should commit at least one tile");
        Assert.Equal(a.Count, b.Count);
        Assert.True(a.SetEquals(b),
            "two identical-parameter wave runs produced different committed cell sets — determinism scaffold failed");
    }
}
