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

    // ── Vault velocity profile: Vx and |v| over time ──────────────────────
    // Player runs into a one-block step at walking speed. A good vault should be
    // roughly speed-preserving: |v| shouldn't spike during the climb and the body
    // shouldn't exit ParkourState noticeably faster than it entered. Prints Vx,
    // Vy, |v| and State per frame so the profile is visible; asserts the bounds.
    [Fact]
    public void HoldRight_VaultOneBlock_VelocityProfile()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Ground top y=48; start a bit back at standing-rest height, running right
        // at walking speed, so the body is settled in StandingState before it
        // reaches the step face at x=128.
        const float WalkSpeed = 100f; // MovementConfig default MaxWalkSpeed
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(60f, 29f),
            StartVelocity = new Vector2(WalkSpeed, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 90,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);

        // Per-frame Vx / Vy / |v| / State table.
        output.WriteLine($"{"Frame",6} {"T(s)",6} {"X",8} {"Y",8} {"Vx",8} {"Vy",8} {"|v|",8}  State");
        output.WriteLine(new string('─', 78));
        foreach (var f in frames)
        {
            float speed = MathF.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
            string marker = f.Transition ? "→" : " ";
            output.WriteLine($"{f.Frame,6} {f.T,6:F3} {f.X,8:F1} {f.Y,8:F1} {f.Vx,8:F1} {f.Vy,8:F1} {speed,8:F1}  {marker}{f.State}");
        }
        SimReport.WriteCsv(frames, "vault_velocity_profile", outputDir: null);

        // Locate the ParkourState window.
        int pStart = -1, pEnd = -1;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].State.Contains("Parkour")) { if (pStart < 0) pStart = i; pEnd = i; }
        Assert.True(pStart > 0, "Body never entered ParkourState");

        float Speed(SimFrame f) => MathF.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
        float entrySpeed = Speed(frames[pStart - 1]);

        // First Standing frame after the vault — and a short settling window after it. (Don't scan
        // to the end: this terrain's platform is finite, so the body eventually walks off and
        // FallingState's free-fall inflates |v| unboundedly, which isn't what we're measuring.)
        int firstStandAfter = -1;
        for (int i = pEnd + 1; i < frames.Length && i <= pEnd + 15; i++)
            if (frames[i].State.Contains("Standing")) { firstStandAfter = i; break; }
        Assert.True(firstStandAfter > 0, "Body never returned to StandingState after the vault");
        int windowEnd = Math.Min(frames.Length, firstStandAfter + 8);

        // |v| must not spike during the vault or just after it.
        float peakSpeed = 0f;
        for (int i = pStart; i < windowEnd; i++) peakSpeed = MathF.Max(peakSpeed, Speed(frames[i]));
        // Lowest |v| during the vault itself — captures the "slows down to execute parkour" half.
        float troughSpeed = float.MaxValue;
        for (int i = pStart; i <= pEnd; i++) troughSpeed = MathF.Min(troughSpeed, Speed(frames[i]));
        float exitSpeed = Speed(frames[firstStandAfter]);
        output.WriteLine($"\nentrySpeed={entrySpeed:F1}  troughSpeed(in vault)={troughSpeed:F1}  exitSpeed(first Standing, frame {firstStandAfter})={exitSpeed:F1}  peakSpeed(vault+8)={peakSpeed:F1}");

        // A vault should be roughly speed-preserving: no big dip during, no spike on exit.
        Assert.True(troughSpeed >= entrySpeed * 0.7f,
            $"Body slowed down during the vault: trough={troughSpeed:F1}, entry={entrySpeed:F1} (want ≥ {entrySpeed * 0.7f:F1})");
        Assert.True(peakSpeed <= entrySpeed * 1.4f,
            $"|v| spiked around the vault: peak={peakSpeed:F1}, entry={entrySpeed:F1} (want ≤ {entrySpeed * 1.4f:F1})");
        Assert.True(exitSpeed >= entrySpeed * 0.8f && exitSpeed <= entrySpeed * 1.3f,
            $"Exit speed off: exit={exitSpeed:F1}, entry={entrySpeed:F1} (want {entrySpeed * 0.8f:F1}..{entrySpeed * 1.3f:F1})");
    }

    // ── Covered jump: hold Space + Right inside a 2-tile tunnel, exit, then jump ──
    // The player runs right inside a 2-tile-tall corridor (the lowest ceiling regular jump can't
    // clear), holding Space. CoveredJumpState only engages once the body's leading edge has actually
    // pushed past the overhang's lip (so it doesn't kick in deep inside). When it does, phase 1
    // slides the body the rest of the way out (Under ramp insures against head clipping); the moment
    // nothing's overhead it flips to phase 2 — a real jump (no double-jump burn, body arcs up well
    // above the corridor's interior).
    [Fact]
    public void HoldSpaceRight_CoveredJumpOutOfTunnel()
    {
        // tile = 16px. Floor top = row 5 = y:80. Ceiling bottom = row 2 bottom = y:48 (2-tile corridor:
        // gap 32 — exactly the "low ceiling" range CoveredJump owns; regular Jump's gap check blocks it
        // here). Ceiling cols 0-14, ends at col 14 ⇒ body becomes "fully out" past x=240 ⇒ phase 2 jump.
        //
        //   0         1         2
        //   0123456789012345678901
        // 0 OOOOOOOOOOOOOOOOOOOO     air (open above the exit)
        // 1 OOOOOOOOOOOOOOOOOOOO     air
        // 2 XXXXXXXXXXXXXXXOOOOO     tunnel ceiling, cols 0-14, bottom y:48
        // 3 OOOOOOOOOOOOOOOOOOOO     corridor air
        // 4 OOOOOOOOOOOOOOOOOOOO     corridor air
        // 5 XXXXXXXXXXXXXXXXXXXX     floor
        // 6 XXXXXXXXXXXXXXXXXXXX     solid
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Start at col 8 (x=136), at standing-rest height ≈ 80 - 19.5 = 60.5 (head at 51 ⇒ 3px clear
        // of the y=48 ceiling), running right at walk speed, holding Space the whole time.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(136f, 60.5f),
            StartVelocity = new Vector2(100f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true, Space = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "covered_jump_tunnel", outputDir: null);

        // Must visit CoveredJumpState.
        int cjStart = -1, cjEnd = -1;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].State.Contains("CoveredJump")) { if (cjStart < 0) cjStart = i; cjEnd = i; }
        Assert.True(cjStart >= 0, "Body never entered CoveredJumpState");

        // Must not deadlock on the lip — it should advance well past the lip's far edge (x=240).
        var last = frames[^1];
        Assert.True(last.X > 256f, $"Body got stuck near the lip — final X={last.X:F1} (expected past x=256)");

        // Must actually jump: at some point the body rises clearly above the standing-rest height it
        // started at (≈ Y 56). A pure walk/duck never does this; a real jump arcs the body up.
        float minY = float.MaxValue;
        for (int i = cjStart; i < frames.Length; i++) minY = MathF.Min(minY, frames[i].Y);
        Assert.True(minY < 40f, $"Body never jumped — min Y after CoveredJump entry was {minY:F1} (expected < 40)");

        // The jump should be a launch state that bleeds into free fall — Falling should appear after.
        bool fellAfter = false;
        for (int i = cjEnd + 1; i < frames.Length; i++) if (frames[i].State.Contains("Falling")) { fellAfter = true; break; }
        Assert.True(fellAfter, "Expected FallingState after the covered jump's hold window ended");
    }

    // ── Dropdown: hold Down + Right near a platform edge → slip off, then fall ──
    // The player starts on top of a finite platform near its right edge and holds Down + Right.
    // DropdownState should engage (preempting Standing/Crouched), apply a horizontal slide force to
    // push the body the rest of the way over the edge, drop the float-height ground constraint so
    // the body's no longer spring-held, and (the moment the body's no longer over any floor) exit
    // to FallingState — leaving the body well below and past the platform.
    [Fact]
    public void HoldDownRight_DropsOffPlatformEdge()
    {
        // tile = 16. Platform = rows 5-7 cols 0-7 (top y=80, right edge x=128). Past col 7: open air,
        // body free-falls. Plenty of vertical runway so the test can observe the fall.
        //
        //   0         1
        //   0123456789012345
        // 0 OOOOOOOOOOOOOOOO    air
        // 1 OOOOOOOOOOOOOOOO    air
        // 2 OOOOOOOOOOOOOOOO    air
        // 3 OOOOOOOOOOOOOOOO    air
        // 4 OOOOOOOOOOOOOOOO    air
        // 5 XXXXXXXXOOOOOOOO    platform top (cols 0-7)
        // 6 XXXXXXXXOOOOOOOO    platform body
        // 7 XXXXXXXXOOOOOOOO    platform body
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXOOOOOOOO
            XXXXXXXXOOOOOOOO
            XXXXXXXXOOOOOOOO", originTileX: 0, originTileY: 0);

        // Start at col 6 (x=104), standing-rest height (y≈60.5), at rest. Edge is at x=128 ⇒ body
        // must slide ~32px before its left vertex clears the corner.
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(104f, 60.5f),
            StartVelocity = new Vector2(0f, 0f),
            Script        = InputScript.Always(new PlayerInput { Right = true, Down = true }),
            Frames        = 60,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: true);
        SimReport.WriteCsv(frames, "dropdown_off_edge", outputDir: null);

        // Must visit DropdownState.
        int dropStart = -1, dropEnd = -1;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].State.Contains("Dropdown")) { if (dropStart < 0) dropStart = i; dropEnd = i; }
        Assert.True(dropStart >= 0, "Body never entered DropdownState");

        // After the drop completes, the body must transition to Falling (not back to Standing).
        bool fellAfter = false;
        for (int i = dropEnd + 1; i < frames.Length; i++) if (frames[i].State.Contains("Falling")) { fellAfter = true; break; }
        Assert.True(fellAfter, "Expected FallingState after the dropdown");

        // Body actually fell off and past the edge — final position is below the platform top (80)
        // by a wide margin, and well past the edge x=128.
        var last = frames[^1];
        Assert.True(last.Y > 200f, $"Body should be well below platform — final Y={last.Y:F1}");
        Assert.True(last.X > 200f, $"Body should be well past edge — final X={last.X:F1}");
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
