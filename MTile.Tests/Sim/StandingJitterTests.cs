using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Phase C / Bullet 7 of Plans/TODO_TOP_BULLETS_PLAN.md.
//
// Repro for the recurring "standing state jitters when pushed up by blocks"
// bug. A sprout grows up beneath a standing player; while the sprout is
// Growing it presents a moving polygon (TileSproutNode.Polygon lerped from
// the parent cell toward the target cell). On TickSprouts → IsComplete, the
// sprout is removed from Graph.Growing and the target cell flips to Solid in
// the same WriteTile call.
//
// Phase ordering inside Simulation.Step:
//   3. dynamic surfaces ticked (platforms)
//   4. ChunkMap.TickSprouts(dt)   ← sprout may finalize HERE
//   5. combat frame, _player.Update (creates / refreshes FSDs)
//   7. PhysicsWorld.StepSwept     ← surfaces are queried HERE
//
// So when a sprout finalizes on the same frame, _player.Update sees the new
// Solid tile (via GroundChecker.TryFind) before StepSwept does the resolution.
// The standing-spring (FloatingSurfaceDistance) should refresh smoothly, but
// the user reports per-frame y discontinuities — these tests log y trajectory
// and assert the absence of large frame-to-frame deltas after the body has
// nominally settled.
public class StandingJitterTests
{
    private readonly ITestOutputHelper _out;
    public StandingJitterTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int FloorRow = 20;
    private const float FloorTopY = FloorRow * 16f;

    private static ChunkMap WidePlatform()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[20];
            for (int i = 0; i < 20; i++) line[i] = (r >= 20) ? 'X' : 'O';
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    private record FrameSample(int Frame, float PosY, float VelY, string State, int GrowingSprouts);

    private List<FrameSample> RunWithLog(
        ChunkMap terrain, PlayerCharacter player, int frames,
        Action<int, ChunkMap> beforeFrame = null)
    {
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var samples = new List<FrameSample>(frames);

        for (int f = 0; f < frames; f++)
        {
            beforeFrame?.Invoke(f, terrain);
            ctrl.InjectInput(new PlayerInput());
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            samples.Add(new FrameSample(
                f, player.Body.Position.Y, player.Body.Velocity.Y,
                player.CurrentStateName, terrain.Graph.Growing.Count));
        }
        return samples;
    }

    private void LogSamples(string label, List<FrameSample> samples)
    {
        _out.WriteLine($"--- {label} ---");
        _out.WriteLine($"  f    y         vy       Δy       state            sprouts");
        float prevY = samples[0].PosY;
        foreach (var s in samples)
        {
            float dy = s.PosY - prevY;
            _out.WriteLine(
                $"  {s.Frame,3}  {s.PosY,8:F3}  {s.VelY,7:F2}  {dy,7:F3}  {s.State,-16}  {s.GrowingSprouts}");
            prevY = s.PosY;
        }
    }

