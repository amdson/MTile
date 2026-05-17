using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Sideways block-push tests. When a TileSprout grows horizontally, its swept
// AABB carries a constant surface velocity ((endCenter - startCenter) / Lifetime).
// If the swept AABB enters the player body, the relative-frame collision math
// in PhysicsWorld should push the body in the direction of growth.
//
// Layout strategy: the sprout cell's parent-priority for sideways growth requires
// (below = empty, then left or right = solid). A two-tile-tall stack on top of
// ground satisfies that for the cell directly next to its upper block — `below`
// is empty corridor air, and the stack is the left/right solid neighbour.
//
// Coordinate notes: tile = 16 px. PlayerCharacter.Radius = 9.5, hex pointing up;
// horizontal half-width = r·cos(30°) ≈ 8.23. SproutLifetime default = 0.1s, so
// at dt=1/30 the sprout grows over ~3 frames and its surface velocity has
// magnitude 16 / 0.1 = 160 px/s.
public class SproutPushTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    // ── Stack on the left, sprout grows rightward into the player ──────────
    // Stack at col 8 rows 2-3. Sprout cell = col 9 row 2.
    //   below (col 9, row 3) = empty
    //   left  (col 8, row 2) = SOLID  ← wins priority
    //   right (col 10, row 2) = empty
    // → sprout grows LEFT→RIGHT, centre moves x:136 → 152, final AABB x:144..160.
    // Player stands flush against the stack's right face; sprout's right face
    // sweeps through the body's left vertex.
    [Fact]
    public void SproutGrowsRight_OutOfUpperLeftCornerStack_PushesPlayerRight()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXOOOOOOOOOOO
            OOOOOOOOXOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Spawn the sprout at col 9 row 2 (cell centre (152, 40)).
        bool ok = terrain.TrySpawnSprout(152f, 40f);
        Assert.True(ok, "Sprout spawn rejected — test setup error");

        // Stack right face at x=144. Body horizontal half-width ≈ 8.23; placing
        // centre at 154 puts the body's left vertex at ~145.77, ~1.8 px clear
        // of the stack at frame 0. The sprout's right face then sweeps through.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(154f, 52f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(default),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "sprout_right_push_flush", outputDir: null);

        var last = frames[^1];
        output.WriteLine($"final X={last.X:F2} (start 154.00)");
        // Sprout AABB ends at x:144..160. To be clear of it, body centre must
        // sit at x ≥ 160 + 8.23 ≈ 168.23. Allow a margin for friction settling.
        Assert.True(last.X >= 168f,
            $"Body was not pushed right by sprout — final X={last.X:F2} (expected ≥ 168)");
    }

    // ── Mirror: stack on the right, sprout grows leftward into the player ──
    // Stack at col 11 rows 2-3. Sprout cell = col 10 row 2.
    //   below empty, left empty, right (col 11, row 2) = SOLID
    // → sprout grows RIGHT→LEFT, centre x:184 → 168, final AABB x:160..176.
    [Fact]
    public void SproutGrowsLeft_OutOfUpperRightCornerStack_PushesPlayerLeft()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOXOOOOOOOO
            OOOOOOOOOOOXOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        bool ok = terrain.TrySpawnSprout(168f, 40f);
        Assert.True(ok, "Sprout spawn rejected — test setup error");

        // Stack left face at x=176. Body centre at 166 → right vertex ≈ 174.23.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(166f, 52f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(default),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "sprout_left_push_flush", outputDir: null);

        var last = frames[^1];
        output.WriteLine($"final X={last.X:F2} (start 166.00)");
        // Sprout AABB ends at x:160..176. To be clear, body centre ≤ 160 - 8.23 ≈ 151.77.
        Assert.True(last.X <= 152f,
            $"Body was not pushed left by sprout — final X={last.X:F2} (expected ≤ 152)");
    }

    // ── Late-contact case ─────────────────────────────────────────────────
    // Same geometry as the rightward test but the player starts with a small
    // gap so the sprout only touches them on the last frame of its growth.
    // The swept-collision path has to catch this brief contact (a discrete
    // post-step check on the final, static AABB wouldn't see the surface
    // velocity that should push the body).
    [Fact]
    public void SproutGrowsRight_WithSmallGap_StillPushesPlayer()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXOOOOOOOOOOO
            OOOOOOOOXOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        bool ok = terrain.TrySpawnSprout(152f, 40f);
        Assert.True(ok);

        // Body centre 162 → left vertex ≈ 153.77. Sprout's final right face
        // at x=160 ⇒ ~6.23 px overlap on the final tick of growth.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(162f, 52f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(default),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "sprout_right_push_gap", outputDir: null);

        var last = frames[^1];
        output.WriteLine($"final X={last.X:F2} (start 162.00)");
        // Body must end up clear of the finalised tile: centre ≥ 168.
        Assert.True(last.X >= 168f,
            $"Body was not pushed by late-contact sprout — final X={last.X:F2} (expected ≥ 168)");
    }

    // ── Sprout opposes player's walk direction ────────────────────────────
    // Player walks RIGHT into the area; sprout grows LEFTWARD into them. The
    // sprout's -160 px/s surface velocity should override the walk and the
    // body should not advance past the finalised sprout face.
    [Fact]
    public void SproutGrowsLeft_AgainstRightInput_BlocksAdvance()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOXOOOOOOOO
            OOOOOOOOOOOXOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        bool ok = terrain.TrySpawnSprout(168f, 40f);
        Assert.True(ok);

        // Start moving right at walk speed, holding Right, well clear of the
        // sprout's final face (x=160). The walk would carry the body past 160
        // in ~3 frames if the sprout didn't intercept it.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(140f, 52f),
            StartVelocity = new Vector2(100f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 30,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "sprout_left_blocks_right", outputDir: null);

        var last = frames[^1];
        output.WriteLine($"final X={last.X:F2} (start 140.00, walking right)");
        // Sprout final AABB left face at x=160 ⇒ body centre may not exceed
        // 160 - 8.23 ≈ 151.77.
        Assert.True(last.X <= 152f,
            $"Player walked through a leftward-sprouting block — final X={last.X:F2} (expected ≤ 152)");
    }
}
