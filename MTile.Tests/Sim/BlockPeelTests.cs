using System;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// BlockGrabAction peel mode (BlockPeelEnabled) — the Shift+LMB terrain grab rework.
// Paint and pull are one phase: a gaussian kernel under the held cursor deposits
// tether onto solid cells (they join the grab group), and the player→group spring —
// superlinear in cursor distance from the group COM — pulls against the group's
// aggregate block→world glue. These pin the design's five load-bearing behaviors:
// a free-hanging block pops in one sweep; anchored stone resists a gentle pull and a
// lapsed attempt takes nothing; overpulling snaps the spring and cancels; the group
// caps at PeelMemberBuffer.Capacity; sand peels off stone where stone holds; and the
// legacy one-frame drag-rip survives behind the flag.
public class BlockPeelTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    // Floating solid at cell (4,0), floor at rows 3-4. The block hangs free (zero
    // outward edges → core-only glue), the floor is the player's footing.
    private static ChunkMap FloatingBlock() => SimTerrain.FromAscii(@"
        OOOOXOOOOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        XXXXXXXXXXXX
        XXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    // Deep flat ground: surface row gty=3, two solid rows beneath.
    private static ChunkMap DeepGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain, Vector2 start, int frames) => new SimConfigMulti
    {
        Terrain = terrain,
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = start, Script = script } },
    };

    private static int SolidCount(ChunkMap c, int gtx0, int gtx1, int gty0, int gty1)
    {
        int n = 0;
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (c.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }

    private static void SetType(ChunkMap map, int gtx, int gty, TileType type)
    {
        int cx = gtx >= 0 ? gtx / Chunk.Size : (gtx - Chunk.Size + 1) / Chunk.Size;
        int cy = gty >= 0 ? gty / Chunk.Size : (gty - Chunk.Size + 1) / Chunk.Size;
        Assert.True(map.TryGet(new Point(cx, cy), out var chunk), $"no chunk under ({gtx},{gty})");
        chunk.Tiles[gtx - cx * Chunk.Size, gty - cy * Chunk.Size].Type = type;
    }

    // Per-frame cursor interpolation — the "sweep" half of paint-and-pull.
    private static InputScript Sweep(InputScript s, Vector2 from, Vector2 to, int frames)
    {
        for (int i = 0; i < frames; i++)
            s.For(1, new PlayerInput { Shift = true, LeftClick = true,
                                       MouseWorldPosition = Vector2.Lerp(from, to, (i + 1f) / frames) });
        return s;
    }

    private static void WithPeel(bool enabled, Action run)
    {
        var cfg  = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = enabled;
        try { run(); } finally { cfg.BlockPeelEnabled = prev; }
    }

    // A free-hanging block has core-only glue, so one sweep — paint it, pull away,
    // release — grabs and throws it.
    [Fact]
    public void FreeHangingBlock_OneSweep_GrabsAndThrows()
    {
        WithPeel(true, () =>
        {
            var terrain = FloatingBlock();
            var onBlock = new Vector2(72f, 8f);    // cell (4,0) center
            var pullTo  = new Vector2(120f, 8f);   // 3 tiles out: beats core glue, far under the snap cap

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onBlock })
                .For(15, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onBlock })
                .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo })
                .Forever(new PlayerInput { MouseWorldPosition = pullTo });

            bool sawOrb = false, sawThrow = false;
            int floorAtRelease = -1;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(72f, 40f), frames: 60),
                onFrame: (f, ps) =>
                {
                    if (ps[0].CurrentActionVars.OrbHeld) sawOrb = true;
                    // Release is frame 35; the thrown orb lands and erupts its budget
                    // onto the floor a few frames later, so "floor untouched by the
                    // GRAB" is measured here, not at the end.
                    if (f == 35) floorAtRelease = SolidCount(terrain, 0, 11, 3, 4);
                },
                onFrameEntities: (f, ps, es) =>
                {
                    foreach (var e in es) if (e is LobbedAreaProjectile) sawThrow = true;
                });

            Assert.True(sawOrb, "The pulled block should break out into a carried orb.");
            Assert.True(sawThrow, "Releasing with the orb in hand should throw a LobbedAreaProjectile.");
            Assert.Equal(TileState.Empty, terrain.GetCellState(4, 0));
            Assert.Equal(24, floorAtRelease);
        });
    }

    // Anchored stone: a gentle pull (force below the group's aggregate glue) holds,
    // and releasing mid-peel takes nothing — no half-broken terrain, no orb.
    [Fact]
    public void AnchoredStone_GentlePull_TakesNothing()
    {
        WithPeel(true, () =>
        {
            var terrain = DeepGround();
            int before  = SolidCount(terrain, 0, 23, 3, 5);
            var onSurf  = new Vector2(120f, 52f);   // in cell (7,3)
            var pullTo  = new Vector2(120f, 20f);   // ~2.3 tiles: real pull, well under stone's glue

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onSurf })
                .For(20, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onSurf })
                .For(30, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo })
                .Forever(new PlayerInput { MouseWorldPosition = pullTo });

            bool sawGrab = false, sawOrb = false;
            int maxPeel = 0;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(120f, 40f), frames: 80),
                onFrame: (f, ps) =>
                {
                    if (ps[0].CurrentActionName == "BlockGrabAction") sawGrab = true;
                    if (ps[0].CurrentActionVars.OrbHeld) sawOrb = true;
                    maxPeel = Math.Max(maxPeel, ps[0].CurrentActionVars.PeelCount);
                });

            output.WriteLine($"max group size = {maxPeel}");
            Assert.True(sawGrab, "Shift+LMB on terrain should enter BlockGrabAction.");
            Assert.True(maxPeel > 0, "Painting the surface should tether at least one cell.");
            Assert.False(sawOrb, "A pull weaker than stone's glue must not break anything out.");
            Assert.Equal(before, SolidCount(terrain, 0, 23, 3, 5));
        });
    }

    // Pulling past PeelSpringMax snaps the player→group spring: the attempt dies
    // whole — state ends despite the still-held button, terrain intact, no orb.
    [Fact]
    public void Overpull_SnapsSpring_CancelsAttempt()
    {
        WithPeel(true, () =>
        {
            var terrain = DeepGround();
            int before  = SolidCount(terrain, 0, 23, 3, 5);
            var onSurf  = new Vector2(120f, 52f);
            var jerkTo  = new Vector2(320f, 52f);   // 12.5 tiles from the group — way past the cap

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onSurf })
                .For(20, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onSurf })
                .Forever(new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = jerkTo });

            bool sawGrab = false, sawOrb = false;
            string lastAction = "";
            SimRunner.RunMulti(Build(script, terrain, new Vector2(120f, 40f), frames: 80),
                onFrame: (f, ps) =>
                {
                    if (ps[0].CurrentActionName == "BlockGrabAction") sawGrab = true;
                    if (ps[0].CurrentActionVars.OrbHeld) sawOrb = true;
                    if (f == 79) lastAction = ps[0].CurrentActionName;
                });

            output.WriteLine($"action at final frame = {lastAction}");
            Assert.True(sawGrab, "The grab should have started before the snap.");
            Assert.False(sawOrb, "A snapped spring must not hand over an orb.");
            Assert.NotEqual("BlockGrabAction", lastAction);   // snap ended the state mid-hold
            Assert.Equal(before, SolidCount(terrain, 0, 23, 3, 5));
        });
    }

    // Sweeping the kernel across a wide patch admits blocks only up to the fixed
    // group capacity — the 26th cell never joins, no matter how much gets painted.
    [Fact]
    public void Painting_CapsGroupAtCapacity()
    {
        WithPeel(true, () =>
        {
            var terrain = DeepGround();
            int before  = SolidCount(terrain, 0, 23, 3, 5);
            var press   = new Vector2(192f, 56f);

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = press })
                .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = press });
            Sweep(script, press, new Vector2(112f, 56f), 20);
            Sweep(script, new Vector2(112f, 56f), new Vector2(272f, 56f), 40);
            Sweep(script, new Vector2(272f, 76f), new Vector2(112f, 76f), 40);
            script.Forever(new PlayerInput { MouseWorldPosition = press });   // release

            int maxPeel = 0;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(192f, 40f), frames: 140),
                onFrame: (f, ps) => maxPeel = Math.Max(maxPeel, ps[0].CurrentActionVars.PeelCount));

            output.WriteLine($"max group size = {maxPeel}");
            Assert.Equal(PeelMemberBuffer.Capacity, maxPeel);
            Assert.Equal(before, SolidCount(terrain, 0, 23, 3, 5));   // painted, never broke out
        });
    }

    // Material weights + cross-material edges: the same gentle pull that stone shrugs
    // off (AnchoredStone_GentlePull_TakesNothing) rips a sand patch off its stone bed.
    [Fact]
    public void SandOnStone_SameGentlePull_PeelsFree()
    {
        WithPeel(true, () =>
        {
            var terrain = DeepGround();
            for (int gtx = 6; gtx <= 8; gtx++) SetType(terrain, gtx, 3, TileType.Sand);
            var onSurf = new Vector2(120f, 52f);
            var pullTo = new Vector2(120f, 20f);

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onSurf })
                .For(20, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onSurf })
                .For(30, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo })
                .Forever(new PlayerInput { MouseWorldPosition = pullTo });

            bool sawOrb = false;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(120f, 40f), frames: 80),
                onFrame: (f, ps) => { if (ps[0].CurrentActionVars.OrbHeld) sawOrb = true; });

            Assert.True(sawOrb, "Sand's weak glue should lose to the same pull stone resisted.");
            for (int gtx = 6; gtx <= 8; gtx++)
                Assert.Equal(TileState.Empty, terrain.GetCellState(gtx, 3));
        });
    }

    // Flag off: the legacy one-frame drag-rip — press, drag past the threshold, and
    // the 1.6-tile radius around the press site is destroyed outright (6 solid cells
    // on this flat surface).
    [Fact]
    public void FlagOff_LegacyDragRip_StillWorks()
    {
        WithPeel(false, () =>
        {
            var terrain = DeepGround();
            int before  = SolidCount(terrain, 0, 23, 3, 5);
            var onSurf  = new Vector2(120f, 52f);
            var dragTo  = new Vector2(140f, 52f);   // 20px > the 12px drag threshold

            var script = new InputScript()
                .For(10, new PlayerInput { MouseWorldPosition = onSurf })
                .For(5,  new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onSurf })
                .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = dragTo })
                .Forever(new PlayerInput { MouseWorldPosition = dragTo });

            // Measured at the release frame: the runner now really throws the orb, and
            // the landing erupts its budget back into the ground a dozen frames later.
            int atRelease = -1;
            SimRunner.RunMulti(Build(script, terrain, new Vector2(120f, 40f), frames: 50),
                onFrame: (f, ps) => { if (f == 25) atRelease = SolidCount(terrain, 0, 23, 3, 5); });

            Assert.Equal(before - 6, atRelease);
        });
    }
}