    // Baseline: a standing player at rest. After the initial settle, y should
    // be flat — no jitter, no oscillation. Establishes the noise floor for
    // subsequent jitter checks.
    [Fact]
    public void StandingPlayer_NoSprouts_BodyYIsFlat()
    {
        var terrain = WidePlatform();
        const float spawnX = 10 * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 12f));

        var samples = RunWithLog(terrain, player, 40);
        LogSamples("standing, no sprouts", samples);

        // After ~5 frames the spring should have settled. Frames 10..39 should
        // be ~constant — assert max |Δy| across that window is tiny.
        float maxDy = 0f;
        for (int i = 11; i < samples.Count; i++)
        {
            float dy = MathF.Abs(samples[i].PosY - samples[i - 1].PosY);
            if (dy > maxDy) maxDy = dy;
        }
        Assert.True(maxDy < 0.05f,
            $"settled standing y should be flat; saw max |Δy| = {maxDy:F4} between frames 10..39");
    }

    // The repro from the plan: a sprout grows UP from the floor cell the
    // player is standing on, into the cell directly above. As it grows the
    // body should be lifted smoothly; when it finalizes (turning from a
    // moving polygon into a static solid tile in the SAME tile-write call)
    // y must not jump.
    [Fact]
    public void SproutGrowsUpUnderStandingPlayer_LogsBodyYTrace()
    {
        var terrain = WidePlatform();
        const int playerCol = 10;
        float spawnX = playerCol * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 12f));

        // Frame 8: player has settled standing on the floor. Request a sprout
        // at (10, 19) — the cell directly above the floor cell the player is
        // standing on. Parent priority makes (10, 20) the parent → grows up.
        const int requestFrame = 8;
        TileSproutNode requestedSprout = null;
        bool requested = false;

        var samples = RunWithLog(terrain, player, 30, (f, t) =>
        {
            if (f == requestFrame && !requested)
            {
                requestedSprout = t.TryRequestTile(playerCol, FloorRow - 1, TileType.Stone);
                requested = true;
                _out.WriteLine($"[frame {f}] requested sprout at ({playerCol},{FloorRow - 1}) → {(requestedSprout != null ? "ok" : "REJECTED")}");
                if (requestedSprout != null)
                    _out.WriteLine($"   StartCenter={requestedSprout.StartCenter}, EndCenter={requestedSprout.EndCenter}, Lifetime={requestedSprout.Lifetime}");
            }
        });

        Assert.NotNull(requestedSprout);
        LogSamples("sprout grows up under standing player", samples);

        // Sanity: at end of run, the player should have been lifted ~16 px
        // (one tile) — from standing on row 20 to standing on row 19.
        float settledBefore = samples[requestFrame - 1].PosY;
        float settledAfter  = samples[^1].PosY;
        _out.WriteLine($"settled y before sprout: {settledBefore:F3}");
        _out.WriteLine($"settled y after sprout : {settledAfter:F3}");
        _out.WriteLine($"delta                  : {settledBefore - settledAfter:F3} (expected ~16)");

        // The jitter check: across the entire sequence, after the body is
        // moving steadily upward (push phase ~3 frames at default SproutLifetime
        // = 0.1s) and then settling, no single frame's Δy should spike to a
        // value inconsistent with smooth motion. We allow up to one tile per
        // frame during the push (the sprout's lerped center moves ~5.3 px per
        // frame at SproutLifetime=0.1s / 3 frames), so the threshold is set at
        // 12px — anything larger is a snap, not motion.
        for (int i = 1; i < samples.Count; i++)
        {
            float dy = samples[i].PosY - samples[i - 1].PosY;
            Assert.True(MathF.Abs(dy) < 12f,
                $"frame {i}: y jumped by {dy:F3} (from {samples[i - 1].PosY:F3} to {samples[i].PosY:F3}) — discontinuity in standing trace");
        }
    }

    // Stacked column: request a Pending chain (rows 19, 18, 17 in one frame).
    // Row 19 grows from the solid floor; rows 18 & 17 are Pending until their
    // parents finalize. Each promotion is a fresh Sprouting → Solid → new
    // Sprouting hand-off — exactly the rapid sequence of moving-rectangle
    // disappearances the user suspected.
    //
    // The promotion path: TickSprouts finalizes row 19 (cell flips Solid) and
    // in the SAME call promotes row 18's pending node to Growing, stamping a
    // fresh polygon starting at row 19's center. Row 18 finalizes 3 frames
    // later, promoting row 17. Total ~9 frames of continuous lift.
    [Fact]
    public void StackedSproutChain_GrowsUpUnderStandingPlayer_NoYJitter()
    {
        var terrain = WidePlatform();
        const int playerCol = 10;
        float spawnX = playerCol * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 12f));

        const int requestFrame = 8;
        bool requested = false;
        var requestedNodes = new List<TileSproutNode>();

        var samples = RunWithLog(terrain, player, 45, (f, t) =>
        {
            if (f == requestFrame && !requested)
            {
                // Build the stack in TOP-DOWN order so each request finds its
                // already-Pending neighbour below — gty-1 row first won't have a
                // sibling yet, then gty-2, then gty-3. Actually the order doesn't
                // matter for the chain (Pending neighbours are seeded after
                // walking solid neighbours), but doing rows 19→17 makes the
                // sequence visible in the request log.
                for (int dy = 1; dy <= 3; dy++)
                {
                    var node = t.TryRequestTile(playerCol, FloorRow - dy, TileType.Stone);
                    requestedNodes.Add(node);
                    _out.WriteLine($"[frame {f}] request ({playerCol},{FloorRow - dy}) → " +
                                   (node == null ? "REJECTED" : $"{node.Status}"));
                }
                requested = true;
            }
        });

        Assert.Equal(3, requestedNodes.Count);
        Assert.NotNull(requestedNodes[0]);   // row 19 should be Growing from the floor
        Assert.Equal(TileSproutStatus.Growing, requestedNodes[0].Status);

        // Detect each row's finalize event by sampling cell state per frame.
        // Graph.Growing.Count alone is misleading — when a parent finalizes,
        // a Pending child promotes in the same TickSprouts call, so the count
        // is conserved through the chain.
        var promotionFrames = new List<int>();
        bool[] wasSolid = new bool[3];
        // First pass: walk the terrain through the same sequence the test ran,
        // observing cell-state transitions. We can't re-run physics, but we can
        // re-check current state — by end of run all 3 are Solid. Detect the
        // chronological order by inspecting sprout finalization in the samples.
        // Simpler: count distinct "drops in Growing.Count or no-change-but-
        // forward-progress" transitions. For a 3-row chain at SproutLifetime
        // = 0.1s and dt = 1/30, the lift takes ~9 frames (3 per row).
        // We just need *a* finalize-frame anchor for the lift window — use
        // the frame where Growing.Count first drops to 0 (chain ends).
        int chainEndFrame = -1;
        for (int i = requestFrame; i < samples.Count; i++)
        {
            if (samples[i].GrowingSprouts == 0 && samples[i - 1].GrowingSprouts > 0)
            {
                chainEndFrame = i;
                break;
            }
        }
        LogSamples("stacked sprout chain (rows 19/18/17)", samples);
        _out.WriteLine($"chain end frame: {chainEndFrame}");
        Assert.True(chainEndFrame > requestFrame, "expected the sprout chain to finalize within the run");

        // Settled y after the full chain — body should be standing on row 17.
        // Expected: floor top at row 17 = 17·16 = 272; settle gap = 2 below
        // MinDistance ⇒ y ≈ 272 - 19 + 2 = 255.
        float settledAfter = samples[^1].PosY;
        _out.WriteLine($"settled y after stacked chain: {settledAfter:F3} (expected ~255)");
        Assert.InRange(settledAfter, 252f, 258f);

        // THE JITTER ASSERTION — mid-chain phase only.
        //
        // The bug was: every time a parent sprout finalized and a child promoted,
        // both shared the same WorldTop for one frame. The just-Solid tile won
        // the iteration-order tie in GroundChecker, the FSD's SurfaceVelocity
        // was set to zero (instead of the child's growth velocity), and the
        // spring's relative-frame damping slammed the body's vy to zero in
        // a single step. Then the next frame, the child outgrew the tie, the
        // FSD picked up the right velocity, and the body lurched back into
        // motion. Stair-step lift.
        //
        // Fix #1 (TickSprouts overshoot age) carries the parent's overshoot
        // (Age - Lifetime) into the child's starting Age so growth is
        // time-continuous across the handoff.
        // Fix #3 (GroundChecker end-of-step prediction) picks the surface
        // that will be highest after dt of motion, so a moving sprout wins
        // over a flush static tile without any tie-break.
        //
        // With both fixes in place, mid-chain |Δvy| stays bounded by the
        // spring's natural response (~10-15 px/s). Exclude:
        //   - frames 0..requestFrame: pre-sprout settle
        //   - frame requestFrame+1: initial catch (body legitimately
        //     accelerates from vy=0 to surface vy in one frame)
        //   - frame chainEndFrame: chain end (body legitimately stops)
        //   - frames after chainEndFrame: post-chain settle
        // Mid-chain frames must show smooth velocity within a tight band.
        float worstDvy = 0f;
        int worstFrame = -1;
        for (int i = requestFrame + 2; i < chainEndFrame; i++)
        {
            float dvy = MathF.Abs(samples[i].VelY - samples[i - 1].VelY);
            if (dvy > worstDvy) { worstDvy = dvy; worstFrame = i; }
        }
        _out.WriteLine($"worst mid-chain |Δvy|: {worstDvy:F2} px/s at frame {worstFrame}");
        Assert.True(worstDvy < 50f,
            $"mid-chain velocity slammed: |Δvy|={worstDvy:F2} px/s at frame {worstFrame} — " +
            "the body should track the sprout's growth velocity smoothly between the initial catch and the chain end. " +
            "Spikes here mean the FSD's SurfaceVelocity is jumping discretely (the original bug) or fix #1/#3 has regressed.");
    }

    // Same scenario, but observe what happens specifically at the finalize
    // frame: the frame on which Graph.Growing.Count drops from 1 → 0. The
    // claimed bug ("moving rectangles disappear momentarily") would surface
    // here — y dropping for one frame as the sprout polygon vanishes before
    // the solid tile is sampled, then snapping back.
    [Fact]
    public void SproutGrowsUpUnderStandingPlayer_NoDropAtFinalizeFrame()
    {
        var terrain = WidePlatform();
        const int playerCol = 10;
        float spawnX = playerCol * 16f + 8f;
        var player = new PlayerCharacter(new Vector2(spawnX, FloorTopY - 12f));

        const int requestFrame = 8;
        bool requested = false;

        var samples = RunWithLog(terrain, player, 30, (f, t) =>
        {
            if (f == requestFrame && !requested)
            {
                t.TryRequestTile(playerCol, FloorRow - 1, TileType.Stone);
                requested = true;
            }
        });

        // Find the frame where Graph.Growing dropped 1 → 0 (sprout finalized).
        int finalizeFrame = -1;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i - 1].GrowingSprouts == 1 && samples[i].GrowingSprouts == 0)
            {
                finalizeFrame = i;
                break;
            }
        }
        Assert.True(finalizeFrame > 0, "sprout never finalized during the run");
        _out.WriteLine($"finalize frame = {finalizeFrame}");

        LogSamples("focus on finalize frame", samples);

        // At and immediately after finalize, the body must not move downward
        // (vy must not flip positive ⇒ body falling) and y must not regress
        // toward the old (lower) standing height. Specifically: y on the
        // finalize frame must not be GREATER than the previous frame's y.
        // (y increases downward — a positive Δy is a "drop").
        float yBefore = samples[finalizeFrame - 1].PosY;
        float yAt     = samples[finalizeFrame].PosY;
        float yAfter  = samples[Math.Min(finalizeFrame + 1, samples.Count - 1)].PosY;
        _out.WriteLine($"y before/at/after finalize: {yBefore:F3} / {yAt:F3} / {yAfter:F3}");

        // Allow a small tolerance for the spring's natural ringing — but a
        // discrete "snap back to old floor height" would be ≥ 8 px.
        Assert.True(yAt - yBefore < 4f,
            $"body dropped {yAt - yBefore:F3}px at finalize frame {finalizeFrame} — sprout-to-solid handoff caused a discontinuity");
        Assert.True(yAfter - yBefore < 4f,
            $"body settled {yAfter - yBefore:F3}px below the pre-finalize position one frame later — sprout-to-solid handoff caused a discontinuity");
    }
}
