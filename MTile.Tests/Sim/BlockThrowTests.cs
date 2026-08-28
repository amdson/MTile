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

    // ── T5: release before the blocks have come loose ─────────────────────────────

    // Deep flat ground: surface row gty=3, two solid rows beneath.
    private static ChunkMap DeepGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    // The one-motion gesture: paint the block for 15 frames, then the same 8-frame
    // 300 px/s leftward swipe the held-throw test uses, then let go — with the spring
    // weakened so the block is still in the ground when the button comes up.
    private static InputScript PaintThenSwipeAndRelease(out Vector2 restAt)
    {
        var script = new InputScript()
            .For(10, new PlayerInput { MouseWorldPosition = OnBlock })
            .For(15, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = OnBlock });
        var p = OnBlock;
        for (int i = 0; i < 8; i++)
        {
            p += new Vector2(-300f * Dt, 0f);
            script.For(1, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = p });
        }
        restAt = p;
        return script;
    }

    private sealed class ContestTrace
    {
        public int     BallSpawnFrame = -1;
        public int     DetachFrame    = -1;
        public Vector2 DetachVel;
        public int     PointGoneFrame = -1;
        public List<string> Actions = new();
    }

    private static ContestTrace RunContest(InputScript script, ChunkMap terrain, int frames, ITestOutputHelper output)
    {
        var t = new ContestTrace();
        SimRunner.RunMulti(Build(script, terrain, frames),
            onFrameEntities: (f, ps, es) =>
            {
                t.Actions.Add(ps[0].CurrentActionName);
                bool point = false;
                foreach (var e in es)
                {
                    if (e is PullPointEntity) point = true;
                    if (e is not LobbedAreaProjectile ball) continue;
                    if (t.BallSpawnFrame < 0) t.BallSpawnFrame = f;
                    if (!ball.Tracking && t.DetachFrame < 0) { t.DetachFrame = f; t.DetachVel = ball.Body.Velocity; }
                }
                if (!point && t.BallSpawnFrame >= 0 && t.PointGoneFrame < 0) t.PointGoneFrame = f;
                if (!point && f > 30 && t.PointGoneFrame < 0 && t.BallSpawnFrame < 0) t.PointGoneFrame = f;
            });
        output.WriteLine($"ball@f{t.BallSpawnFrame} detach@f{t.DetachFrame} v=({t.DetachVel.X:F0},{t.DetachVel.Y:F0}) pointGone@f{t.PointGoneFrame}");
        return t;
    }

    private static void WithWeakSpring(float coeff, Action run)
    {
        var cfg  = MovementConfig.Current;
        float prev = cfg.PeelSpringCoeff;
        cfg.PeelSpringCoeff = coeff;
        try { run(); } finally { cfg.PeelSpringCoeff = prev; }
    }

    // Let go while the block is still in the ground: the point flies on, the contest
    // finishes against it, the block comes free into a ball that chases the point —
    // and detaches at the SAME speed as a clod that was already in hand for the same
    // swipe. That equality is the whole point of routing both through the point.
    [Fact]
    public void ReleaseBeforeBreakout_StillThrows_AtTheHeldThrowsSpeed()
    {
        WithPeel(() =>
        {
            // Reference: clod in hand, same 8-frame swipe, release.
            var held = GrabThenHold(new Vector2(100f, 30f), 20);
            var hp = new Vector2(100f, 30f);
            for (int i = 0; i < 8; i++)
            {
                hp += new Vector2(-300f * Dt, 0f);
                held.For(1, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = hp });
            }
            held.Forever(new PlayerInput { MouseWorldPosition = hp });
            var a = RunContest(held, FloatingBlock(), 100, output);

            // One motion: the block is still attached at release (weak spring), and
            // comes free only once the flying point has pulled far enough.
            ContestTrace b = null;
            WithWeakSpring(0.15f, () =>
            {
                var script = PaintThenSwipeAndRelease(out var restAt)
                    .Forever(new PlayerInput { MouseWorldPosition = restAt });
                b = RunContest(script, FloatingBlock(), 100, output);
            });

            const int releaseB = 33;   // 10 + 15 + 8
            Assert.True(b.BallSpawnFrame > releaseB, $"the block should come free AFTER the release (ball at f{b.BallSpawnFrame}, release f{releaseB})");
            Assert.True(b.DetachFrame > 0, "the freed ball should chase the point and detach");
            Assert.True(a.DetachFrame > 0);
            Assert.True(MathF.Abs(b.DetachVel.X - a.DetachVel.X) < 60f,
                        $"one-motion throw ({b.DetachVel.X:F0}) should match the held throw ({a.DetachVel.X:F0}) for the same swipe");
            Assert.True(b.DetachVel.X < -220f, $"and it should carry the swipe, got {b.DetachVel.X:F0}");
        });
    }

    // Anchored stone, released mid-pull: the flying point can't beat the glue; the
    // point dies empty within its cap, terrain intact, no ball.
    [Fact]
    public void ReleaseBeforeBreakout_AnchoredStone_DiesEmpty()
    {
        WithPeel(() =>
        {
            var terrain = DeepGround();
            int before  = 0;
            for (int gtx = 0; gtx <= 23; gtx++) for (int gty = 3; gty <= 5; gty++)
                if (terrain.GetCellState(gtx, gty) == TileState.Solid) before++;
            var onSurf = new Vector2(120f, 52f);
            var pullTo = new Vector2(120f, 20f);
            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onSurf })
                .For(20, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onSurf })
                .For(30, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo })
                .Forever(new PlayerInput { MouseWorldPosition = pullTo });

            var cfgMulti = new SimConfigMulti
            {
                Terrain = terrain, Frames = 100, Dt = Dt, Gravity = new Vector2(0f, 600f),
                Players = new[] { new SimPlayer { StartPosition = new Vector2(120f, 40f), Script = script } },
            };
            int pointGone = -1; bool sawBall = false;
            SimRunner.RunMulti(cfgMulti, onFrameEntities: (f, ps, es) =>
            {
                bool point = false;
                foreach (var e in es)
                {
                    if (e is PullPointEntity) point = true;
                    if (e is LobbedAreaProjectile) sawBall = true;
                }
                if (f >= 60 && !point && pointGone < 0) pointGone = f;
            });

            int after = 0;
            for (int gtx = 0; gtx <= 23; gtx++) for (int gty = 3; gty <= 5; gty++)
                if (terrain.GetCellState(gtx, gty) == TileState.Solid) after++;
            int cap = (int)MathF.Ceiling(MovementConfig.Current.GrabPointMaxSeconds / Dt) + 2;
            output.WriteLine($"point gone at f{pointGone} (release f60, cap +{cap})");
            Assert.False(sawBall, "stone's glue should hold against the flying point");
            Assert.Equal(before, after);
            Assert.True(pointGone >= 60 && pointGone <= 60 + cap, $"point should die within its cap, gone at f{pointGone}");
        });
    }

    // The reason the contest lives on the entity: a slash pressed the frame after the
    // release enters immediately AND the throw still lands.
    [Fact]
    public void SlashRightAfterRelease_EntersAndTheThrowStillLands()
    {
        WithPeel(() =>
        {
            WithWeakSpring(0.15f, () =>
            {
                var script = PaintThenSwipeAndRelease(out var restAt)
                    .For(1, new PlayerInput { MouseWorldPosition = restAt })
                    .For(6, new PlayerInput { LeftClick = true, MouseWorldPosition = restAt + new Vector2(20f, 0f) })
                    .Forever(new PlayerInput { MouseWorldPosition = restAt });
                var t = RunContest(script, FloatingBlock(), 100, output);

                // A click while the cursor is still moving parses as the stab gesture
                // (motion + click), so the follow-up lands as Stab rather than Slash —
                // either is "an attack entered immediately", which is what matters.
                const int release = 33;
                bool attacked = false;
                for (int f = release; f <= release + 2 && f < t.Actions.Count; f++)
                    if (t.Actions[f].Contains("Slash") || t.Actions[f].Contains("Stab")) attacked = true;
                output.WriteLine(string.Join(",", t.Actions.GetRange(release - 1, 10)));
                Assert.True(attacked, "the follow-up attack should enter within two frames of the release");
                Assert.True(t.BallSpawnFrame > release, "the block should still come free after the release");
                Assert.True(t.DetachFrame > 0 && t.DetachVel.X < -220f, $"and the throw should still fly, got vx={t.DetachVel.X:F0}");
            });
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
