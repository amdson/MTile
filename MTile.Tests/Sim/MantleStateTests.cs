using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// MantleState (Plans/CORRIDOR_MANEUVER_PLAN.md): the deliberate flush-step climb that owns
// the case the steering ramps' steep-angle taper vacated. Three contracts pinned here:
//   1. Flush + slow + held-direction against a 1-block step → mantle up onto it, no pop.
//   2. A running approach still vaults via ParkourState — the mantle's speed gate keeps it
//      out of at-speed maneuvers (reflex layer regression guard).
//   3. A too-tall wall offers no mantle: the body just stays put (honest bonk).
public class MantleStateTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;

    // One-block step: floor row 3, step row 2 at cols 8..15. Step lip at x=128, top y=32.
    // Standing rest (R=12): center ≈ floorTop − (R + R·sin60°) ≈ floorTop − 22.4 → y ≈ 9 on
    // the step, y ≈ 25 on the lower floor — "on top" asserts use y < 14 to split the two.
    private static ChunkMap StepTerrain() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOXXXXXXXXOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private SimFrame[] Run(ChunkMap terrain, Vector2 start, int frames, float dt = Dt, InputScript script = null)
    {
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = start,
            Script        = script ?? InputScript.Always(new PlayerInput { Right = true }),
            Frames        = frames,
            Dt            = dt,
            Gravity       = new Vector2(0f, 600f),
        };
        var result = SimRunner.Run(cfg);
        foreach (var f in result)
            if (f.Frame % 5 == 0 || f.State.Contains("Mantle"))
                output.WriteLine($"{f.Frame,3} {f.State,-22} x={f.X,7:F2} y={f.Y,6:F2} vx={f.Vx,7:F2} vy={f.Vy,7:F2}");
        return result;
    }

    // Step lip (left edge of the 1-tile step, cols 8..15) at x = 8 * TileSize; step top at
    // y = 2 * TileSize; floor top at y = 3 * TileSize. Standing rest offset (R + R·sin60°,
    // a body-scale constant unrelated to tile size) ≈ 22.39. Rest-on-step ≈ stepTopY − 22.39;
    // rest-on-floor ≈ floorTopY − 22.39; the midpoint between them is the "delivered on top"
    // Y split (mirrors the file's stale 16px-grid derivation, rebuilt for Chunk.TileSize).
    [Fact]
    public void FlushAgainstStep_HoldToward_MantlesOntoTop()
    {
        float lipX = 8 * Chunk.TileSize;
        float stepTopY = 2 * Chunk.TileSize;
        float floorTopY = 3 * Chunk.TileSize;
        float restOffset = PlayerCharacter.Radius + PlayerCharacter.Radius * MathF.Sin(MathF.PI / 3f);
        float onTopY = (stepTopY + floorTopY) / 2f - restOffset; // midpoint split between step-rest and floor-rest

        // Body face 1px from the lip (face = x + Radius), at standing rest on the lower floor.
        //
        // Getting a genuinely SLOW entry into this precondition check is not just "hold Right
        // from rest": InputIntent.HeldHorizontal only latches after 3 consecutive same-direction
        // frames (the tap/hold debounce), and WalkAccel (3000 px/s^2) against MaxWalkSpeed (100)
        // carries ground velocity past MantleMaxEntrySpeed (60) within that same 2-3 frame
        // window regardless of Dt or starting distance — so "Right held from a dead stop"
        // reliably lands the Held-latch frame already over the mantle's gate and ParkourState
        // (which requires an at-speed entry) claims it instead, every time. A brief opposite
        // tap first (Left, released before it latches) leaves the body decelerating through
        // zero exactly as Right's own 3-frame Held-latch completes, so the precondition check
        // sees a genuinely near-zero entry speed — the actual "slow/flush" case MantleState
        // exists to catch. Game-rate Dt (1/60, matching WalkIntoStep_At60fps below) — the
        // reversal's zero-crossing window is narrow enough that 1/30 skips over it.
        var script = new InputScript().For(3, new PlayerInput { Left = true })
                                       .Then(new PlayerInput { Right = true }).Forever();
        var frames = Run(StepTerrain(), new Vector2(lipX - PlayerCharacter.Radius - 1f, floorTopY - restOffset), 90, dt: 1f / 60f, script: script);

        Assert.True(frames.Any(f => f.State.Contains("Mantle")), "expected MantleState to engage");
        Assert.True(frames.Any(f => f.Y < onTopY && f.X > lipX),
            "expected the body to be delivered on top of the step");
    }

    [Fact]
    public void RunningApproach_StillVaultsViaParkour_NeverMantles()
    {
        float lipX = 8 * Chunk.TileSize;
        float stepTopY = 2 * Chunk.TileSize;
        float floorTopY = 3 * Chunk.TileSize;
        float restOffset = PlayerCharacter.Radius + PlayerCharacter.Radius * MathF.Sin(MathF.PI / 3f);
        float onTopY = (stepTopY + floorTopY) / 2f - restOffset;

        // Same start as the canonical vault test: far from the step, running right, starting
        // just above the floor so it drops the last bit and settles under gravity.
        var frames = Run(StepTerrain(), new Vector2(12f, floorTopY - PlayerCharacter.Radius), 120);

        Assert.True(frames.Any(f => f.State.Contains("Parkour")), "expected the reflex vault");
        Assert.DoesNotContain(frames, f => f.State.Contains("Mantle"));
        Assert.True(frames.Any(f => f.Y < onTopY && f.X > lipX + 2f), "expected the vault to complete");
    }

    // Game-rate (1/60) walk-up from one tile short: the fallback chain end-to-end. The reflex
    // ramps engage first (ParkourState), but at R=12 the 1-block step is steep relative to the
    // body and the steep-angle taper abstains as the approach closes — the body stalls flush,
    // and MantleState claims and completes the climb. Reflex first, maneuver catches the
    // reflex's abstention: the boundary between them is allowed to move with tuning, but the
    // OUTCOME (body delivered on top, one of the two states did it) is the contract.
    [Fact]
    public void WalkIntoStep_At60fps_ReflexOrMantleDeliversOnTop()
    {
        var frames = Run(StepTerrain(), new Vector2(100f, 27f), 120, dt: 1f / 60f);

        Assert.True(frames.Any(f => f.State.Contains("Parkour") || f.State.Contains("Mantle")),
            "expected the reflex vault and/or the mantle to engage");
        Assert.True(frames.Any(f => f.Y < 14f && f.X > 128f),
            "expected the body to be delivered on top of the step");
    }

    [Fact]
    public void FlushAgainstThreeBlockWall_NoMantle_StaysPut()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            OOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        float wallFaceX = 8 * Chunk.TileSize;
        float floorTopY = 3 * Chunk.TileSize;
        float restOffset = PlayerCharacter.Radius + PlayerCharacter.Radius * MathF.Sin(MathF.PI / 3f);
        float restY = floorTopY - restOffset;

        var frames = Run(terrain, new Vector2(wallFaceX - PlayerCharacter.Radius - 1f, restY), 60);

        Assert.DoesNotContain(frames, f => f.State.Contains("Mantle"));
        // Honest bonk: the body stays at floor level against the wall (rest ≈ restY), well
        // below any real climb attempt (the wall is 3 tiles tall, far past MantleMaxRise).
        Assert.True(frames.All(f => f.Y > restY - 5f), "body should not climb a 3-block wall");
        Assert.True(frames.All(f => f.X < wallFaceX), "body should not pass through the wall");
    }
}
