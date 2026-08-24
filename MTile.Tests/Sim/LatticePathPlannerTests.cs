using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase-1 gates for the lattice path planner oracle
// (Plans/LATTICE_PATH_PLANNER.md §5): path sanity over flat ground, a 1-high
// block, a duck-under ceiling, wall behavior, determinism, timing. Terrain via
// SimTerrain ascii (X solid, O open), origin in TILE coords (tile = 16 px).
//
// Geometry cheat sheet (hexagon half-width 6, half-height 10.4, margin =
// half a cell = 1.6 px at 5 cells/tile — the tracker's band,
// hover 10): a floor whose top face is at world y F rests the body center at
// ≈ F − 20.4 (envelope ≈ F − 10.4, minus hover); a ceiling whose bottom face
// is at world y C blocks centers above ≈ C + 12.4 (+ margin). The default
// window is LookaheadTiles (3.5 tiles = 56 px) along u.
public class LatticePathPlannerTests(ITestOutputHelper output)
{
    private static (LatticePathPlanner planner, PhysicsBody body, CoastSample[] path)
        Setup(Vector2 pos)
    {
        var body = new PlayerCharacter(pos).Body;
        return (new LatticePathPlanner(), body, new CoastSample[256]);
    }

    private static int Solve(LatticePathPlanner planner, ChunkMap chunks, PhysicsBody body,
                             Vector2 seed, CoastSample[] path, out float cost, out bool bonk)
        => Solve(planner, chunks, body, seed, new Vector2(100f, 0f), path, out cost, out bonk);

    private static int Solve(LatticePathPlanner planner, ChunkMap chunks, PhysicsBody body,
                             Vector2 seed, Vector2 vel, CoastSample[] path,
                             out float cost, out bool bonk)
        => planner.Solve(chunks, body.Polygon, seed, vel,
            new Vector2(1f, 0f), hover: true, MovementConfig.Current.FoldHoverOffset,
            path, out cost, out bonk);

    // Flat floor at tile row 6 (top face y = 96): rest center ≈ 75.6.
    private static ChunkMap FlatFloor() =>
        SimTerrain.FromAscii(new string('X', 40), originTileX: 0, originTileY: 6);

    [Fact]
    public void FlatWalk_HugsHover_ReachesFarBand()
    {
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, FlatFloor(), body, seed, path, out _, out bool bonk);

