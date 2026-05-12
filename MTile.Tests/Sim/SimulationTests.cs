using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Headless movement simulations. Run with:
//   dotnet test --logger "console;verbosity=detailed"
// CSV files are written to the working directory for spreadsheet inspection.
public class SimulationTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    // ── Flat ground: hold right ────────────────────────────────────────────
    // Baseline: player walks right on flat ground. Should enter StandingState
    // quickly and accelerate to MaxWalkSpeed.
    [Fact]
    public void HoldRight_FlatGround_ReachesWalkSpeed()
    {
        // Layout (each char = 16×16 px tile, origin at tile (0,0))
        // Row 0 = y:0..15, Row 1 = y:16..31, Row 2 = y:32..47
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Start: player center 1 radius above ground top (row 2 top = y:32)
        // Body radius = 12, so center.Y = 32 - 12 = 20
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(12f, 20f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 90,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "hold_right_flat", outputDir: null);

        // Body should be in StandingState and at full walk speed by frame 30
        // (subsequent frames may walk off the right edge of the test terrain).
        var midRun = frames[30];
        Assert.Contains("Standing", midRun.State);
        Assert.True(midRun.Vx > 50f, $"Expected rightward velocity, got Vx={midRun.Vx:F1}");
    }

    // ── Vault: approach one-block platform from the left ───────────────────
    // Player walks right, hits a single-block-height ledge, enters ParkourState,
    // vaults over it, lands on top, returns to StandingState.
    [Fact]
    public void HoldRight_VaultOneBlock_LandsOnTop()
    {
        // Tile layout: player starts left at tile x=1, ground at row 3.
        // Single block platform: row 2 col 8..15. Ground full at row 3.
        //
        //   01234567890123456789
        // 0 OOOOOOOOOOOOOOOOOOOO
        // 1 OOOOOOOOOOOOOOOOOOOO
        // 2 OOOOOOOOXXXXXXXXOOOO   ← one-block platform (cols 8–15)
        // 3 XXXXXXXXXXXXXXXXXXXX   ← full ground
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Ground top = row 3 = y:48. Player center.Y = 48 - 12 = 36.
        // Start at X=12 (one radius from left edge), approaching platform at X=128.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(12f, 36f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 180,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "vault_one_block", outputDir: null);

        // Should visit ParkourState at some point and reach StandingState on top.
        // Don't assert against the last frame — the platform is finite; the body
        // walks past it and into FallingState.
        bool visitedParkour = false;
        bool reachedStandingPostVault = false;
        bool seenParkour = false;
        foreach (var f in frames)
        {
            if (f.State.Contains("Parkour")) { visitedParkour = true; seenParkour = true; }
            else if (seenParkour && f.State.Contains("Standing")) reachedStandingPostVault = true;
        }

        Assert.True(visitedParkour, "Expected ParkourState during vault");
        Assert.True(reachedStandingPostVault, "Expected StandingState after ParkourState completes");
    }

    // ── Vault from touching the block ──────────────────────────────────────
    // Player starts with body touching the left face of a one-block obstacle
    // (X = corner.X - radius), velocity zero. Holds Right. The corner detector
    // should fire immediately, but with zero entry velocity the duration estimate
    // and PD spring may struggle. Failure mode: body stays pinned to the wall.
    [Fact]
    public void HoldRight_VaultFromTouchingBlock_DoesNotStick()
    {
        // Same layout as VaultOneBlock, but start at the wall.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Wall corner at X=128 (col 8 left edge). Body radius = 12.
        // Touching: center.X = 128 - 12 = 116. Ground center.Y = 48 - 12 = 36.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(116f, 36f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "vault_from_touching", outputDir: null);

        // Body should advance past the corner (X > 144 = corner.X + tile).
        // If body is stuck against the wall, X will stay near 116.
        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── Vault when starting only a few px from the block ───────────────────
    // Player starts close enough that corner detection fires within 1-2 frames,
    // before the body reaches typical walk speed. Tests low-momentum vault entry.
    [Fact]
    public void HoldRight_VaultFromVeryClose_DoesNotStick()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // 4 px gap between body and wall: center.X = 128 - 12 - 4 = 112
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(112f, 36f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "vault_from_very_close", outputDir: null);

        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── Running into a block at close range with momentum ─────────────────
    // Matches the player's reported bug: parkour-state initiated when "too close"
    // to the block. Body spawns with full walk-speed momentum, already inside the
    // corner detector's probe range. ParkourState should fire on frame 0 and
    // complete cleanly — no deadlock against the wall.
    [Fact]
    public void RunRight_VaultFromInsideCornerRange_DoesNotStick()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Corner at (128, 32). Start body 2px from wall at running-rest height.
        // Y=24 matches the StandingState steady-state height observed in the
        // VaultOneBlock trace (spring lift over ground at Y=48).
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(114f, 24f),
            StartVelocity = new Vector2(200f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 90,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "vault_inside_corner_range", outputDir: null);

        // Body must end up past the corner (X > 144).
        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── Body touching the wall with rightward momentum ────────────────────
    // Worst case for "starts too close": body face is flush against the wall,
    // moving right at full speed. Physics will zero the X velocity on the first
    // step. Without enough vertical lift from the PD, the body slides up the
    // wall instead of arcing over.
    [Fact]
    public void RunRight_VaultFromTouchingWithMomentum_DoesNotStick()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Touching: center.X = corner.X - radius = 128 - 12 = 116.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(116f, 24f),
            StartVelocity = new Vector2(200f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 90,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "vault_touching_with_momentum", outputDir: null);

        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── Slow walk into wall ────────────────────────────────────────────────
    // Body inches up to the wall at a small fraction of walk speed. The corner
    // detector fires, but ParkourState's entry velocity is low. With no autofire
    // (HeldHorizontal still requires sustained input), this should still complete.
    [Fact]
    public void SlowRight_VaultFromShortDistance_DoesNotStick()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // 2 px gap, low speed (Vx=30) — body reaches wall on frame 1.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(114f, 24f),
            StartVelocity = new Vector2(30f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "vault_slow_short", outputDir: null);

        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── Drop onto a ledge from above with horizontal momentum ──────────────
    // Body falls off a higher platform, lands while moving right, encounters a
    // 1-block ledge as it lands. Body Y is in transient motion (not at rest
    // height), and the FSM may flicker between Falling/Standing/Parkour.
    [Fact]
    public void Drop_OntoBlockEdge_DoesNotStick()
    {
        // Higher platform on the left (col 0..7, row 0), gap, then the 1-block
        // wall + ground block. Player drops from the high platform's right edge.
        //
        //   01234567890123456789
        // 0 XXXXXXXXOOOOOOOOOOOO   ← high platform (cols 0–7, top y=0)
        // 1 OOOOOOOOOOOOOOOOOOOO
        // 2 OOOOOOOOXXXXXXXXOOOO   ← 1-block wall (cols 8–15)
        // 3 XXXXXXXXXXXXXXXXXXXX   ← ground
        var terrain = SimTerrain.FromAscii(@"
            XXXXXXXXOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Spawn on top of the high platform (top at y=0), center.Y = -12.
        // Wait — Y=0 platform top is the world top; spawn slightly inside.
        // High platform top = row 0 = y:0..16. Player center on top: -4 (above y=0).
        // Just step off: start at (120, -4) walking right at Vx=80.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(120f, -4f),
            StartVelocity = new Vector2(80f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "drop_onto_block_edge", outputDir: null);

        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── High-velocity vault ────────────────────────────────────────────────
    // Body has 2x walk speed entering the corner. The duration estimate
    // (chord/speed) shrinks; PD has fewer frames to track the path.
    [Fact]
    public void FastRunRight_VaultAtHighSpeed_DoesNotStick()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(116f, 24f),
            StartVelocity = new Vector2(400f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 60,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "vault_high_speed", outputDir: null);

        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── Crouching player walking into a wall ──────────────────────────────
    // BUG: holding Down + Right against a wall enters WallSlidingState,
    // which is wrong — the player is crouched on the ground, not airborne.
    // Expected: stays in CrouchedState (or stops walking into the wall, but
    // certainly NOT WallSliding).
    [Fact]
    public void CrouchAndWalkIntoWall_DoesNotEnterWallSliding()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(116f, 24f),
            StartVelocity = new Vector2(0f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true, Down = true }),
            Frames        = 60,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "crouch_walk_into_wall", outputDir: null);

        bool sawWallSlide = false;
        foreach (var f in frames)
            if (f.State.Contains("WallSliding")) sawWallSlide = true;

        Assert.False(sawWallSlide, "Crouching player against wall entered WallSlidingState");
    }

    // ── Vault immediately after exiting crouch ─────────────────────────────
    // BUG: when the body's Y is still at "crouch height" (lower than standing
    // rest), ParkourState fires from too low a position and the path becomes
    // infeasible — body gets stuck against the wall.
    [Fact]
    public void ExitCrouch_ThenVault_DoesNotStick()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // 30 frames of crouching while walking right toward the wall (low speed
        // because CrouchMaxWalkSpeed < MaxWalkSpeed). Then release Down and
        // continue Right — body Y is still low when ParkourState fires.
        var script = new InputScript()
            .For(30, new PlayerInput { Right = true, Down = true })
            .Forever(new PlayerInput { Right = true });

        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(40f, 24f),
            StartVelocity = new Vector2(0f, 0f),
            Script        = script,
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "exit_crouch_then_vault", outputDir: null);

        var last = frames[^1];
        Assert.True(last.X > 144f, $"Body got stuck — final X={last.X:F1} (expected past corner X=144)");
    }

    // ── Run through the jagged corridor in Levels/course.txt ──────────────
    // A long stress test: floor pillars + ceiling stalactites alternating across
    // the corridor. Player holds Right with running momentum from one end. This
    // chains vault/duck transitions and is the canonical "feels miserable" case
    // — running through it should feel close to flat-ground speed if state
    // transitions don't bleed off momentum.
    [Fact]
    public void HoldRight_CourseCorridor_RunsThrough()
    {
        // Mirrors Levels/course.txt:
        //   row 10        — corridor ceiling (solid)
        //   row 11 cols 2,6,10,15  — stalactites hanging into the corridor
        //   row 12        — corridor air gap
        //   row 13 cols 0,4,8,12,13 — floor pillars (vault targets)
        //   row 14        — corridor floor (with pit at cols 10-11)
        //   row 15        — solid bottom
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX
            OOXOOOXOOOXOOOOX
            OOOOOOOOOOOOOOOO
            XOOOXOOOXOOOXXOO
            XXXXXXXXXXOOXXXX
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Start in col 1 just past the first pillar (col 0), at standing-rest
        // height above the corridor floor (floor top y=224, body center 24 above).
        // Running momentum so the trace shows chained-obstacle behavior, not
        // standstill-vault behavior (which has its own dedicated tests above).
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(24f, 200f),
            StartVelocity = new Vector2(200f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 240,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "course_corridor", outputDir: null);

        // Average forward speed: total X advanced / time elapsed. On flat ground
        // this would be ~MaxWalkSpeed = 200 px/s. Expect at least 60 px/s through
        // the obstacle course as a sanity floor — anything below that means the
        // body is stalling on a transition somewhere.
        var last      = frames[^1];
        float advance = last.X - cfg.StartPosition.X;
        float elapsed = last.T;
        float avgVx   = advance / elapsed;
        output.WriteLine($"Average Vx over course: {avgVx:F1} px/s ({advance:F0} px in {elapsed:F2}s)");
        Assert.True(avgVx > 60f, $"Body stalled — avg Vx={avgVx:F1} px/s (want > 60)");
    }

    // ── Walk into a wall too tall to vault ─────────────────────────────────
    // Player approaches a 3-block-tall wall. The upper-corner detector should
    // NOT fire (corner is out of reach), so ParkourState should not initiate.
    // Failure mode: parkour does fire, path is impossible, body deadlocks.
    [Fact]
    public void HoldRight_TallWall_StaysInFreeStates()
    {
        // 3-block-tall wall starting at col 8, ground at row 5.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            OOOOOOOOXXXXXXXXOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Ground top = row 5 = y:80. center.Y = 80 - 12 = 68.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(12f, 68f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "tall_wall", outputDir: null);

        // Body should NOT enter ParkourState (corner unreachable). It should
        // walk into the wall and stop. Specifically: never visit Parkour.
        bool visitedParkour = false;
        foreach (var f in frames)
            if (f.State.Contains("Parkour")) visitedParkour = true;

        Assert.False(visitedParkour, "ParkourState fired against unreachable corner");
    }
}
