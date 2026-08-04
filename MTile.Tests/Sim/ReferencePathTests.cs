using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// movement_todo #7 + #8 (M4): LedgePull and Dropdown migrated onto the reference-
// clip system — a baked/authored Hermite clip retargeted at Enter (the clip's Entry
// anchor → entry pose, its Gate anchor → measured gate) and tracked by ReferencePath's PD
// servo. Arbitration, triggers, and completion tests are unchanged; these tests
// pin that the clip-driven moves still deliver, plus the bespoke fallback
// behind UseReferenceClips=false.
// The shipped pull arc as originally authored — normalized units, default anchors.
// Used to pin that clip-space rescaling is invisible to the retarget.
internal static class ReferenceClipRegistryTestHelper
{
    public static HermiteClipDocument NormalizedPull()
    {
        var doc = new HermiteClipDocument
        {
            Name = "test_pull",
            Keys =
            {
                new HermiteClipKey { X = 0.00f, Y =  0.00f, TX = 0.05f, TY = -1.60f },
                new HermiteClipKey { X = 0.06f, Y = -0.80f, TX = 0.15f, TY = -1.00f },
                new HermiteClipKey { X = 0.22f, Y = -1.08f, TX = 1.60f, TY =  0.00f },
                new HermiteClipKey { X = 1.00f, Y = -1.00f, TX = 1.60f, TY =  0.10f },
            },
        };
        doc.RederiveT();
        return doc;
    }
}

public class ReferencePathTests(ITestOutputHelper output)
{
    private const float Dt      = 1f / 30f;
    private const float Gravity = 600f;
    private const float TS      = Chunk.TileSize; // 16

    // ── Frame mapping: endpoints bind exactly, mirroring falls out of the sign ──

    [Fact]
    public void ReferenceFrame_BindsEntryAndGate()
    {
        var entry = new Vector2(132f, 44f);
        var gate  = new Vector2(158f, 4f);
        var frame = new ReferenceFrame(entry, gate);

        Assert.Equal(entry, frame.Map(Vector2.Zero));
        Assert.Equal(gate, frame.Map(new Vector2(1f, -1f)));

        // Left-facing: gate left of entry mirrors x; a descent flips y.
        var down = new ReferenceFrame(entry, new Vector2(110f, 80f));
        Assert.Equal(new Vector2(110f, 80f), down.Map(new Vector2(1f, -1f)));
        // Mid-curve point above the entry maps below it when the gate is below.
        Assert.True(down.Map(new Vector2(0.5f, -0.5f)).Y > entry.Y);
    }

    // Clips are authored in pixels against their own Entry/Gate anchors. Rescaling
    // clip space is therefore a no-op on the retargeted arc — that invariant is what
    // lets the editor pick any authoring box, and what makes the anchors' defaults
    // bit-compatible with the pre-anchor normalized files.
    [Fact]
    public void AnchoredFrame_RescalingClipSpace_LeavesTheWorldArcUnchanged()
    {
        var entry = new Vector2(132f, 44f);
        var gate  = new Vector2(158f, 4f);

        var normalized = ReferenceClipRegistryTestHelper.NormalizedPull();
        var pixels     = ReferenceClipRegistryTestHelper.NormalizedPull();
        pixels.RescaleClipSpace(new Vector2(26f, -40f));   // sign flip included

        var fn = new ReferenceFrame(normalized, entry, gate);
        var fp = new ReferenceFrame(pixels, entry, gate);

        Assert.Equal(entry, fp.Map(pixels.Entry));
        Assert.Equal(gate,  fp.Map(pixels.Gate));
        for (float t = 0f; t <= 1.0001f; t += 0.1f)
        {
            var a = fn.Map(normalized.Eval(t));
            var b = fp.Map(pixels.Eval(t));
            Assert.True(Vector2.Distance(a, b) < 1e-3f, $"t={t:0.0}: {a} vs {b}");
        }
    }

    // A clip whose anchors share an axis can't be normalized on it; the frame falls
    // back to 1:1 there instead of dividing by zero.
    [Fact]
    public void AnchoredFrame_DegenerateAxis_FallsBackToPixels()
    {
        var clip = ReferenceClipRegistryTestHelper.NormalizedPull();
        clip.Gate = new Vector2(0f, -40f);   // pure-vertical box
        var frame = new ReferenceFrame(clip, new Vector2(100f, 50f), new Vector2(100f, 10f));

        Assert.Equal(1f, frame.Scale.X);                                   // px passthrough
        Assert.Equal(new Vector2(105f, 50f), frame.Map(new Vector2(5f, 0f)));
    }

    // ── Ledge pull: same fixture family as LedgePullExitTests ──────────────────
    // 3-tall wall on the right, lip at the standing body's head line. Grab via a
    // tap of Up from Standing, hang, then hold Up → LedgePullState → standing on
    // top past the corner.

