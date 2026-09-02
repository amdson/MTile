using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Rollback gate for the block-grab pulling point (Plans/BLOCK_THROW_PLAN.md Phase 1).
// The peel group is a SPARSE snapshotted component on a helper entity, and the owning
// action keeps only an EntityId — so a mid-peel snapshot/restore has to reproduce the
// group (members, tethers, glue wear), the point's driven state, and the action's
// handle to it, bit for bit. Same shape as SnapshotRoundTripTests: run to K, snapshot,
// run on, restore, replay, compare a per-frame probe.
public class PullPointSnapshotTests(ITestOutputHelper output)
{
    // Deep flat ground: surface row gty=3, two solid rows beneath (same as BlockPeelTests).
    private static ChunkMap DeepGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);

    // The AnchoredStone gesture: paint the surface for 20 frames, then a gentle pull
    // that the stone's glue resists — so the group stays alive and worked (non-trivial
    // tether + glue-wear numbers) across the whole window without breaking out.
    private static PlayerInput InputAt(int frame)
    {
        var onSurf = new Vector2(120f, 52f);
        var pullTo = new Vector2(120f, 20f);
        if (frame < 10) return new PlayerInput { MouseWorldPosition = onSurf };
        if (frame < 30) return new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onSurf };
        return new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo };
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)}|{p.CurrentActionName}|")
          .Append($"pp{p.CurrentActionVars.PullPointId.Index}.{p.CurrentActionVars.PullPointId.Generation}|")
          .Append($"orb{p.CurrentActionVars.OrbHeld}|n{p.CurrentActionVars.PeelCount}|s{Bits(p.CurrentActionVars.PeelStrain)}\n");
        foreach (var e in sim.Entities)
        {
            sb.Append($"E{e.Id.Index}.{e.Id.Generation}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)}|hp{Bits(e.Health)}");
            if (e is PullPointEntity pt)
            {
                var g = pt.Group;
                sb.Append($"|driven{pt.Driven}|tgt{Bits(pt.TargetPos.X)},{Bits(pt.TargetPos.Y)}|harv{pt.HarvestBlocks}|ball{pt.BallId.Index}.{pt.BallId.Generation}|n{g.Count}|s{Bits(g.Strain)}|snap{g.Snapped}");
                for (int i = 0; i < g.Count; i++)
                {
                    var m = g.Members[i];
                    sb.Append($"|m{m.Gtx},{m.Gty}:{Bits(m.Tether)},{Bits(m.GlueWear)}");
                }
            }
            if (e is LobbedAreaProjectile ball)
                sb.Append($"|track{ball.Tracking}|pt{ball.PointId.Index}.{ball.PointId.Generation}|b{ball.Budget}/{ball.HarvestBlocks}|v{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}");
            sb.Append('\n');
        }
        for (int gtx = 4; gtx <= 11; gtx++) sb.Append((int)sim.Chunks.GetCellState(gtx, 3));
        sb.Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void MidPeel_SnapshotRestore_ReproducesGroupBitForBit()
    {
        const int K = 40;    // snapshot mid-pull, group live and worked
        const int N = 75;

        var cfg  = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = true;
        try
        {
            var sim = new Simulation(DeepGround(), new Vector2(120f, 40f), _ => { });
            for (int f = 0; f < K; f++) sim.Step(InputAt(f));

            // The point must exist and be mid-peel at the snapshot frame, or the test
            // proves nothing.
            PullPointEntity live = null;
            foreach (var e in sim.Entities) if (e is PullPointEntity pt) live = pt;
            Assert.NotNull(live);
            Assert.True(live.PeelCount > 0, "group should be painted by the snapshot frame");
            Assert.Equal(live.Id, sim.Player.CurrentActionVars.PullPointId);

            var snap = sim.Snapshot();

            var liveTrace = new List<string>();
            for (int f = K; f < N; f++) { sim.Step(InputAt(f)); liveTrace.Add(Probe(sim)); }

            sim.Restore(snap);
            var replayTrace = new List<string>();
            for (int f = K; f < N; f++) { sim.Step(InputAt(f)); replayTrace.Add(Probe(sim)); }

            Assert.Equal(liveTrace.Count, replayTrace.Count);
            for (int i = 0; i < liveTrace.Count; i++)
            {
                if (liveTrace[i] != replayTrace[i])
                {
                    output.WriteLine($"Divergence at replay frame {K + i}:");
                    output.WriteLine("LIVE:\n"   + liveTrace[i]);
                    output.WriteLine("REPLAY:\n" + replayTrace[i]);
                }
                Assert.Equal(liveTrace[i], replayTrace[i]);
            }
            output.WriteLine($"Round-trip identical across {liveTrace.Count} frames after restore@{K}.");
            output.WriteLine(liveTrace[^1]);
        }
        finally { cfg.BlockPeelEnabled = prev; }
    }

    // Floating solid at cell (4,0) over a floor — a free-hanging block pops in one
    // sweep, so this scenario reaches a HELD BALL (tracking projectile + point linked
    // by id) and then a release mid-chase. The round trip has to reproduce the
    // point↔ball ids, the tracker state, the bleed clock and the detach.
    private static ChunkMap FloatingBlock() => SimTerrain.FromAscii(@"
        OOOOXOOOOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        XXXXXXXXXXXX
        XXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static PlayerInput ThrowInputAt(int frame)
    {
        // Cell (4,0) center on the current Chunk.TileSize grid.
        var onBlock = new Vector2(4 * Chunk.TileSize + Chunk.TileSize / 2f, Chunk.TileSize / 2f);
        // 48 px out (absolute, not tile-scaled — beats the free block's core-only glue,
        // a px-tuned config threshold unrelated to the grid).
        var pullTo  = onBlock + new Vector2(48f, 0f);
        var hold    = new Vector2(100f, 30f);
        if (frame < 10) return new PlayerInput { MouseWorldPosition = onBlock };
        if (frame < 25) return new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = onBlock };
        if (frame < 35) return new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = pullTo };
        if (frame < 55) return new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = hold };
        // Swipe left for 8 frames, then release.
        if (frame < 63) return new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = hold + new Vector2(-5f * (frame - 54), 0f) };
        return new PlayerInput { MouseWorldPosition = hold + new Vector2(-40f, 0f) };
    }

    [Theory]
    [InlineData(45)]   // ball in hand, tracking a driven point
    [InlineData(60)]   // mid-swipe, still held
    [InlineData(64)]   // just released: point flying, ball chasing
    public void HeldBallAndThrow_SnapshotRestore_ReproducesBitForBit(int k)
    {
        const int N = 110;
        var cfg  = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = true;
        try
        {
            // Cell (4,2) center: one row below the block, mid-gap, clear of both the
            // block above and the floor below.
            var start = new Vector2(4 * Chunk.TileSize + Chunk.TileSize / 2f, 2 * Chunk.TileSize + Chunk.TileSize / 2f);
            var sim = new Simulation(FloatingBlock(), start, _ => { });
            for (int f = 0; f < k; f++) sim.Step(ThrowInputAt(f));

            bool sawBall = false;
            foreach (var e in sim.Entities) if (e is LobbedAreaProjectile) sawBall = true;
            Assert.True(sawBall, "a ball should exist at the snapshot frame");

            var snap = sim.Snapshot();
            var liveTrace = new List<string>();
            for (int f = k; f < N; f++) { sim.Step(ThrowInputAt(f)); liveTrace.Add(Probe(sim)); }

            sim.Restore(snap);
            var replayTrace = new List<string>();
            for (int f = k; f < N; f++) { sim.Step(ThrowInputAt(f)); replayTrace.Add(Probe(sim)); }

            for (int i = 0; i < liveTrace.Count; i++)
            {
                if (liveTrace[i] != replayTrace[i])
                {
                    output.WriteLine($"Divergence at replay frame {k + i}:");
                    output.WriteLine("LIVE:\n"   + liveTrace[i]);
                    output.WriteLine("REPLAY:\n" + replayTrace[i]);
                }
                Assert.Equal(liveTrace[i], replayTrace[i]);
            }
            output.WriteLine(liveTrace[^1]);
        }
        finally { cfg.BlockPeelEnabled = prev; }
    }

    // A point that died AFTER the snapshot (here: the player lets go) must come back on
    // restore — the drop-and-recreate path through EntityFactory.Rehydrate, including
    // the sparse group component.
    [Fact]
    public void PointDeadAfterSnapshot_IsRehydratedWithItsGroup()
    {
        const int K = 40;
        var cfg  = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = true;
        try
        {
            var sim = new Simulation(DeepGround(), new Vector2(120f, 40f), _ => { });
            for (int f = 0; f < K; f++) sim.Step(InputAt(f));
            var snap   = sim.Snapshot();
            string atK = Probe(sim);

            // Release: the action exits and hands the point off; it contests the stone
            // for up to GrabPointMaxSeconds, loses, and is swept.
            for (int f = 0; f < 25; f++) sim.Step(new PlayerInput { MouseWorldPosition = new Vector2(120f, 20f) });
            bool anyPoint = false;
            foreach (var e in sim.Entities) if (e is PullPointEntity) anyPoint = true;
            Assert.False(anyPoint, "the point should be gone after release");

            sim.Restore(snap);
            Assert.Equal(atK, Probe(sim));
        }
        finally { cfg.BlockPeelEnabled = prev; }
    }
}
