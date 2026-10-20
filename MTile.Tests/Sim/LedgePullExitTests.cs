using System;
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
    private const float Dt      = 1f / 60f;
    private const float Gravity = 600f;
    private const float TS      = Chunk.TileSize; // 16

    // 2-tile-tall wall on the right, top tile at the grounded body's head — the
    // canonical graspable ledge (same geometry as LedgeGrabFlickerTests).
    // Corner inner edge ≈ (144, 48); hang Y ≈ 57.5; standing-height line ≈ 29.
    private static ChunkMap BuildLedgeTerrain() => SimTerrain.FromAscii(@"
            ..........
            ..........
            .........X
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    private const float GroundTop    = 5 * TS;   // 80
    private const float WallLeft     = 9 * TS;   // 144
    private const float CornerTopY   = 2 * TS;   // 32 (3-tall wall: lip at the R=12 head line)
    private const float HalfW        = 10.392f;  // a 10.4 px standoff from the wall, NOT the body half-width (~5 px since the 0.485 width scale) — BACKLOG 5.19
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

    // Same lip (corner top at CornerTopY, wall face at WallLeft) on a 5-tall wall,
    // so a body can be BELOW the lip beside the wall — the rising-grab start.
    private static ChunkMap BuildTallLedgeTerrain() => SimTerrain.FromAscii(@"
            ..........
            ..........
            .........X
            .........X
            .........X
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    private SimFrame[] Run(InputScript script, int frames = 150)
        => Run(script, BuildLedgeTerrain(),
               new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius), Vector2.Zero, frames);

    // Beside the tall wall, two radii below the lip, launched upward.
    private SimFrame[] RunRising(InputScript script, float vy, int frames)
        => Run(script, BuildTallLedgeTerrain(),
               new Vector2(WallLeft - HalfW, CornerTopY + 2f * PlayerCharacter.Radius), new Vector2(0f, vy), frames);

    private SimFrame[] Run(InputScript script, ChunkMap terrain, Vector2 start, Vector2 startVel, int frames)
    {
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = start,
            StartVelocity = startVel,
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

    // ── Hold-to-climb: the press that grabs, held, rolls into the pull ─────────

    [Fact]
    public void HoldUpFromGround_GrabsThenPullsOverWithoutRepress()
    {
        var trace = Run(new InputScript()
            .For(30, new PlayerInput { })
            .Forever(new PlayerInput { Up = true }));

        int grab = FirstFrame(trace, "LedgeGrabState");
        int pull = FirstFrame(trace, "LedgePullState");
        Assert.True(grab > 0, "never grabbed");
        Assert.True(pull > grab, "held Up never rolled the grab into a pull");
        // Pull waits out the tap window, then starts — no long hang in between.
        float wait = (pull - grab) * Dt;
        Assert.True(wait >= MovementConfig.Current.LedgeAutoPullHoldTime - Dt && wait < 0.4f,
            $"pull started {wait:F3}s after the grab");
        // Carried up and over: ends standing on top of the ledge, not hanging.
        var last = trace[^1];
        Assert.Equal("StandingState", last.State);
        Assert.True(last.X > WallLeft && last.Y < CornerTopY - 18f,
            $"ended at ({last.X:F1}, {last.Y:F1}), not on top of the lip at ({WallLeft}, {CornerTopY})");
    }

    [Fact]
    public void TapUpFromGround_JustHangs()
    {
        var trace = Run(new InputScript()
            .For(30, new PlayerInput { })
            .For(3,  new PlayerInput { Up = true })   // 0.1 s tap
            .Forever(new PlayerInput { }), frames: 90);

        Assert.True(FirstFrame(trace, "LedgeGrabState") > 0, "never grabbed");
        Assert.Equal(-1, FirstFrame(trace, "LedgePullState"));
        Assert.Equal("LedgeGrabState", trace[^1].State);
    }

    // ── Hold-to-climb keeps momentum: a grab made while rising ───────────────
    // Rising past the lip at 250 px/s with Up held: the grab must not catch the
    // rise, and the pull (a path, not a clock) must not brake it to its pace —
    // the body keeps rising until it is over the lip.

    [Fact]
    public void RisingGrab_HoldUp_CarriesMomentumOverTheLip()
    {
        var trace = RunRising(InputScript.Always(new PlayerInput { Up = true }), -250f, frames: 40);

        int grab = FirstFrame(trace, "LedgeGrabState");
        int pull = FirstFrame(trace, "LedgePullState");
        Assert.True(grab >= 0 && pull > grab, $"grab f{grab}, pull f{pull}");
        // Rising every frame from launch until above the standing line: no catch.
        int top = Array.FindIndex(trace, f => f.Y < StandingLineY);
        Assert.True(top > 0, "never rose above the standing line");
        Assert.All(trace.Take(top + 1), f => Assert.True(f.Vy < 0f, $"f{f.Frame}: stopped rising (vy={f.Vy:F1}) below the lip"));
        // Gravity is the only brake while coasting: vy at the pull's start is what
        // the launch leaves after (pull − 0) frames of g, not a caught hang.
        float coastVy = -250f + Gravity * Dt * (pull + 1);
        Assert.True(trace[pull - 1].Vy < coastVy + 15f,
            $"vy entering the pull {trace[pull - 1].Vy:F1}, gravity-only coast ≈ {coastVy:F1}");
        var last = trace[^1];
        Assert.Equal("StandingState", last.State);
        Assert.True(last.X > WallLeft && last.Y < CornerTopY - 18f,
            $"ended at ({last.X:F1}, {last.Y:F1}), not on top of the lip");
    }

    [Fact]
    public void RisingGrab_TapUp_CatchesIntoTheHang()
    {
        var trace = RunRising(new InputScript()
            .For(3, new PlayerInput { Up = true })
            .Forever(new PlayerInput { }), -250f, frames: 60);

        Assert.Equal(-1, FirstFrame(trace, "LedgePullState"));
        // Caught impulsively when the tap resolves (Up up on frame 3): no coasting
        // on upward past the release point.
        float releaseY = trace[3].Y;
        Assert.True(trace.Skip(3).Min(f => f.Y) > releaseY - 1f,
            $"kept rising after the tap released at y={releaseY:F1} (min {trace.Skip(3).Min(f => f.Y):F1})");
        var last = trace[^1];
        Assert.Equal("LedgeGrabState", last.State);
        float hangY = CornerTopY + PlayerCharacter.Radius;
        Assert.True(MathF.Abs(last.Y - hangY) < 4f && MathF.Abs(last.Vy) < 10f,
            $"ended at Y={last.Y:F1} vy={last.Vy:F1}, expected settled at hang ≈ {hangY}");
    }

    // ── Pull from above the hang: Up pressed where the pull would carry the body ──
    // Rising past the lip beside the wall (no Up yet), then a press: the pull
    // starts directly — no hang first — and carries the body onto the top.

    [Theory]
    [InlineData(2)]    // just above the hang line
    [InlineData(5)]    // beside the lip
    [InlineData(8)]    // near the crest
    public void UpPressedAboveTheHang_PullsDirectly(int pressFrame)
    {
        var trace = RunRising(new InputScript()
            .For(pressFrame, new PlayerInput { })
            .Forever(new PlayerInput { Up = true }), -280f, frames: 50);

        int pull = FirstFrame(trace, "LedgePullState");
        Assert.True(pull >= pressFrame && pull <= pressFrame + 1, $"pull started f{pull}, Up pressed f{pressFrame}");
        Assert.True(trace[pull].Y < CornerTopY + PlayerCharacter.Radius,
            $"pull started at y={trace[pull].Y:F1}, at or below the hang line");
        Assert.Equal(-1, FirstFrame(trace, "LedgeGrabState"));
        var last = trace[^1];
        Assert.Equal("StandingState", last.State);
        Assert.True(last.X > WallLeft && last.Y < CornerTopY - 18f,
            $"ended at ({last.X:F1}, {last.Y:F1}), not on top of the lip");
    }

    // ── The pull completes inside its timeout at the game's own rate ─────────
    // Every other test here runs at 1/30; the game steps at 1/60. A pull that
    // ground up the corner (no actuator pushing off the wall) or paced too slowly
    // ran out MaxLipManeuverTime and re-grabbed — "moves up, gives up, falls back".

    [Fact]
    public void PullFromHang_At60Hz_CompletesInsideTheTimeout()
    {
        const float dt60 = 1f / 60f;
        var trace = SimRunner.Run(new SimConfig
        {
            Terrain       = BuildLedgeTerrain(),
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = new InputScript()
                .For(60, new PlayerInput { })
                .For(5,  new PlayerInput { Up = true })   // tap → grab
                .For(30, new PlayerInput { })             // hang
                .Forever(new PlayerInput { Up = true }),  // hold → pull
            Frames        = 200,
            Dt            = dt60,
            Gravity       = new Vector2(0f, Gravity),
        });

        int pullStart = FirstFrame(trace, "LedgePullState");
        int pullEnd   = LastFrame(trace, "LedgePullState");
        Assert.True(pullStart > 0, "pull never started");
        float pullTime = (pullEnd - pullStart + 1) * dt60;
        Assert.True(pullTime < MovementConfig.Current.MaxLipManeuverTime - 0.05f,
            $"pull took {pullTime:F3}s — at the {MovementConfig.Current.MaxLipManeuverTime}s timeout");
        Assert.Equal(1, trace.Count(f => f.State == "LedgePullState" && trace[f.Frame - 1].State != "LedgePullState"));
        var last = trace[^1];
        Assert.Equal("StandingState", last.State);
        Assert.True(last.X > WallLeft && last.Y < CornerTopY - 18f,
            $"ended at ({last.X:F1}, {last.Y:F1}), not on top of the lip");
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

    // ── Row N: pull blocked past MaxLipManeuverTime — sags back to the hang ──────────

    [Fact]
    public void PullTimeout_RegrabsInsteadOfFallingOut()
    {
        // Floating overhang directly in the body's mantle path, positioned at the
        // row containing StandingLineY (the head height the body would reach if it
        // crested the lip) — so the pull physically can't crest the corner. The
        // body's grown to ~3 tiles tall on this grid, so StandingLineY now sits a
        // row above the lip's row0 (was row0 itself on the old 2-tile-tall body);
        // an extra empty row keeps the same physical "can't fit" gap below it.
        // (Lip is row 2 with the 3-tall R=12 wall, same as BuildLedgeTerrain.)
        // The overhang spans 3 columns: with PullCompleted's 0.7R buffer a
        // single-tile overhang left a notch the body could squeeze into beside it,
        // "completing" the pull below the block — this test is about a pull that
        // genuinely cannot crest.
        var terrain = SimTerrain.FromAscii(@"
            .......XXX
            ..........
            ..........
            .........X
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: -1);
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = GrabThenPull(60, new PlayerInput { }),   // hold Up well past MaxLipManeuverTime
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

    // ── Row I: a jump mid-pull launches off the corner at once, no ground hop ──

    [Fact]
    public void JumpMidPull_LaunchesOffTheCornerImmediately()
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
        Assert.True(ledgeJump > pullStart, "jump mid-pull never fired LedgeJumpState");

        // From any point of the pull: the press lands ~2 frames in, and the corner
        // launch takes over that frame — below the lip, not waiting for the top.
        Assert.True(ledgeJump <= pullStart + 3, $"LedgeJump waited {ledgeJump - pullStart} frames into the pull");
        Assert.Equal("LedgePullState", trace[ledgeJump - 1].State);
        Assert.True(trace[ledgeJump].Y > StandingLineY,
            $"LedgeJump fired at Y={trace[ledgeJump].Y}, already at the standing line");
        // Powered past the lip: the legs clear the corner.
        float legReach = PlayerCharacter.StandingHeight - PlayerCharacter.Radius;
        Assert.True(trace.Skip(ledgeJump).Min(f => f.Y) + legReach < CornerTopY,
            "the corner launch never lifted the feet above the lip");

        // The servo brakes the pull's surplus vy: from launch onward the body never
        // exceeds the target (-210) by more than per-frame slack.
        float fastest = trace.Skip(ledgeJump).Min(f => f.Vy);
        Assert.True(fastest >= MovementConfig.Current.LedgeJumpTargetVy - 25f,
            $"vy reached {fastest} after launch — pull velocity stacked onto the jump");

        // No ground-jump detour and no wall-jump misfire.
        Assert.DoesNotContain(trace.Skip(pullStart).Take(ledgeJump - pullStart), f => f.State == "StandingState");
        Assert.DoesNotContain(trace, f => f.State is "WallJumpingState" or "DoubleJumpingState");
    }

    // ── Jump from the hang: powered off the corner until the legs clear it ──────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JumpFromHang_PowersOffTheCorner(bool inward)
    {
        // Wide platform on top so an inward jump has somewhere to land.
        var terrain = SimTerrain.FromAscii(@"
            .........................
            .........................
            .........XXXXXXXXXXXXXXXX
            .........XXXXXXXXXXXXXXXX
            .........XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var trace = Run(new InputScript()
                .For(30, new PlayerInput { })
                .For(3,  new PlayerInput { Up = true })                          // tap → grab
                .For(10, new PlayerInput { })                                    // hang
                .For(3,  new PlayerInput { Space = true, Right = inward })       // tap jump
                .Forever(new PlayerInput { Right = inward }),
            terrain, new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius), Vector2.Zero, 90);

        int grab = FirstFrame(trace, "LedgeGrabState");
        int jump = FirstFrame(trace, "LedgeJumpState");
        Assert.True(grab > 0 && jump > grab, $"grab f{grab}, ledge jump f{jump}");
        Assert.DoesNotContain(trace, f => f.State == "WallJumpingState");
        // Committed past a tap: still powered after Space came up, until the legs
        // are above the corner — and well above it at the apex.
        float legReach = PlayerCharacter.StandingHeight - PlayerCharacter.Radius;
        int end = FirstFrame(trace, "FallingState", jump);
        if (end < 0) end = FirstFrame(trace, "StandingState", jump);
        Assert.True(end > jump + 3, $"ledge jump ended {end - jump} frames in — a tap cut it short");
        Assert.True(trace[end - 1].Y + legReach <= CornerTopY + 2f,
            $"launch ended at y={trace[end - 1].Y:F1}, feet below the lip");
        if (inward)
        {
            int landed = FirstFrame(trace, "StandingState", jump);
            Assert.True(landed > 0 && trace[landed].X > WallLeft && trace[landed].Y < CornerTopY,
                "inward jump from the hang did not land on top");
        }
        else
        {
            float apexAboveLip = CornerTopY - trace.Skip(jump).Min(f => f.Y);
            Assert.True(apexAboveLip > 40f, $"apex only {apexAboveLip:F1}px above the lip — not a powerful jump");
        }
    }

    // ── Row K: Space + inward launches up onto the ledge instead of wall-jumping away ──

    [Fact]
    public void JumpPlusInwardMidPull_QueuesAndLandsOnTop()
    {
        // Same ledge but with a wide platform on top, so landing inward has
        // somewhere to stand. (3-tall like BuildLedgeTerrain — lip at the R=12 head line.)
        var terrain = SimTerrain.FromAscii(@"
            .........................
            .........................
            .........XXXXXXXXXXXXXXXX
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
