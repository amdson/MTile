using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Operation-level flows through the live sim (SimRunner): whole traversals, not
// single maneuvers — vault chains, ducking under protrusions at speed, tunnel
// runs, drops into tunnel mouths, staircases. These pin the OUTCOME of common
// player verbs; the states that deliver them are allowed to change.
//
// Two tiers per the corrector roadmap: passage tests (the body gets through —
// required green) and cleanliness tests (no stall / no hard bonk — the ambient
// preemptive-duck spec, red until the StandServo/ambient mode lands, plan §7).
public class CorrectorOperationsTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;

    private SimFrame[] Run(ChunkMap terrain, Vector2 start, int frames, string label)
    {
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = start,
            Script        = InputScript.Always(new PlayerInput { Right = true }),
            Frames        = frames,
            Dt            = Dt,
            Gravity       = new Vector2(0f, 600f),
        };
        var result = SimRunner.Run(cfg);
        output.WriteLine($"--- {label} ---");
        foreach (var f in result)
            if (f.Frame % 10 == 0 || f.Transition)
                output.WriteLine($"{f.Frame,3} {f.State,-24} x={f.X,7:F2} y={f.Y,6:F2} vx={f.Vx,7:F2} vy={f.Vy,7:F2}");
        return result;
    }

    private static int ActivationCount(SimFrame[] frames, string stateSubstring)
    {
        int n = 0; bool inside = false;
        foreach (var f in frames)
        {
            bool now = f.State.Contains(stateSubstring);
            if (now && !inside) n++;
            inside = now;
        }
        return n;
    }

    // ── Vault chain: two spaced 1-block steps taken in stride ──
    [Fact]
    public void VaultChain_TwoSteps_BothTakenInStride()
    {
        // Floor row 6 (top 96); step A cols 8-11 (x 128..192), step B cols 16-19
        // (x 256..320), both 1 block tall (top 80).
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXOOOOXXXXOOOO
            XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 5);
        var frames = Run(terrain, new Vector2(12f, 72f), 220, "vault chain");

        Assert.True(ActivationCount(frames, "Parkour") >= 2,
            $"expected two vault activations, got {ActivationCount(frames, "Parkour")}");
        Assert.True(frames.Any(f => f.X > 150f && f.X < 200f && f.Y < 60f), "expected delivery on step A");
        Assert.True(frames.Any(f => f.X > 280f && f.Y < 60f), "expected delivery on step B");
        Assert.True(frames[^1].X > 330f, $"expected the run to continue past step B, got x={frames[^1].X:F1}");
    }

    // ── Staircase: three consecutive 1-block rises at speed ──
    [Fact]
    public void Staircase_ThreeRises_ClimbedWithoutStall()
    {
        // Floor row 8 (top 128); steps at cols 10-11 (top 112), 12-13 (top 96),
        // 14-15 (top 80); top level continues cols 14..21.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOXXXXXXXX
            OOOOOOOOOOOOXXXXXXXXXX
            OOOOOOOOOOXXXXXXXXXXXX
            XXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 5);
        var frames = Run(terrain, new Vector2(12f, 104f), 260, "staircase");

        Assert.True(frames.Any(f => f.Y < 60f && f.X > 230f),
            "expected the body delivered onto the top level");
        // No terminal stall: the body keeps making forward progress to the end.
        Assert.True(frames[^1].X > 300f, $"expected continued run on top, got x={frames[^1].X:F1}");
    }

    // ── Duck: a 1-tile protrusion over a 2-high gap, hit at running speed ──
    // Passage tier: the body must get under and through, standing again after.
    [Fact]
    public void DuckUnderProtrusion_AtSpeed_PassesThrough()
    {
        // Floor row 4 (top 64); protrusion cols 10-11 (x 160..192) at row 1
        // (y 16..32): gap 32px — crouch-passable, standing head (y-top 28) clips.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOXXOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 1);
        var frames = Run(terrain, new Vector2(12f, 40f), 240, "duck under protrusion");

        Assert.True(frames.Any(f => f.X > 200f), $"expected passage under the protrusion, max x={frames.Max(f => f.X):F1}");
        // Under the protrusion the body must actually be ducked (center below the
        // standing line, top clear of the lip's band).
        foreach (var f in frames.Where(f => f.X > 165f && f.X < 187f))
            Assert.True(f.Y > 42f, $"body not ducked under the lip: y={f.Y:F2} at x={f.X:F1}");
        Assert.True(frames[^1].X > 300f, $"expected the run to continue, got x={frames[^1].X:F1}");
    }

    // ── Tunnel: enter a 2-high corridor at speed, traverse, stand after exit ──
    [Fact]
    public void TunnelRun_TwoHigh_EnterTraverseExit()
    {
        // Floor row 6 (top 96); tunnel ceiling row 3 (bottom 64) over cols 8..17
        // (x 128..288): gap 32. Open sky before and after.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOXXXXXXXXXXOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 3);
        var frames = Run(terrain, new Vector2(12f, 72f), 300, "tunnel run");

        Assert.True(frames.Any(f => f.X > 300f), $"expected full traversal, max x={frames.Max(f => f.X):F1}");
        // Inside the tunnel the body is crouched (center below the standing line).
        foreach (var f in frames.Where(f => f.X > 140f && f.X < 275f))
            Assert.True(f.Y > 74f, $"body not ducked inside the tunnel: y={f.Y:F2} at x={f.X:F1}");
        // After the exit it stands back up and keeps running.
        Assert.True(frames[^1].X > 330f, $"expected continued run after exit, got x={frames[^1].X:F1}");
        Assert.Contains(frames.Where(f => f.X > 300f), f => f.State.Contains("Standing"));
    }

    // ── Drop into a tunnel mouth: run off a 1-block drop straight into a low corridor ──
    [Fact]
    public void DropIntoTunnelMouth_LandsAndTraverses()
    {
        // Upper floor row 4 (top 64) cols 0..7 → 1-block drop to the lower floor
        // (row 5, top 80). The tunnel ceiling (rows 1-2, bottom y=48 ⇒ gap 32 over
        // the lower floor; roof top y=16 = a 48px rise, beyond the climb envelope,
        // so the drop route is the only route) starts 4 columns PAST the drop
        // edge: at full speed the falling head clears the mouth lip by ~10px with
        // no tuck. That recess is calibrated to today's machinery — tightening the
        // mouth toward the edge is the ambient-tuck spec (see the Skip test below).
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOXXXXXXXXOOOOOO
            OOOOOOOOOOOOXXXXXXXXOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 1);
        var frames = Run(terrain, new Vector2(12f, 40f), 300, "drop into tunnel mouth");

        // Ceiling spans x 192..320; lower floor from x=128.
        Assert.True(frames.Any(f => f.X > 330f), $"expected traversal of the tunnel, max x={frames.Max(f => f.X):F1}");
        // Down + ducked inside the corridor (not wedged at the mouth).
        Assert.True(frames.Any(f => f.X > 200f && f.X < 310f && f.Y > 60f),
            "expected the body down on the lower floor inside the tunnel");
        Assert.True(frames[^1].X > 350f, $"expected continued run after exit, got x={frames[^1].X:F1}");
    }

    // Ambient-tuck spec (plan §7 — StandServo/preemptive duck): with the mouth
    // only 2 columns past the drop edge, threading it at speed needs an
    // anticipatory tuck/fast-fall the current machinery doesn't have — the body
    // bonks the mouth lip and wedges on the lower floor in front of it (the
    // passable-lip case the anti-autopilot rules explicitly allow assisting).
    // Un-skip when ambient mode lands.
    [Fact(Skip = "needs the StandServo root (plan §7 baseline-posture): threading a 2-col mouth at speed requires shedding ~130px/s of drop budget in ~0.16s, and grounded frames contribute nothing (the spring zeroes downward vy) — redirect-only ambient is structurally insufficient")]
    public void DropIntoTunnelMouth_TightMouth_RequiresAnticipatoryTuck()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOXXXXXXXXOOOOOOOO
            OOOOOOOOOOXXXXXXXXOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 1);
        var frames = Run(terrain, new Vector2(12f, 40f), 300, "drop into tight tunnel mouth");

        Assert.True(frames.Any(f => f.X > 300f), $"expected traversal, max x={frames.Max(f => f.X):F1}");
        Assert.True(frames[^1].X > 330f, $"expected continued run after exit, got x={frames[^1].X:F1}");
    }
}
