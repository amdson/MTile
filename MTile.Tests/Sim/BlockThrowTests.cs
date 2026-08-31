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

    // The reason the contest lives on the entity: a slash pressed right after the release
    // still enters — the flying pull-point doesn't swallow the player's next attack —
    // AND the throw still lands.
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

                // The follow-up is a genuine short click, so it lands as GroundSlash1 once
                // BlockGrabAction's own recovery countdown finishes — about 7 frames after
                // the release. It is NOT instant, and it must not be: this used to read
                // `StabAction` two frames in, which was the Shift+LMB gesture bug (the
                // throw's own long dragging press emitted a Stab intent that fired the
                // moment Shift came up), and the window here was two frames wide because
                // that phantom stab was filling it. InputParser now suppresses the drag
                // gestures for a Shift-owned press; see InputParserGestureTests.
                const int release = 33;
                int attackFrame = -1;
                for (int f = release; f < t.Actions.Count && f <= release + 12; f++)
                    if (t.Actions[f].Contains("Slash")) { attackFrame = f; break; }
                output.WriteLine(string.Join(",", t.Actions.GetRange(release - 1, 10)));
                Assert.True(attackFrame > 0,
                    "the follow-up slash should enter once the throw's recovery clears");
                // The regression guard, at the integration level the unit tests can't see:
                // the throw's drag must never resurface as a stab.
                for (int f = release; f < t.Actions.Count && f <= release + 12; f++)
                    Assert.False(t.Actions[f].Contains("Stab"),
                        $"the block throw's drag must not parse as a stab (frame {f} = {t.Actions[f]})");
                Assert.True(t.BallSpawnFrame > release, "the block should still come free after the release");
                Assert.True(t.DetachFrame > 0 && t.DetachVel.X < -220f, $"and the throw should still fly, got vx={t.DetachVel.X:F0}");
            });
        });
    }

    // ── Contact fuse: the thrown clod bursts on a body ────────────────────────────

    // Throw the clod at an opponent standing in its path. It should burst ON them —
    // damage + knockback along the throw — rather than sailing through to land behind.
    // It also flies through its own thrower first, which is the faction exclusion.
    [Fact]
    public void ThrownClod_BurstsOnAnOpponent()
    {
        WithPeel(() =>
        {
            var hold   = new Vector2(100f, 30f);
            var script = GrabThenHold(hold, 20);
            var p = hold;
            for (int i = 0; i < 8; i++)          // 300 px/s leftward swipe, then release
            {
                p += new Vector2(-300f * Dt, 0f);
                script.For(1, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = p });
            }
            script.Forever(new PlayerInput { MouseWorldPosition = p });

            // Target 48 px to the thrower's left — in the swipe's line, past the thrower.
            var target = new Vector2(24f, 40f);
            var cfg = new SimConfigMulti
            {
                Terrain = FloatingBlock(), Frames = 100, Dt = Dt, Gravity = new Vector2(0f, 600f),
                Players = new[]
                {
                    new SimPlayer { StartPosition = Start,  Script = script },
                    new SimPlayer { StartPosition = target, Script = InputScript.Always(default),
                                    Faction = Faction.Player2 },
                },
            };

            float hurtAt = -1f; int hurtFrame = -1, ballGoneFrame = -1;
            float shove = 0f;
            SimRunner.RunMulti(cfg, onFrameEntities: (f, ps, es) =>
            {
                if (hurtFrame < 0 && ps[1].Combat.DamageTaken > 0f)
                {
                    hurtFrame = f;
                    hurtAt    = ps[1].Combat.DamageTaken;
                    shove     = ps[1].Body.Velocity.X;
                }
                bool ball = false;
                foreach (var e in es) if (e is LobbedAreaProjectile) ball = true;
                if (!ball && hurtFrame >= 0 && ballGoneFrame < 0) ballGoneFrame = f;
            });

            output.WriteLine($"hurt@f{hurtFrame} pct={hurtAt:F1} vx={shove:F0} ballGone@f{ballGoneFrame}");
            Assert.True(hurtFrame > 0, "the thrown clod should hit the opponent it was thrown at");
            Assert.True(shove < -50f, $"the burst should shove them along the throw, got vx={shove:F0}");
            Assert.True(ballGoneFrame >= 0 && ballGoneFrame - hurtFrame <= 2,
                        $"the clod should be spent on contact (hit f{hurtFrame}, gone f{ballGoneFrame})");
        });
    }

    // ── Terrain fuse: the clod breaks where it strikes ────────────────────────────

    // FloatingBlock's grabbable block over a floor long enough that a 300 px/s throw
    // lands on it rather than sailing off the edge.
    private static ChunkMap WideFloor() => SimTerrain.FromAscii(@"
        OOOOXOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);


    // A flat-out horizontal throw arcs into the floor with most of its speed intact.
    // It should burst there — within a frame or two of the contact and within a tile
    // of where it touched down — not skid along until friction has stopped it, which
    // is what the old velocity-halt heuristic waited for.
    [Fact]
    public void ThrownClod_BurstsWhereItStrikesTheGround()
    {
        WithPeel(() =>
        {
            var hold   = new Vector2(100f, 30f);
            var script = GrabThenHold(hold, 20);
            var p = hold;
            for (int i = 0; i < 8; i++)          // rightward, into the long stretch of floor
            {
                p += new Vector2(300f * Dt, 0f);
                script.For(1, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = p });
            }
            script.Forever(new PlayerInput { MouseWorldPosition = p });

            int contactFrame = -1, goneFrame = -1;
            float contactX = 0f, contactSpeed = 0f, lastX = 0f;
            SimRunner.RunMulti(Build(script, WideFloor(), 120),
                onFrameEntities: (f, ps, es) =>
                {
                    LobbedAreaProjectile ball = null;
                    foreach (var e in es) if (e is LobbedAreaProjectile b && !b.Tracking) ball = b;
                    if (ball != null)
                    {
                        lastX = ball.Body.Position.X;
                        // onFrameEntities runs after StepSwept, so this is the contact
                        // impulse from the step that just ran.
                        if (contactFrame < 0 && ball.Body.LastImpulseMagnitude > 0.01f)
                        {
                            contactFrame = f;
                            contactX     = ball.Body.Position.X;
                            contactSpeed = ball.Body.Velocity.Length();
                        }
                    }
                    else if (contactFrame >= 0 && goneFrame < 0) goneFrame = f;
                });

            output.WriteLine($"first ground contact f{contactFrame} at x={contactX:F0} (|v|={contactSpeed:F0}), " +
                             $"gone f{goneFrame} at x={lastX:F0}");
            Assert.True(contactFrame > 0, "the thrown clod should reach the floor");
            Assert.True(goneFrame > 0 && goneFrame - contactFrame <= 3,
                        $"the clod should burst on contact, not after settling (contact f{contactFrame}, gone f{goneFrame})");
            Assert.True(MathF.Abs(lastX - contactX) < Chunk.TileSize,
                        $"it should break where it struck: touched at x={contactX:F0}, gone at x={lastX:F0}");
        });
    }

    // Point-blank: a wall close enough that the clod reaches it DURING the post-release
    // chase, before the ball has converged on the flying point. It must burst on the
    // wall it struck. The chase used to swallow this — no fuse ran while tracking, and
    // a stopped ball never converges on a point still flying, so the clod hung against
    // the wall for the full GrabChaseMaxSeconds and then dropped to the floor.
    [Fact]
    public void ThrownClod_BurstsOnAWallHitDuringTheChase()
    {
        WithPeel(() =>
        {
            // Grab block at (4,0); wall column at gtx=3, rows 1-2 (x 48..64, y 16..48),
            // two tiles left of the thrower — inside the chase window.
            var terrain = SimTerrain.FromAscii(@"
                OOOOXOOOOOOOOOOOOOOOOOOO
                OOOXOOOOOOOOOOOOOOOOOOOO
                OOOXOOOOOOOOOOOOOOOOOOOO
                XXXXXXXXXXXXXXXXXXXXXXXX
                XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

            var hold   = new Vector2(100f, 30f);
            var script = GrabThenHold(hold, 20);
            var p = hold;
            for (int i = 0; i < 8; i++)          // swipe left, into the wall
            {
                p += new Vector2(-300f * Dt, 0f);
                script.For(1, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = p });
            }
            script.Forever(new PlayerInput { MouseWorldPosition = p });

            int hitFrame = -1, goneFrame = -1;
            bool hitWhileChasing = false;
            var hitAt = Vector2.Zero;
            var lastAt = Vector2.Zero;
            SimRunner.RunMulti(Build(script, terrain, 120),
                onFrameEntities: (f, ps, es) =>
                {
                    LobbedAreaProjectile ball = null;
                    foreach (var e in es) if (e is LobbedAreaProjectile b) ball = b;
                    if (ball != null)
                    {
                        lastAt = ball.Body.Position;
                        if (hitFrame < 0 && ball.Body.LastImpulseMagnitude > 45f)
                        {
                            hitFrame = f;
                            hitAt    = ball.Body.Position;
                            hitWhileChasing = ball.Tracking;
                        }
                    }
                    else if (hitFrame >= 0 && goneFrame < 0) goneFrame = f;
                });

            output.WriteLine($"struck f{hitFrame} at ({hitAt.X:F0},{hitAt.Y:F0}) chasing={hitWhileChasing}, " +
                             $"gone f{goneFrame} at ({lastAt.X:F0},{lastAt.Y:F0})");
            Assert.True(hitFrame > 0, "the thrown clod should reach the wall");
            Assert.True(hitWhileChasing, "this scenario is only meaningful if the wall is hit mid-chase");
            Assert.True(goneFrame > 0 && goneFrame - hitFrame <= 3,
                        $"it should burst on the wall (struck f{hitFrame}, gone f{goneFrame})");
            // The floor is at y=48; bursting on the wall means it never got there.
            Assert.True(lastAt.Y < 40f, $"it should burst at the wall, not fall to the floor first (y={lastAt.Y:F0})");
        });
    }

    // The mirror: the same throw doesn't burst on the thrower it flies straight through.
    [Fact]
    public void ThrownClod_PassesThroughItsOwnThrower()
    {
        WithPeel(() =>
        {
            var hold   = new Vector2(100f, 30f);
            var script = GrabThenHold(hold, 20);
            var p = hold;
            for (int i = 0; i < 8; i++)
            {
                p += new Vector2(-300f * Dt, 0f);
                script.For(1, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = p });
            }
            script.Forever(new PlayerInput { MouseWorldPosition = p });

            float selfHurt = 0f;
            SimRunner.RunMulti(Build(script, FloatingBlock(), 100),
                onFrame: (f, ps) => selfHurt = MathF.Max(selfHurt, ps[0].Combat.DamageTaken));

            Assert.Equal(0f, selfHurt);
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