    private static ChunkMap LedgeTerrain() => SimTerrain.FromAscii(@"
            ..........
            ..........
            .........X
            .........X
            .........X
            XXXXXXXXXX", originTileX: 0, originTileY: 0);

    private const float GroundTop  = 5 * TS;   // 80
    private const float WallLeft   = 9 * TS;   // 144
    private const float CornerTopY = 2 * TS;   // 32
    private const float HalfW      = 10.392f;  // R·sin60° at R=12

    private SimFrame[] RunPull()
    {
        var cfg = new SimConfig
        {
            Terrain       = LedgeTerrain(),
            StartPosition = new Vector2(WallLeft - HalfW, GroundTop - 2f * PlayerCharacter.Radius),
            Script        = new InputScript()
                                .For(30, new PlayerInput { })              // settle to Standing
                                .For(3,  new PlayerInput { Up = true })    // tap Up → LedgeGrab
                                .For(10, new PlayerInput { })              // hang
                                .Forever(new PlayerInput { Up = true }),   // pull to the top
            Frames        = 150,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var trace = SimRunner.Run(cfg);
        SimReport.Print(trace, output, fullTable: false);
        return trace;
    }

    [Fact]
    public void LedgePull_ClipDriven_DeliversStandingOnTop()
    {
        var trace = RunPull();

        int pullStart = Array.FindIndex(trace, f => f.State == "LedgePullState");
        int pullEnd   = Array.FindLastIndex(trace, f => f.State == "LedgePullState");
        Assert.True(pullStart > 0, "pull never started");

        // Completed rather than timed out: under MaxLipManeuverTime (0.5s = 15 frames
        // at dt=1/30). The clip pace is tuned to the bespoke pull's measured 14
        // frames, so completion rides just inside the cap — exactly as before.
        Assert.True(pullEnd - pullStart < 15,
            $"pull ran {pullEnd - pullStart} frames — timed out instead of completing");

        // Delivered: past the corner at standing height, and it sticks (grounded
        // on the lip by the end of the run, not fallen back off).
        var after = trace.Skip(pullEnd + 1).ToArray();
        Assert.Contains(after, f => f.State == "StandingState" && f.X > WallLeft && f.Y < CornerTopY - 18f);

        // The clip's crest is authored 8% above the gate (~3px); with servo
        // overshoot the apex must still stay within ~12px of the standing line.
        float minY = trace.Min(f => f.Y);
        Assert.True(minY > CornerTopY - 2f * PlayerCharacter.Radius - 12f,
            $"apex y={minY:F1} — ballooned above the authored crest");
    }

    [Fact]
    public void LedgePull_BespokeFallback_StillDelivers()
    {
        MovementConfig.Current.UseReferenceClips = false;
        try
        {
            var trace = RunPull();
            int pullStart = Array.FindIndex(trace, f => f.State == "LedgePullState");
            Assert.True(pullStart > 0, "bespoke pull never started");
            Assert.Contains(trace.Skip(pullStart), f => f.State == "StandingState" && f.X > WallLeft && f.Y < CornerTopY - 18f);
        }
        finally
        {
            MovementConfig.Current.UseReferenceClips = true;
        }
    }

    // ── Dropdown: same terrain family as DropdownTests ─────────────────────────
    // Platform at row 5 cols 0..9 (top y=80, drop edge x=160), floor at row 8
    // (top y=128). Hold Down while hanging over → clip-shaped slide-out → Falling
    // → lands below, close to the platform edge (the slip-off must not fling).

    private const string DropTerrain = @"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX";

    private SimFrame[] RunDrop()
    {
        var cfg = new SimConfig
        {
            Terrain       = SimTerrain.FromAscii(DropTerrain, originTileX: 0, originTileY: 0),
            StartPosition = new Vector2(157f, 60.5f),
            StartVelocity = Vector2.Zero,
            Script        = InputScript.Always(new PlayerInput { Down = true }),
            Frames        = 45,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };
        var trace = SimRunner.Run(cfg);
        SimReport.Print(trace, output, fullTable: false);
        return trace;
    }

    [Fact]
    public void Dropdown_ClipDriven_SlipsOffAndLandsNearTheWall()
    {
        var trace = RunDrop();

        Assert.Contains(trace, f => f.State.Contains("Dropdown"));
        int airborne = Array.FindIndex(trace, f => f.State.Contains("Falling"));
        Assert.True(airborne > 0, "dropdown never went airborne");

        // Landed on the lower floor (top y=128, rest ≈ 105.6), close to the wall
        // under the platform edge (x=160) — DropdownExitVelMult still softens the
        // slip-off, clip or no clip.
        var last = trace[^1];
        Assert.True(last.Y > 100f, $"never descended to the lower floor: y={last.Y:F1}");
        Assert.True(last.X > 160f && last.X < 210f,
            $"landed at x={last.X:F1} — expected just past the edge (160..210)");
    }

    [Fact]
    public void Dropdown_BespokeFallback_StillDrops()
    {
        MovementConfig.Current.UseReferenceClips = false;
        try
        {
            var trace = RunDrop();
            Assert.Contains(trace, f => f.State.Contains("Dropdown"));
            Assert.True(trace[^1].Y > 100f, $"bespoke drop never reached the floor: y={trace[^1].Y:F1}");
        }
        finally
        {
            MovementConfig.Current.UseReferenceClips = true;
        }
    }
}
