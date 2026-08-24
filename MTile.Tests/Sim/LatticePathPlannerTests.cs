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
// Geometry cheat sheet (hexagon half-width 6, half-height 10.4, margin 2,
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
        => planner.Solve(chunks, body.Polygon, seed, new Vector2(100f, 0f),
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
        var sb = new StringBuilder();
        for (int r = 0; r < 6; r++)
            sb.Append("OOOOOOOOOOXOOOOOOOOOOOOO\n");   // rows 0..5: the wall
        sb.Append(new string('X', 24));                 // row 6: floor
        var chunks = SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 0);
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, chunks, body, seed, path, out _, out bool bonk);

        Assert.True(bonk, planner.LastDebug);
        // The wall's C-obstacle boundary is at 160 − (half-tile 8 + half-width
        // 6 + margin 2) = 152: the honest bonk walks up to exactly there.
        if (n > 0)
            Assert.True(path[n - 1].Pos.X <= 152.5f,
                $"path claims to pass the wall: {path[n - 1].Pos.X:F1}");
    }

    [Fact]
    public void FreeStandingTwoHighWall_RoutesOver()
    {
        // PINS AN ACCEPTED DESIGN DECISION (plan §3.3/§4.3): edges are pure
        // geometry — no support gate — so a free-standing 2-high wall with open
        // air above it gets an over-the-top route. Whether the legs can deliver
        // it is the tracker's and the give-up's question, not the path's.
        var sb = new StringBuilder();
        sb.Append("OOOOOOOOOOXOOOOOOOOOOOOO\n");        // row 4: wall top
        sb.Append("OOOOOOOOOOXOOOOOOOOOOOOO\n");        // row 5: wall bottom
        sb.Append(new string('X', 24));                  // row 6: floor
        var chunks = SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: 4);
        var seed = new Vector2(100f, 75f);
        var (planner, body, path) = Setup(seed);
        int n = Solve(planner, chunks, body, seed, path, out _, out bool bonk);

        Assert.True(n > 0, "no path");
        Assert.False(bonk, planner.LastDebug);
        float minY = float.MaxValue;
        for (int i = 0; i < n; i++) minY = MathF.Min(minY, path[i].Pos.Y);
        Assert.True(minY < 55f, $"did not route over the wall: minY {minY:F1}");
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
        for (int i = 0; i < 20; i++)
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
