using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The lattice stand-fold engine (MovementConfig.FoldEngine "lattice" →
// LatticePathPlanner for the shape, LatticeTracker's short-horizon channel
// QP for the forces; Plans/LATTICE_PATH_PLANNER.md §1 revised note).
// The FoldRefEngineTests contracts, verbatim, for the new engine — plus a
// rollback round trip across the lattice solve. The qp engine stays the
// config default, so the rest of the sim suite is unaffected.
public class FoldLatticeEngineTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _prevEngine = Swap("lattice");

    private static string Swap(string engine)
    {
        var prev = MovementConfig.Current.FoldEngine;
        MovementConfig.Current.FoldEngine = engine;
        return prev;
    }

    public void Dispose() => MovementConfig.Current.FoldEngine = _prevEngine;

    // 6 open rows above the floor (66 px at the 11 px grid, comfortably more
    // than the ~33 px standing body) plus a long, 64-tile floor — the old
    // 3-open-row/24-col shape gave only 48 px of headroom and 384 px of
    // floor at 16 px tiles; at 11 px tiles that shrank to 33 px / 264 px,
    // tight enough to matter for a run that walks the body for 180 frames.
    private static ChunkMap Flat() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");

    private static readonly PlayerInput HoldRight = new() { Right = true };

    private static Vector2 Spawn()
    {
        float floorTop = 6 * Chunk.TileSize;
        return new Vector2(4 * Chunk.TileSize, floorTop - 2f * PlayerCharacter.Radius);
    }

    private static string Probe(Simulation sim)
    {
        var b = sim.Player.Body;
        return $"{sim.Player.CurrentStateName} p=({b.Position.X:R},{b.Position.Y:R}) v=({b.Velocity.X:R},{b.Velocity.Y:R})";
    }

    [Fact]
    public void Deterministic_BitIdentical()
    {
        var a = new Simulation(Flat(), Spawn());
        var b = new Simulation(Flat(), Spawn());
        b.Player.CorrectorDebug.CaptureTrajectories = true;   // capture must not perturb

        for (int f = 0; f < 180; f++)
        {
            a.Step(HoldRight);
            b.Step(HoldRight);
            Assert.Equal(a.Player.Body.Position, b.Player.Body.Position);
            Assert.Equal(a.Player.Body.Velocity, b.Player.Body.Velocity);
        }
    }

    [Fact]
    public void HoldsHover_AndMakesProgress_OnFlatGround()
    {
        var sim = new Simulation(Flat(), Spawn());
        float floorTop = 6 * Chunk.TileSize;
        float startX = sim.Player.Body.Position.X;

        for (int f = 0; f < 180; f++)
        {
            sim.Step(HoldRight);
            var p = sim.Player.Body.Position;
            Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y), $"non-finite pos at frame {f}: {p}");
            Assert.True(p.Y < floorTop + 1f, $"sank to {p.Y:F1} (floor top {floorTop}) at frame {f}");
            Assert.True(p.Y > floorTop - 6f * PlayerCharacter.Radius,
                $"flew to {p.Y:F1} at frame {f}");
        }

        float dx = sim.Player.Body.Position.X - startX;
        Assert.True(dx > 1.5f * MovementConfig.Current.MaxWalkSpeed,
            $"only advanced {dx:F1}px over 180 frames");
    }

    [Fact]
    public void AtRest_StaysPut()
    {
        var sim = new Simulation(Flat(), Spawn());
        for (int f = 0; f < 120; f++) sim.Step(default);

        var v = sim.Player.Body.Velocity;
        Assert.True(MathF.Abs(v.X) < 5f, $"drifting: vx={v.X:F2}");
        Assert.True(MathF.Abs(v.Y) < 5f, $"bobbing: vy={v.Y:F2}");
    }

    // The bumpy tunnel (the in-game "corridor" stage shape). Rebuilt for the
    // 11 px grid the same way Stage.cs's corridor was: a 4-tile (44 px)
    // interior — slab ceiling at row 1, floor bumps (row 5) at col ≡ 0
    // (mod 6), ceiling bumps (row 2) at col ≡ 3 (mod 6) — since the old
    // 3-tile/mod-4 shape (48 px interior at 16 px tiles) no longer fits the
    // unchanged ~33 px body at 11 px tiles. Under the lattice engine the
    // path itself threads them (plan §7 scenario 1); the deform only mops
    // up quantization.
    [Fact]
    public void BumpyTunnel_HoldRight_TraversesAtSpeed()
    {
        const int W = 64;
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                sb.Append(r switch
                {
                    6 => 'X',
                    1 when tunnel => 'X',
                    2 when tunnel && c % 6 == 3 => 'X',
                    5 when tunnel && c % 6 == 0 => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        var sim = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                 new Vector2(24f, 6 * ts - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 600; f++) sim.Step(HoldRight);

        float x = sim.Player.Body.Position.X;
        float avg = (x - 24f) / (600f / 60f);
        float mouthX = 16 * ts, deepX = 600f * ts / 16f;
        output.WriteLine($"tunnel: x={x:F1} avg={avg:F1} px/s");
        Assert.True(x > deepX, $"stalled in the tunnel at x={x:F1} (mouth at {mouthX:F0})");
        Assert.True(avg > 55f, $"tunnel traversal too slow: {avg:F1} px/s");
    }

    // 1-high step: the path climbs it (plan §3.3 — a climb is geometry).
    [Fact]
    public void OneHighStep_HoldRight_ClimbsAndContinues()
    {
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(48);
            for (int c = 0; c < 48; c++)
                sb.Append(r == 6 || (r == 5 && c >= 10) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTop = 6 * ts;
        var sim = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                 new Vector2(40f, floorTop - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 240; f++) sim.Step(HoldRight);

        var p = sim.Player.Body.Position;
        Assert.True(p.X > 18 * ts, $"never climbed past the step: x={p.X:F1} (step at {10 * ts})");
        Assert.True(p.Y < floorTop - ts, $"on the ledge the body should ride higher: y={p.Y:F1}");
    }

    // Tall wall spanning the whole window: no admissible route, the DP's
    // furthest node is at the wall, the carry runs straight into it and the
    // rows truncate — the honest bonk. No climb, no planned brake.
    [Fact]
    public void TallWall_HoldRight_HonestStop()
    {
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(24);
            for (int c = 0; c < 24; c++)
                sb.Append(r == 6 || (r >= 0 && c >= 12) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTop = 6 * ts;
        var sim = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                 new Vector2(40f, floorTop - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 300; f++) sim.Step(HoldRight);

        var p = sim.Player.Body.Position;
        float wallX = 12 * ts;
        Assert.True(p.X > wallX - 3 * ts, $"never reached the wall: x={p.X:F1}");
        Assert.True(p.X < wallX - 4f,
            $"passed through / into the wall: x={p.X:F1} (face at {wallX})");
        Assert.True(MathF.Abs(p.Y - (floorTop - 2f * PlayerCharacter.Radius)) < 8f,
            $"should stand at the wall, not climb it: y={p.Y:F1}");
    }

    // The duck-in: the path dips under the lip (the ceiling test of
    // LatticePathPlannerTests, now driving a body) and the servo rides it
    // into the corridor. The low section is 3 tiles (33 px) — the same
    // physical clearance as the old 2-tile/32 px gap at 16 px tiles, just
    // above CrouchedState's auto-crouch threshold (~31.3 px) so this stays
    // a path-level duck rather than a real crouch; the raw 2-tile/22 px gap
    // at 11 px tiles would force a genuine crouch instead.
    [Fact]
    public void HoldRight_DucksIntoLowCorridor()
    {
        int ts = Chunk.TileSize;
        var chunks = SimTerrain.FromAscii(@"
            XXXXXXXXXXXXXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXXXXXX
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXX");
        float floorTop = 6 * ts;
        var sim = new Simulation(chunks, new Vector2(2.5f * ts, floorTop - 2f * PlayerCharacter.Radius));

        for (int f = 0; f < 300; f++) sim.Step(HoldRight);

        float x = sim.Player.Body.Position.X;
        Assert.True(x > 14 * ts,
            $"stalled at x={x:F1} (corridor mouth at {8 * ts}) — never ducked in");
    }

    // The engine must actually be the one driving: over the step course the
    // planner produces a path on nearly every frame (LastReach > 0), rather
    // than the ref-rollout fallback (pinned seed / no path) carrying the run.
    [Fact]
    public void Engages_OnNearlyEveryFrame()
    {
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(48);
            for (int c = 0; c < 48; c++)
                sb.Append(r == 6 || (r == 5 && c >= 10) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTop = 6 * ts;
        var sim = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                 new Vector2(40f, floorTop - 2f * PlayerCharacter.Radius));
        var planner = sim.Player.CorrectorDebug.Lattice;
        int engaged = 0, bonked = 0;
        const int N = 240;
        for (int f = 0; f < N; f++)
        {
            sim.Step(HoldRight);
            if (planner.LastReach > 0) engaged++;
            // Bonks count only while a fold state owns the body: the climb
            // family (Parkour vaults this step — the arbitration item) runs
            // its own solve, and the planner's verdict under it is moot.
            string st = sim.Player.CurrentStateName;
            bool fold = st.Contains("Standing") || st.Contains("Crouched") || st.Contains("Falling");
            if (planner.LastBonk && fold) bonked++;
        }
        output.WriteLine($"engaged {engaged}/{N} frames, bonk on {bonked} (fold-owned frames)");
        Assert.True(engaged >= (int)(0.9f * N), $"lattice engaged on only {engaged}/{N} frames");
        // Under the argmax goal a "bonk" also means "chose to stop short of
        // the far band" — legitimate in front of a step while the climb is
        // not yet worth it — so it is no longer an error on an open course.
        Assert.True(bonked <= N / 10, $"bonked on {bonked} frames of an open course");
    }

    // Informational: whole-step cost under "lattice" vs "ref" on the tunnel
    // course (the planner runs every tick here). Generous ceiling so CI noise
    // can't flake it; the number is what matters for the rollback budget.
    [Fact]
    public void Cost_TunnelStep_LatticeVsRef()
    {
        const int W = 64;
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                sb.Append(r switch
                {
                    6 => 'X',
                    2 when tunnel => 'X',
                    3 when tunnel && c % 4 == 3 => 'X',
                    5 when tunnel && c % 4 == 1 => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        string terrain = string.Join("\n", rows);
        double Run(string engine)
        {
            var prev = Swap(engine);
            try
            {
                var sim = new Simulation(SimTerrain.FromAscii(terrain),
                                         new Vector2(24f, 6 * ts - 2f * PlayerCharacter.Radius));
                // Warm past the JIT tier-up (30 calls + ~100 ms background
                // compile) so this times optimized code; see the planner's
                // Time test. DOTNET_TieredCompilation=0 removes the doubt.
                for (int f = 0; f < 120; f++) sim.Step(HoldRight);
                System.Threading.Thread.Sleep(300);
                for (int f = 0; f < 60; f++) sim.Step(HoldRight);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < 540; f++) sim.Step(HoldRight);
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds * 1000.0 / 540;
            }
            finally { Swap(prev); }
        }
        double lattice = Run("lattice"), reference = Run("ref");
        output.WriteLine($"tunnel step: lattice {lattice:F1} us/step, ref {reference:F1} us/step");
        Assert.True(lattice < 2000.0, $"lattice step {lattice:F0} us — orders off the budget");
    }

    // Rollback across the lattice solve: snapshot mid-climb over a 1-high
    // step, replay, and the trace must be bit-identical — the planner's
    // scratch is derived data only (plan §4.8).
    [Fact]
    public void SnapshotMidStep_RestoreReplaysBitForBit()
    {
        int ts = Chunk.TileSize;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new System.Text.StringBuilder(48);
            for (int c = 0; c < 48; c++)
                sb.Append(r == 6 || (r == 5 && c >= 10) ? 'X' : 'O');
            rows[r] = sb.ToString();
        }
        float floorTop = 6 * ts;
        var live = new Simulation(SimTerrain.FromAscii(string.Join("\n", rows)),
                                  new Vector2(40f, floorTop - 2f * PlayerCharacter.Radius));
        const int N = 240;
        // Run until the body is within the planner's window of the step.
        int k = 0;
        while (live.Player.Body.Position.X < 10 * ts - 2 * ts && k < 200) { live.Step(HoldRight); k++; }
        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = k; f < N; f++) { live.Step(HoldRight); liveTrace.Add(Probe(live)); }
        Assert.True(live.Player.Body.Position.X > 12 * ts, "fixture never crossed the step");

        live.Restore(snap);
        var replayTrace = new List<string>();
        for (int f = k; f < N; f++) { live.Step(HoldRight); replayTrace.Add(Probe(live)); }

        Assert.Equal(liveTrace.Count, replayTrace.Count);
        for (int i = 0; i < liveTrace.Count; i++)
        {
            if (liveTrace[i] != replayTrace[i])
                output.WriteLine($"Divergence at replay frame {k + i}:\nLIVE:   {liveTrace[i]}\nREPLAY: {replayTrace[i]}");
            Assert.Equal(liveTrace[i], replayTrace[i]);
        }
    }
}
