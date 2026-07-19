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

    private SimFrame[] Run(ChunkMap terrain, Vector2 start, int frames, float dt = Dt)
    {
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = start,
            Script        = InputScript.Always(new PlayerInput { Right = true }),
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

    [Fact]
    public void FlushAgainstStep_HoldToward_MantlesOntoTop()
    {
        // Body face 1px from the lip (face = x + Radius ⇒ x = 128 − 12 − 1), at standing rest.
        var frames = Run(StepTerrain(), new Vector2(115f, 26f), 60);

        Assert.True(frames.Any(f => f.State.Contains("Mantle")), "expected MantleState to engage");
        Assert.True(frames.Any(f => f.Y < 14f && f.X > 128f),
            "expected the body to be delivered on top of the step");
        // Ballistic envelope: the climb must not meaningfully overshoot the landing (rest ≈ 9;
        // the mantle's 2R gate target is 8, so a few px above rest is the expected apex).
        float apexY = frames.Min(f => f.Y);
        Assert.True(apexY >= 2f, $"mantle overshot the landing: apex y={apexY:F2} (rest ≈9)");
    }

    [Fact]
    public void RunningApproach_StillVaultsViaParkour_NeverMantles()
    {
        // Same start as the canonical vault test: far from the step, running right.
        var frames = Run(StepTerrain(), new Vector2(12f, 36f), 120);

        Assert.True(frames.Any(f => f.State.Contains("Parkour")), "expected the reflex vault");
        Assert.DoesNotContain(frames, f => f.State.Contains("Mantle"));
        Assert.True(frames.Any(f => f.Y < 17f && f.X > 130f), "expected the vault to complete");
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
        var frames = Run(terrain, new Vector2(115f, 26f), 60);

        Assert.DoesNotContain(frames, f => f.State.Contains("Mantle"));
        // Honest bonk: the body stays at floor level against the wall (rest ≈ 25.6).
        Assert.True(frames.All(f => f.Y > 20f), "body should not climb a 3-block wall");
        Assert.True(frames.All(f => f.X < 128f), "body should not pass through the wall");
    }
}
