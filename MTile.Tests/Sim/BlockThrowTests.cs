using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The block THROW (Plans/BLOCK_THROW_PLAN.md T4): the grab's ball is a tracking
// LobbedAreaProjectile that follows the pulling point; release hands the point off at
// the cursor's swipe velocity and the ball converges to it and detaches. These pin the
// three feel rules — a still release drops, a swipe throws at swipe speed, and the held
// clod rests near the hand however far the cursor is — plus the entity bookkeeping.
public class BlockThrowTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    // Floating solid at cell (4,0), floor at rows 3-4 (same as BlockPeelTests): a
    // free-hanging block pops in one sweep, so every test starts from a clod in hand.
    private static ChunkMap FloatingBlock() => SimTerrain.FromAscii(@"
        OOOOXOOOOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        XXXXXXXXXXXX
        XXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 OnBlock = new(72f, 8f);    // cell (4,0) center
    private static readonly Vector2 PullTo  = new(120f, 8f);   // 3 tiles out: beats core glue
    private static readonly Vector2 Start   = new(72f, 40f);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain, int frames) => new SimConfigMulti
    {
        Terrain = terrain,
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = Start, Script = script } },
    };

    // Paint the block, pull it free, then hold the clod with the cursor parked at
    // `hold` for `holdFrames`. Callers append the release gesture.
    private static InputScript GrabThenHold(Vector2 hold, int holdFrames) => new InputScript()
        .For(10, new PlayerInput { MouseWorldPosition = OnBlock })
        .For(15, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = OnBlock })
        .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = PullTo })
        .For(holdFrames, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = hold });

    private sealed class BallTrace
    {
        public int     DetachFrame = -1;
        public Vector2 DetachVel;
        public Vector2 DetachPos;
        public bool    SawTracking;
        public float   MaxHeldDist;
        public int     LastHeldFrame = -1;
    }

    private static BallTrace Run(InputScript script, int frames, ITestOutputHelper output)
    {
        var trace = new BallTrace();
        SimRunner.RunMulti(Build(script, FloatingBlock(), frames),
            onFrameEntities: (f, ps, es) =>
            {
                foreach (var e in es)
                {
                    if (e is not LobbedAreaProjectile ball) continue;
                    if (ball.Tracking)
                    {
                        trace.SawTracking = true;
                        if (ps[0].CurrentActionVars.OrbHeld)
                        {
                            trace.LastHeldFrame = f;
                            trace.MaxHeldDist = MathF.Max(trace.MaxHeldDist,
                                (ball.Body.Position - ps[0].Body.Position).Length());
                        }
                    }
                    else if (trace.DetachFrame < 0)
                    {
                        trace.DetachFrame = f;
                        trace.DetachVel   = ball.Body.Velocity;
                        trace.DetachPos   = ball.Body.Position;
                    }
                }
            });
        output.WriteLine($"tracking={trace.SawTracking} lastHeld=f{trace.LastHeldFrame} maxHeldDist={trace.MaxHeldDist:F1} " +
                         $"detach=f{trace.DetachFrame} v=({trace.DetachVel.X:F0},{trace.DetachVel.Y:F0}) p=({trace.DetachPos.X:F0},{trace.DetachPos.Y:F0})");
        return trace;
    }

    private static void WithPeel(Action run)
    {
        var cfg  = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = true;
        try { run(); } finally { cfg.BlockPeelEnabled = prev; }
    }

    // Mouse parked, then released: the point hands off at ≈0, the ball is already on
    // it, and it detaches within a few frames at ≈0 relative speed — a drop.
    [Fact]
    public void StillRelease_DropsTheClod()
    {
        WithPeel(() =>
        {
            var hold   = new Vector2(100f, 30f);
            var script = GrabThenHold(hold, 20)
                .Forever(new PlayerInput { MouseWorldPosition = hold });
            var t = Run(script, 80, output);

            Assert.True(t.SawTracking, "the freed block should become a tracking ball");
            Assert.True(t.DetachFrame >= 0, "the ball should detach after release");
            Assert.True(t.DetachFrame - 55 <= 6, $"detach took {t.DetachFrame - 55} frames after release");
            Assert.True(MathF.Abs(t.DetachVel.X) < 30f, $"drop should have ~no horizontal speed, got {t.DetachVel.X}");
            Assert.True(t.DetachVel.Y < 60f, $"drop should not be flung, vy={t.DetachVel.Y}");
        });
    }

    // A leftward swipe of 300 px/s for a few frames, then release: the point flies left
    // at ~300 and the ball detaches flying left at ~300 — the swipe IS the throw.
    [Fact]
    public void SwipeRelease_ThrowsAtSwipeVelocity()
    {
        WithPeel(() =>
        {
            var hold   = new Vector2(100f, 30f);
            var script = GrabThenHold(hold, 20);
            const float vx = -300f;
            var p = hold;
            for (int i = 0; i < 8; i++)
            {
                p += new Vector2(vx * Dt, 0f);
                script.For(1, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = p });
            }
            script.Forever(new PlayerInput { MouseWorldPosition = p });
            var t = Run(script, 100, output);

            Assert.True(t.DetachFrame >= 0, "the ball should detach after release");
            Assert.True(t.DetachVel.X < -220f && t.DetachVel.X > -380f,
                        $"throw should carry the swipe (~{vx}), got vx={t.DetachVel.X}");
            Assert.True(MathF.Abs(t.DetachVel.Y) < 80f, $"a horizontal swipe should throw flat, vy={t.DetachVel.Y}");
        });
    }

    // The held clod rests near the hand — GrabHandDistance + GrabHandLean at most —
    // even with the cursor parked ~48 px away, and it never sinks below the feet.
    [Fact]
    public void HeldClod_RestsNearTheHand()
    {
        WithPeel(() =>
        {
            var far    = Start + new Vector2(48f, -10f);
            var script = GrabThenHold(far, 40)
                .Forever(new PlayerInput { MouseWorldPosition = far });
            var t = Run(script, 80, output);

            var cfg = MovementConfig.Current;
            float limit = cfg.GrabHandDistance + cfg.GrabHandLean + 6f;   // + tracker lag
            Assert.True(t.SawTracking);
            Assert.True(t.LastHeldFrame > 40, "the clod should be held through the hold window");
            Assert.True(t.MaxHeldDist <= limit, $"held clod strayed to {t.MaxHeldDist:F1} px (limit {limit:F1})");
        });
    }

    // Bookkeeping: a released grab leaves exactly one ball and no point behind, the
    // action exits on the release frame, and nothing routes the release into another
    // Shift+LMB action.
    [Fact]
    public void Release_ExitsActionThatFrame_AndPointDies()
    {
        WithPeel(() =>
        {
            var hold   = new Vector2(100f, 30f);
            var script = GrabThenHold(hold, 20)
                .Forever(new PlayerInput { MouseWorldPosition = hold });

            var actions = new List<string>();
            int pointGoneFrame = -1, ballsWhenPointGone = -1;
            SimRunner.RunMulti(Build(script, FloatingBlock(), 70),
                onFrameEntities: (f, ps, es) =>
                {
                    actions.Add(ps[0].CurrentActionName);
                    if (f < 55 || pointGoneFrame >= 0) return;
                    int points = 0, balls = 0;
                    foreach (var e in es)
                    {
                        if (e is PullPointEntity) points++;
                        if (e is LobbedAreaProjectile) balls++;
                    }
                    if (points == 0) { pointGoneFrame = f; ballsWhenPointGone = balls; }
                });

            output.WriteLine($"point gone at f{pointGoneFrame}, balls then = {ballsWhenPointGone}");
            Assert.Equal("BlockGrabAction", actions[54]);
            Assert.NotEqual("BlockGrabAction", actions[55]);   // released at frame 55 ⇒ out that frame
            // The point outlives the release only for the chase, then dies; the ball
            // (dropped, still falling) is the one thing left of the grab.
            Assert.True(pointGoneFrame >= 55 && pointGoneFrame <= 65, $"point should die shortly after release, gone at f{pointGoneFrame}");
            Assert.Equal(1, ballsWhenPointGone);
        });
    }
}