        Assert.True(n > 0, "no path");
        Assert.False(bonk, planner.LastDebug);
        float lookahead = MovementConfig.Current.LatticeLookaheadTiles * Chunk.TileSize;
        Assert.True(path[n - 1].Pos.X >= seed.X + lookahead - 2f * Chunk.TileSize / 5f,
            $"fell short: {path[n - 1].Pos.X:F1} of {seed.X + lookahead:F1}");
        for (int i = 0; i < n; i++)
            Assert.True(MathF.Abs(path[i].Pos.Y - seed.Y) <= 5f,
                $"hover deviation {path[i].Pos.Y - seed.Y:F1} at sample {i}");
    }

    [Fact]
    public void BlockAhead_PathClimbsOver()
    {
        // 1-high block at tile x=7 (world 112..128) on the row-6 floor. Its
        // C-obstacle spans x ≈ [96, 144], y up to ≈ 59.6 — inside the window
        // from a seed at x=80 (far band ≈ 132.8).
        var sb = new StringBuilder();
        sb.Append("OOOOOOOXOOOOOOOOOOOOOOOO\n");      // row 5: the block
        sb.Append(new string('X', 24));                // row 6: the floor
        var chunks = SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 5);
        var seed = new Vector2(80f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, chunks, body, seed, path, out _, out bool bonk);

        Assert.True(n > 0, "no path");
        Assert.False(bonk, planner.LastDebug);
        float minY = float.MaxValue;
        for (int i = 0; i < n; i++) minY = MathF.Min(minY, path[i].Pos.Y);
        Assert.True(seed.Y - minY >= 10f, $"never climbed: minY {minY:F1} vs seed {seed.Y:F1}");
        Assert.True(path[n - 1].Pos.X > 128f, $"stopped at {path[n - 1].Pos.X:F1}");
    }

    [Fact]
    public void CeilingAhead_PathDucksUnder()
    {
        // Ceiling (row 3, bottom face y=64) from tile x=7 onward over the row-6
        // floor: free centers under it are y ∈ [76.4, 83.6) — below the 75.6
        // rest carry, so the path must dip to pass (scenario 1/2's "slightly
        // under hover").
        var sb = new StringBuilder();
        sb.Append("OOOOOOO").Append(new string('X', 17)).Append('\n');   // row 3
        sb.Append(new string('O', 24)).Append('\n');                     // row 4
        sb.Append(new string('O', 24)).Append('\n');                     // row 5
        sb.Append(new string('X', 24));                                  // row 6
        var chunks = SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 3);
        var seed = new Vector2(80f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, chunks, body, seed, path, out _, out bool bonk);

        Assert.True(n > 0, "no path");
        Assert.False(bonk, planner.LastDebug);
        float maxY = float.MinValue;
        for (int i = 0; i < n; i++) maxY = MathF.Max(maxY, path[i].Pos.Y);
        Assert.True(maxY >= 76.4f, $"never ducked: maxY {maxY:F1}");
        Assert.True(path[n - 1].Pos.X > 125f, $"stopped at {path[n - 1].Pos.X:F1}");
    }

    [Fact]
    public void FullHeightWall_Bonks()
    {
        // A barrier spanning the whole window at tile x=10 (world 160..176):
        // no admissible route at any height — the far band is unreachable and
        // the DP gives up at the furthest reachable node (the honest bonk).
        // The window reaches ±L·(steepest slope) = ±168 px from the seed
        // (near-90° cone, ±3 offsets), so the wall starts at row −8.
        var sb = new StringBuilder();
        for (int r = -8; r < 6; r++)
            sb.Append("OOOOOOOOOOXOOOOOOOOOOOOO\n");   // rows −8..5: the wall
        sb.Append(new string('X', 24));                 // row 6: floor
        var chunks = SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: -8);
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, chunks, body, seed, path, out _, out bool bonk);

        Assert.True(bonk, planner.LastDebug);
        // The wall's C-obstacle boundary is at 160 − (half-tile 8 + half-width
        // 6 + margin 1.6) = 144.4: the honest bonk walks up to exactly there.
        if (n > 0)
            Assert.True(path[n - 1].Pos.X <= 152.5f,
                $"path claims to pass the wall: {path[n - 1].Pos.X:F1}");
    }

    [Fact(Skip = "argmax goal at ProgressWeight 7: the 2-high wall's over-route costs only ~45 (per-edge-angle steepness, ±3 primitives — a (1,3) edge buys 9.6 px of rise for 20.5), so it is still worth its ~26 px of progress; the single-weight window that mounts 1-high (cost 13) and refuses 2-high is (0.5, 1.7). Cost structure decision pending — LATTICE_SCENARIOS.md seventh pass")]
    public void FreeStandingTwoHighWall_NotWorthClimbing()
    {
        // PINS THE GOAL RULE (plan §3.4 revised): edges are still pure
        // geometry, so an over-the-top route EXISTS for a free-standing
        // 2-high wall — but at ProgressWeight 7 its ≈238 of steepness is not
        // worth the ≈26 px of progress it buys, so the argmax stops the path
        // before the wall (a bonk the costs decided). A 1-high block (≈132)
        // still is worth it: BlockAhead_PathClimbsOver.
        var sb = new StringBuilder();
        sb.Append("OOOOOOOOOOXOOOOOOOOOOOOO\n");        // row 4: wall top
        sb.Append("OOOOOOOOOOXOOOOOOOOOOOOO\n");        // row 5: wall bottom
        sb.Append(new string('X', 24));                  // row 6: floor
        var chunks = SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 4);
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, chunks, body, seed, path, out _, out bool bonk);

        Assert.True(n > 0, "no path");
        Assert.True(bonk, planner.LastDebug);
        float minY = float.MaxValue;
        for (int i = 0; i < n; i++) minY = MathF.Min(minY, path[i].Pos.Y);
        Assert.True(minY > 65f, $"climbed the wall anyway: minY {minY:F1}");
        Assert.True(path[n - 1].Pos.X < 160f - 14f, $"path claims to pass the wall: {path[n - 1].Pos.X:F1}");
    }

    // The seed run (§3.5) is OFF by default (a re-planning tracker turns it
    // into a feedback loop — LATTICE_SCENARIOS.md fourth pass); these three
    // tests pin the feature itself, so they switch it on for their scope.
    private static float WithRun(float px)
    {
        float prev = MovementConfig.Current.LatticeSeedRunPx;
        MovementConfig.Current.LatticeSeedRunPx = px;
        return prev;
    }

    // Seed run (§3.5): a body moving up-right at hover has its first 8 px of
    // path FORCED up-right — the path starts where the body is going — and
    // the hover cost then brings it back down within the window.
    [Fact]
    public void SeedVelocity_FixesInitialDirection()
    {
        float prevRun = WithRun(8f);
        try { SeedVelocity_FixesInitialDirection_Body(); }
        finally { MovementConfig.Current.LatticeSeedRunPx = prevRun; }
    }

    private static void SeedVelocity_FixesInitialDirection_Body()
    {
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, FlatFloor(), body, seed, new Vector2(100f, -100f), path,
                      out _, out bool bonk);

        Assert.True(n > 2, "no path");
        Assert.False(bonk, planner.LastDebug);
        var d0 = path[1].Pos - path[0].Pos;
        Assert.True(d0.X > 0f && d0.Y < 0f && MathF.Abs(d0.X + d0.Y) < 1e-3f,
            $"first edge is not the 45° run: {d0}");
        float minY = float.MaxValue;
        for (int i = 0; i < n; i++) minY = MathF.Min(minY, path[i].Pos.Y);
        float runPx = MovementConfig.Current.LatticeSeedRunPx;
        Assert.True(seed.Y - minY >= runPx * 0.7f - 2f,
            $"run too short: rose only {seed.Y - minY:F1} px for an {runPx} px run");
        Assert.True(MathF.Abs(path[n - 1].Pos.Y - seed.Y) <= 5f,
            $"never came back to hover: end y {path[n - 1].Pos.Y:F1}");
    }

    // Below SeedRunMinSpeed nothing is forced: hover jitter must not bend the
    // path.
    [Fact]
    public void SeedVelocity_SlowIsNotForced()
    {
        float prevRun = WithRun(8f);
        try { SeedVelocity_SlowIsNotForced_Body(); }
        finally { MovementConfig.Current.LatticeSeedRunPx = prevRun; }
    }

    private static void SeedVelocity_SlowIsNotForced_Body()
    {
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, FlatFloor(), body, seed, new Vector2(10f, -10f), path,
                      out _, out _);
        Assert.True(n > 0, "no path");
        for (int i = 0; i < n; i++)
            Assert.True(MathF.Abs(path[i].Pos.Y - seed.Y) <= 5f,
                $"slow seed velocity bent the path: {path[i].Pos.Y - seed.Y:F1} at {i}");
    }

    // A run into an obstacle is forced only as far as it fits. Seeded one
    // cell above the floor's inflated C-obstacle (boundary ≈ 83.6) and moving
    // down-right, the first run edge is blocked, nothing is forced, and the
    // solve degrades to the plain seeded path — which rises back to hover.
    [Fact]
    public void SeedVelocity_BlockedRunFallsBack()
    {
        float prevRun = WithRun(8f);
        try { SeedVelocity_BlockedRunFallsBack_Body(); }
        finally { MovementConfig.Current.LatticeSeedRunPx = prevRun; }
    }

    private static void SeedVelocity_BlockedRunFallsBack_Body()
    {
        var seed = new Vector2(100f, 81f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, FlatFloor(), body, seed, new Vector2(100f, 100f), path,
                      out _, out bool bonk);
        Assert.True(n > 1, "no path");
        Assert.False(bonk, planner.LastDebug);
        Assert.True(path[1].Pos.Y <= path[0].Pos.Y + 0.01f,
            $"forced a dive into the floor: {path[0].Pos.Y:F1} → {path[1].Pos.Y:F1}");
        Assert.True(path[n - 1].Pos.Y < seed.Y - 3f,
            $"never rose back toward hover: end y {path[n - 1].Pos.Y:F1}");
    }

    [Fact]
    public void Determinism_RepeatSolvesIdentical()
    {
        var chunks = FlatFloor();
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        var path2 = new CoastSample[256];
        int n1 = Solve(planner, chunks, body, seed, path, out float c1, out bool b1);
        int n2 = Solve(planner, chunks, body, seed, path2, out float c2, out bool b2);
        Assert.Equal(n1, n2);
        Assert.Equal(c1, c2);
        Assert.Equal(b1, b2);
        for (int i = 0; i < n1; i++) Assert.Equal(path[i].Pos, path2[i].Pos);
    }

    // TEMP EXPERIMENT (phase-1 gate): µs/solve + alloc/solve, printed. Mirrors
    // ZzzLatticeTiming so the two planners are directly comparable.
    [Fact]
    public void Time()
    {
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        var chunks = FlatFloor();
        // Warm past the JIT's tier-up threshold (30 calls + a ~100 ms
        // background compile), else this times tier-0 code — measured 6×
        // slower than the optimized solve. DOTNET_TieredCompilation=0 is the
        // no-doubt way to run it.
        for (int i = 0; i < 300; i++)
            Solve(planner, chunks, body, seed, path, out _, out _);
        System.Threading.Thread.Sleep(300);
        for (int i = 0; i < 100; i++)
            Solve(planner, chunks, body, seed, path, out _, out _);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        const int N = 500;
        for (int i = 0; i < N; i++)
            Solve(planner, chunks, body, seed, path, out _, out _);
        sw.Stop();
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        output.WriteLine($"lattice path: {sw.Elapsed.TotalMilliseconds * 1000.0 / N:F1} us/solve, " +
                         $"{(a1 - a0) / (double)N:F0} B alloc/solve — {planner.LastDebug}");
        Assert.True(sw.Elapsed.TotalMilliseconds * 1000.0 / N < 1000.0,
            "over 1 ms/solve — orders off the plan's budget");
    }
}
