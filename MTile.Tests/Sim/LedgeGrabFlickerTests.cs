using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Regression for the user-reported bug: pressing Up while next to a low ledge
// triggered a rapid ParkourState ↔ StandingState flicker.
//
// Root cause: a 1-tile-tall wall at the body's chest level reads as an
// ExposedLowerCorner (overcrop / duck) and trips ParkourState. Each frame the
// ramp redirects the body a fraction of a pixel into the wall; the next frame
// ExposedLowerCornerChecker's OutsideBodyFace filter (which compared the tile's
// near edge to the body's RIGHT vertex) rejects the same corner because of that
// sub-pixel overlap. ParkourState exits → StandingState pulls the body back from
// the wall by sub-pixel → corner detected again → ParkourState re-engages. Two-
// frame cycle, indefinitely.
//
// The fix in ExposedLowerCornerChecker is to anchor the OutsideBodyFace check on
// body.Position.X (center) rather than the facing vertex, so a fractional clip
// can't toggle detection off.
public class LedgeGrabFlickerTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;
    private const float TS = Chunk.TileSize; // 16

    // Layout for the bug repro: 1-tile-tall wall at col 9 row 1 above row-3 ground.
    // When the body stands on row-3 ground its head-Y (~19.5) sits inside row 1's
    // y range — the wall's TOP is a ledge corner; its BOTTOM is an overcrop corner
    // that ExposedLowerCornerChecker reports for ParkourState's duck precondition.
    private static ChunkMap BuildShortLedgeTerrain() => SimTerrain.FromAscii(@"
            ..........
            .........X
            ..........
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    // 2-tile-tall wall whose TOP sits at the grounded body's head — the canonical
    // "graspable ledge" geometry.
    private static ChunkMap BuildTallLedgeTerrain() => SimTerrain.FromAscii(@"
            ..........
            ..........
            ..........
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    // ── Flicker repros ──────────────────────────────────────────────────────

    // Primary repro. Holding Right while a 1-tile chest-height wall is to the
    // right used to flicker Parkour ↔ Standing every 2 frames forever. The fix
    // collapses that to a single Standing transition (the body stops at the wall).
    [Fact]
    public void RunRightNoUp_NoStateFlicker()
    {
        var terrain = BuildShortLedgeTerrain();
        const float groundTop = 3 * TS;
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(40f, groundTop - 2f * PlayerCharacter.Radius),
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        AssertStableAfterSettle(frames, settleFrame: 30, label: "Right-only");
    }

    // Same wall, but with Right + Up held the whole run. Before the fix this was
    // the textbook user gesture that caused the flicker.
    [Fact]
    public void RunRightAndHoldUp_NoStateFlicker()
    {
        var terrain = BuildShortLedgeTerrain();
        const float groundTop = 3 * TS;
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(40f, groundTop - 2f * PlayerCharacter.Radius),
            Script        = InputScript.Always(new PlayerInput { Right = true, Up = true }),
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        AssertStableAfterSettle(frames, settleFrame: 30, label: "Right+Up");
    }

    // Hold Right against the wall, then tap Up after a brief settle. Same low-ledge
    // geometry as above. Expectation: body settles into Standing or LedgeGrab and
    // stays there — no two-frame Parkour/Standing oscillation.
    [Fact]
    public void StandAtShortLedge_HoldRightAndTapUp_NoStateFlicker()
    {
        var terrain = BuildShortLedgeTerrain();
        const float groundTop = 3 * TS;
        const float wallLeft  = 9 * TS;
        float halfW = 8.227f;
        var script = new InputScript()
            .For(15, new PlayerInput { Right = true })
            .Forever(new PlayerInput { Right = true, Up = true });
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(wallLeft - halfW, groundTop - 2f * PlayerCharacter.Radius),
            Script        = script,
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        AssertStableAfterSettle(frames, settleFrame: 30, label: "stand-tap-Up short");
    }

    // ── Positive controls ──────────────────────────────────────────────────

    // Taller wall where the top tile is at the body's head Y: this is the
    // genuine ledge-grab case. Hold Up after a brief settle and expect a clean
    // single transition to LedgeGrabState.
    [Fact]
    public void StandAtLedge_TapUp_GrabsCleanly()
    {
        var terrain = BuildTallLedgeTerrain();
        const float groundTop = 5 * TS;
        const float wallLeft  = 9 * TS;
        float halfW = 8.227f;
        var script = new InputScript()
            .For(30, new PlayerInput { })
            .Forever(new PlayerInput { Up = true });
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(wallLeft - halfW, groundTop - 2f * PlayerCharacter.Radius),
            Script        = script,
            Frames        = 120,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        SimReport.Print(frames, output, fullTable: false);

        // Body should land in LedgeGrabState within a few frames of the Up tap
        // and stay there for the rest of the run.
        var last = frames[^1];
        Assert.Equal("LedgeGrabState", last.State);
        AssertStableAfterSettle(frames, settleFrame: 40, label: "tall ledge grab");
    }

    // Tall pillar (5 tiles) for the airborne-fall case. Top tile at row 5 (top Y=80),
    // pillar extends down through row 9. Ground at row 10. Body falls past the lip
    // from above-and-to-the-left while holding Up.
    private static ChunkMap BuildTallPillarTerrain() => SimTerrain.FromAscii(@"
            ..........
            ..........
            ..........
            ..........
            ..........
            .........X
            .........X
            .........X
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    // The user's scenario: player FALLS near a tall pillar's top corner with Up held.
    // Captures whether LedgeGrab / ParkourState / Falling alternate as the body's
    // altitude sweeps past the ledge-grab and vault-band windows in successive frames.
    [Fact]
    public void FallPastTallPillarTopCorner_HoldUp_NoStateFlicker()
    {
        var terrain = BuildTallPillarTerrain();
        const float pillarTop = 5 * TS;     // 80
        const float pillarLeft = 9 * TS;    // 144
        float halfW = 8.227f;

        // Body just left of the pillar's left face, two tiles above the lip,
        // falling straight down with Up held the whole run — the natural "drop
        // alongside the wall, try to catch the lip" gesture.
        var cfg = new SimConfig
        {
            Terrain        = terrain,
            StartPosition  = new Vector2(pillarLeft - halfW, pillarTop - 32f),
            StartVelocity  = new Vector2(0f, 0f),
            Script         = InputScript.Always(new PlayerInput { Up = true }),
            Frames         = 90,
            Dt             = Dt,
            Gravity        = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        AssertStableAfterSettle(frames, settleFrame: 0, label: "drop next to pillar Up held");
    }

    // Body falls toward the pillar from above-and-side with Right+Up pressed —
    // simulating "approach the pillar in the air, try to grab as you fall past".
    [Fact]
    public void FallTowardTallPillarTopCorner_RightAndUp_NoStateFlicker()
    {
        var terrain = BuildTallPillarTerrain();
        const float pillarTop = 5 * TS;
        const float pillarLeft = 9 * TS;
        float halfW = 8.227f;

        var cfg = new SimConfig
        {
            Terrain        = terrain,
            StartPosition  = new Vector2(pillarLeft - halfW - 8f, pillarTop - 24f),
            StartVelocity  = new Vector2(50f, 30f),
            Script         = InputScript.Always(new PlayerInput { Right = true, Up = true }),
            Frames         = 90,
            Dt             = Dt,
            Gravity        = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        AssertStableAfterSettle(frames, settleFrame: 0, label: "fall+right toward pillar");
    }

    // Body falls FAST past the lip with Up held. At terminal-vy each frame body
    // moves ~16 px (one tile), so the head-Y can skip past the pillar's top tile's
    // y-range in a single frame. Exercises the "fast traversal, brief grab window"
    // path.
    [Fact]
    public void FallFastPastTallPillarTopCorner_HoldUp_NoStateFlicker()
    {
        var terrain = BuildTallPillarTerrain();
        const float pillarTop = 5 * TS;
        const float pillarLeft = 9 * TS;
        float halfW = 8.227f;

        var cfg = new SimConfig
        {
            Terrain        = terrain,
            StartPosition  = new Vector2(pillarLeft - halfW, pillarTop - 16f),
            StartVelocity  = new Vector2(0f, 400f),
            Script         = InputScript.Always(new PlayerInput { Up = true }),
            Frames         = 90,
            Dt             = Dt,
            Gravity        = new Vector2(0f, Gravity),
        };
        var frames = SimRunner.Run(cfg);
        AssertStableAfterSettle(frames, settleFrame: 0, label: "fast fall past pillar Up held");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // Counts state transitions strictly after `settleFrame`. The flicker bug
    // manifested as one transition every 2 frames forever; ≤ 3 transitions over
    // the remaining ~90 frames is a wide-but-decisive ceiling that excludes the
    // flicker without making the test brittle to single-shot transitions.
    private void AssertStableAfterSettle(SimFrame[] frames, int settleFrame, string label)
    {
        SimReport.Print(frames, output, fullTable: false);
        int count = 0;
        var seq = new System.Collections.Generic.List<string>();
        foreach (var f in frames)
        {
            if (!f.Transition || f.Frame < settleFrame) continue;
            count++;
            seq.Add($"f{f.Frame}:{f.State}");
        }
        output.WriteLine($"{label}: {count} transitions after frame {settleFrame} — {string.Join(" → ", seq)}");
        Assert.True(count <= 3,
            $"State flicker after settle (label={label}): {count} transitions after frame {settleFrame}: {string.Join(" → ", seq)}");
    }
}
