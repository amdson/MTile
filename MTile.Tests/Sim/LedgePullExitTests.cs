using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Scenario coverage for Plans/LEDGE_PULL_INPUT_MATRIX.md — what happens when the
// player releases Up / presses jump / holds directions while LedgePullState is
// executing. The headline bug (row D): releasing Up mid-pull used to dump the body
// into FallingState carrying the pull's full upward velocity, reading as a free
// jump. Now it re-grabs the ledge and the hang's spring/damper absorbs the
// velocity through the contact.
public class LedgePullExitTests(ITestOutputHelper output)
{
    private const float Dt      = 1f / 30f;
    private const float Gravity = 600f;
    private const float TS      = Chunk.TileSize; // 16

    // 2-tile-tall wall on the right, top tile at the grounded body's head — the
    // canonical graspable ledge (same geometry as LedgeGrabFlickerTests).
    // Corner inner edge ≈ (144, 48); hang Y ≈ 57.5; standing-height line ≈ 29.
    private static ChunkMap BuildLedgeTerrain() => SimTerrain.FromAscii(@"
            ..........
            ..........
            ..........
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    private const float GroundTop    = 5 * TS;   // 80
    private const float WallLeft     = 9 * TS;   // 144
    private const float CornerTopY   = 3 * TS;   // 48
    private const float HalfW        = 8.227f;
    private static float StandingLineY => CornerTopY - 2f * PlayerCharacter.Radius; // ~29

    // Settle → tap Up (grab) → hang → press Up again (pull). Callers append the
    // mid-pull input under test. Pull starts ~frame 43.
    private static InputScript GrabThenPull(int pullFramesHeld, PlayerInput then)
        => new InputScript()
            .For(30, new PlayerInput { })                    // settle to Standing
            .For(3,  new PlayerInput { Up = true })          // tap Up → LedgeGrab
            .For(10, new PlayerInput { })                    // hang
            .For(pullFramesHeld, new PlayerInput { Up = true })
            .Forever(then);

    private SimFrame[] Run(InputScript script, int frames = 150)
    {
        var cfg = new SimConfig
        {
            Terrain       = BuildLedgeTerrain(),
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = script,
            Frames        = frames,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var trace = SimRunner.Run(cfg);
        SimReport.Print(trace, output, fullTable: false);
        return trace;
    }

    private static int FirstFrame(SimFrame[] trace, string state, int from = 0)
    {
        for (int i = from; i < trace.Length; i++)
            if (trace[i].State == state) return i;
        return -1;
    }

    private static int LastFrame(SimFrame[] trace, string state)
    {
        for (int i = trace.Length - 1; i >= 0; i--)
            if (trace[i].State == state) return i;
        return -1;
    }

    // ── Row D: release Up midway up — re-grab, settle back to the hang ─────────

    [Fact]
    public void ReleaseUpMidPull_RegrabsAndSettlesAtHang()
    {
        var trace = Run(GrabThenPull(4, new PlayerInput { }));

        int pullStart = FirstFrame(trace, "LedgePullState");
        Assert.True(pullStart > 0, "pull never started");
        int pullEnd = LastFrame(trace, "LedgePullState");

        // The frame after the pull ends must already be the re-grab — no airborne gap.
        Assert.Equal("LedgeGrabState", trace[pullEnd + 1].State);

        // Never rose to standing height: the abandoned pull must not read as a jump.
        float minY = trace.Skip(pullStart).Min(f => f.Y);
        Assert.True(minY > StandingLineY + 2f,
            $"body reached {minY}, above the standing line {StandingLineY} — looks like a free jump");

        // Settled: final frames hang in LedgeGrabState with ~zero velocity near hang Y.
        var last = trace[^1];
        Assert.Equal("LedgeGrabState", last.State);
        Assert.True(MathF.Abs(last.Vy) < 10f, $"still moving at end of run: vy={last.Vy}");
        float hangY = CornerTopY + PlayerCharacter.Radius;
        Assert.True(MathF.Abs(last.Y - hangY) < 4f, $"ended at Y={last.Y}, expected hang ≈ {hangY}");
    }

    // ── Row N: pull blocked past MaxVaultTime — sags back to the hang ──────────

    [Fact]
    public void PullTimeout_RegrabsInsteadOfFallingOut()
    {
        // Floating overhang one tile above the lip (row 1, directly in the body's
        // mantle path) so the pull physically can't crest the corner — the body
        // stalls under it until MaxVaultTime expires, then sags back to the hang.
        // (Two tiles up, as before, left enough room to stand on the 1-wide pillar.)
        var terrain = SimTerrain.FromAscii(@"
            ..........
            .........X
            ..........
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = GrabThenPull(60, new PlayerInput { }),   // hold Up well past MaxVaultTime
            Frames        = 150,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var trace = SimRunner.Run(cfg);
        SimReport.Print(trace, output, fullTable: false);

        int pullEnd = LastFrame(trace, "LedgePullState");
        Assert.True(pullEnd > 0, "pull never started");
        Assert.Equal("LedgeGrabState", trace[pullEnd + 1].State);
    }

    // ── Row G: release Up while holding Down — clean climb-down from the hang ──

    [Fact]
    public void ReleaseUpHoldingDown_DropsFromHangWithoutJumpVelocity()
    {
        var trace = Run(GrabThenPull(4, new PlayerInput { Down = true }));

        int pullEnd = LastFrame(trace, "LedgePullState");
        Assert.True(pullEnd > 0, "pull never started");

        // Re-grab first (grace ~3 frames), then the held Down releases the hang.
        Assert.Equal("LedgeGrabState", trace[pullEnd + 1].State);
        int exit = pullEnd + 1;
        while (exit < trace.Length && trace[exit].State == "LedgeGrabState") exit++;
        Assert.True(exit < trace.Length, "never released the hang");

        // The exit must be a descent, not a jump: in this geometry the hang sits just
        // above the floor, so a clean drop lands as Crouched/Standing almost at once.
        Assert.Contains(trace[exit].State, new[] { "FallingState", "CrouchedState", "StandingState" });
        Assert.True(trace[exit].Vy > -60f,
            $"left the hang with vy={trace[exit].Vy} — pull velocity leaked into the exit");

        // Never rose to standing height, and ends grounded (Down held → crouched).
        float minY = trace.Skip(pullEnd - 4).Min(f => f.Y);
        Assert.True(minY > StandingLineY + 2f, $"rose to {minY} — looks like a jump");
        Assert.Contains(trace[^1].State, new[] { "CrouchedState", "StandingState" });
    }

    // ── Row F: release Up while holding away — grace absorbs, then a soft exit ──

    [Fact]
    public void ReleaseUpHoldingAway_GraceDampsBeforeExit()
    {
        // Wall is on the right (+1) → Left is away.
        var trace = Run(GrabThenPull(4, new PlayerInput { Left = true }));

        int pullEnd = LastFrame(trace, "LedgePullState");
        Assert.True(pullEnd > 0, "pull never started");
        Assert.Equal("LedgeGrabState", trace[pullEnd + 1].State);

        int falling = FirstFrame(trace, "FallingState", pullEnd + 1);
        Assert.True(falling > 0, "away-press never dropped from the re-grab");

        // The grace window let the damper eat most of the pull's vy.
        Assert.True(trace[falling].Vy > -60f,
            $"exited the re-grab with vy={trace[falling].Vy} — damper didn't absorb the pull");
        float minY = trace.Skip(pullEnd - 4).Min(f => f.Y);
        Assert.True(minY > StandingLineY + 2f, $"rose to {minY} — looks like a jump");
    }

    // ── Row I: jump queued mid-pull fires LedgeJump at the top, no ground hop ──

    [Fact]
    public void JumpQueuedMidPull_FiresLedgeJumpAtTop()
    {
        // Press-and-hold Space two frames into the pull, Up stays held.
        var script = new InputScript()
            .For(30, new PlayerInput { })
            .For(3,  new PlayerInput { Up = true })
            .For(10, new PlayerInput { })
            .For(2,  new PlayerInput { Up = true })
            .Forever(new PlayerInput { Up = true, Space = true });
        var trace = Run(script);

        int pullStart = FirstFrame(trace, "LedgePullState");
        int ledgeJump = FirstFrame(trace, "LedgeJumpState");
        Assert.True(pullStart > 0, "pull never started");
        Assert.True(ledgeJump > pullStart, "queued jump never fired LedgeJumpState");

        // Queued, not instant: the press happened ~2 frames into the pull but the
        // jump waited for the body to reach standing height beside the lip.
        Assert.True(ledgeJump >= pullStart + 3, "LedgeJump fired before the top of the pull");
        Assert.True(trace[ledgeJump - 1].State == "LedgePullState",
            "something other than the pull preceded the ledge jump");
        Assert.True(trace[ledgeJump].Y <= StandingLineY + 3f,
            $"LedgeJump fired at Y={trace[ledgeJump].Y}, below the lip's standing line {StandingLineY}");

        // The servo brakes the pull's surplus vy: from launch onward the body never
        // exceeds the target (-210) by more than per-frame slack.
        float fastest = trace.Skip(ledgeJump).Min(f => f.Vy);
        Assert.True(fastest >= MovementConfig.Current.LedgeJumpTargetVy - 25f,
            $"vy reached {fastest} after launch — pull velocity stacked onto the jump");

        // No ground-jump detour and no wall-jump misfire.
        Assert.DoesNotContain(trace.Skip(pullStart).Take(ledgeJump - pullStart), f => f.State == "StandingState");
        Assert.DoesNotContain(trace, f => f.State is "WallJumpingState" or "DoubleJumpingState");
    }

    // ── Row K: Space + inward queues for the top instead of wall-jumping away ──

    [Fact]
    public void JumpPlusInwardMidPull_QueuesAndLandsOnTop()
    {
        // Same ledge but with a wide platform on top, so landing inward has
        // somewhere to stand.
        var terrain = SimTerrain.FromAscii(@"
            .........................
            .........................
            .........................
            .........XXXXXXXXXXXXXXXX
            .........XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Wall on the right → Right is inward.
        var script = new InputScript()
            .For(30, new PlayerInput { })
            .For(3,  new PlayerInput { Up = true })
            .For(10, new PlayerInput { })
            .For(2,  new PlayerInput { Up = true })
            .Forever(new PlayerInput { Up = true, Right = true, Space = true });
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = script,
            Frames        = 150,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var trace = SimRunner.Run(cfg);
        SimReport.Print(trace, output, fullTable: false);

        Assert.DoesNotContain(trace, f => f.State == "WallJumpingState");
        int ledgeJump = FirstFrame(trace, "LedgeJumpState");
        Assert.True(ledgeJump > 0, "inward+Space never produced a LedgeJump");

        // Holding inward through the launch carries the body onto the platform.
        int landed = FirstFrame(trace, "StandingState", ledgeJump);
        Assert.True(landed > 0, "never landed after the ledge jump");
        Assert.True(trace[landed].X > WallLeft, $"landed at X={trace[landed].X}, not on top of the ledge");
        Assert.True(trace[landed].Y < CornerTopY, $"landed at Y={trace[landed].Y}, not standing on the lip");
    }

    // ── Row J: Space + away mid-pull still bails out as a wall jump ────────────

    [Fact]
    public void JumpPlusAwayMidPull_BailsAsWallJump()
    {
        var script = new InputScript()
            .For(30, new PlayerInput { })
            .For(3,  new PlayerInput { Up = true })
            .For(10, new PlayerInput { })
            .For(2,  new PlayerInput { Up = true })
            .Forever(new PlayerInput { Up = true, Left = true, Space = true });
        var trace = Run(script);

        int pullStart = FirstFrame(trace, "LedgePullState");
        int wallJump  = FirstFrame(trace, "WallJumpingState");
        Assert.True(pullStart > 0, "pull never started");
        Assert.True(wallJump > pullStart && wallJump <= pullStart + 4,
            $"away+Space should bail immediately (pull at {pullStart}, wall jump at {wallJump})");
    }
}
