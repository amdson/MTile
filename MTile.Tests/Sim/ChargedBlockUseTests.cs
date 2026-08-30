using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// What a charged block is FOR. Charging one costs a whole avalanche meter
// (BlockChargeTests covers the gesture); these pin the two ways that meter comes back
// out:
//
//   1. Peeled into a grab's clod, it turns the throw from a splat into a demolition
//      charge — the burst scales and the clod craters instead of leaving a mound.
//   2. Standing near an eruption when it fires, it cashes itself in, which is the only
//      way an eruption exceeds EruptMax — the block adds a whole meter of its own.
//
// The third sink — destroying the block where it stands, which detonates it — lives in
// ChargedBlastTests, because it hangs off BreakCell rather than off either verb here.
public class ChargedBlockUseTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private static Vector2 CellCenter(int gtx, int gty) => new(
        gtx * Chunk.TileSize + Chunk.TileSize * 0.5f,
        gty * Chunk.TileSize + Chunk.TileSize * 0.5f);

    // ── 1. The grab clod ────────────────────────────────────────────────────────

    // Same shape BlockThrowTests uses: a free-hanging block at cell (4,0) pops in one
    // sweep, so the clod is in hand within a few frames.
    private static ChunkMap FloatingBlock() => SimTerrain.FromAscii(@"
        OOOOXOOOOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        XXXXXXXXXXXX
        XXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 OnBlock = new(72f, 8f);    // cell (4,0) center
    private static readonly Vector2 PullTo  = new(120f, 8f);   // 3 tiles out: beats core glue
    private static readonly Vector2 GrabStart = new(72f, 40f);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain, int frames, Vector2 start) =>
        new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = frames,
            Dt      = Dt,
            Gravity = new Vector2(0f, 600f),
            Players = new[] { new SimPlayer { StartPosition = start, Script = script } },
        };

    private static InputScript GrabThenHold(Vector2 hold, int holdFrames) => new InputScript()
        .For(10, new PlayerInput { MouseWorldPosition = OnBlock })
        .For(15, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = OnBlock })
        .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = PullTo })
        .For(holdFrames, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = hold });

    private static void WithPeel(Action run)
    {
        var cfg   = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = true;
        try { run(); } finally { cfg.BlockPeelEnabled = prev; }
    }

    // The charge has to be read off the cell BEFORE the peel breaks it — BreakCell
    // clears the flag, so a naive "check after harvesting" always reports zero. This is
    // the test that would catch that ordering regression.
    [Fact]
    public void PeelingAChargedBlock_ProducesAChargedClod()
    {
        WithPeel(() =>
        {
            var terrain = FloatingBlock();
            terrain.Charge.Set(4, 0);

            int maxCharged = 0, maxHarvest = 0;
            var hold = new Vector2(100f, 30f);
            SimRunner.RunMulti(Build(GrabThenHold(hold, 20), terrain, 60, GrabStart),
                onFrameEntities: (f, ps, es) =>
                {
                    foreach (var e in es)
                        if (e is LobbedAreaProjectile ball)
                        {
                            maxCharged = Math.Max(maxCharged, ball.ChargedBlocks);
                            maxHarvest = Math.Max(maxHarvest, ball.HarvestBlocks);
                        }
                });

            output.WriteLine($"harvest={maxHarvest} charged={maxCharged}");
            Assert.True(maxHarvest > 0, "setup: the block should have been peeled into a clod");
            Assert.True(maxCharged > 0, "a charged block peeled into the clod should arm it");
        });
    }

    // The uncharged control: the same grab on a plain block yields a plain clod, so the
    // flag above is actually reading the charge and not just "a clod exists".
    [Fact]
    public void PeelingAPlainBlock_ProducesAPlainClod()
    {
        WithPeel(() =>
        {
            var terrain = FloatingBlock();

            int maxCharged = 0, maxHarvest = 0;
            var hold = new Vector2(100f, 30f);
            SimRunner.RunMulti(Build(GrabThenHold(hold, 20), terrain, 60, GrabStart),
                onFrameEntities: (f, ps, es) =>
                {
                    foreach (var e in es)
                        if (e is LobbedAreaProjectile ball)
                        {
                            maxCharged = Math.Max(maxCharged, ball.ChargedBlocks);
                            maxHarvest = Math.Max(maxHarvest, ball.HarvestBlocks);
                        }
                });

            output.WriteLine($"harvest={maxHarvest} charged={maxCharged}");
            Assert.True(maxHarvest > 0, "setup: the block should have been peeled into a clod");
            Assert.Equal(0, maxCharged);
        });
    }

    private static int SolidCount(ChunkMap c, int gtx0, int gtx1, int gty0, int gty1)
    {
        int n = 0;
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (c.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }

    // Wide flat ground with the player parked far to the left, doing nothing. The clod
    // is spawned directly rather than grabbed: driving the real gesture would end with
    // the grab's LMB release parsed as a Stab, and a stab carves terrain of its own —
    // it would be measuring the wrong verb.
    private static ChunkMap BurstGround()
    {
        var rows = new List<string>();
        for (int y = 0; y < 4; y++) rows.Add(new string('O', 32));
        for (int y = 4; y < 7; y++) rows.Add(new string('X', 32));
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    // Throw a clod at the ground and let it burst. Returns the change in solid tiles
    // under the impact: a plain clod deposits its material as a mound, a charged one
    // skips the deposit entirely and craters instead.
    private int BurstDelta(int charged)
    {
        var terrain = BurstGround();
        var sim = new Simulation(terrain, CellCenter(2, 3));      // player parked far left
        for (int f = 0; f < 5; f++) sim.Step(new PlayerInput());

        int before = SolidCount(terrain, 12, 26, 2, 6);

        // Born above the impact site and thrown down hard, so it bursts on the STRIKE
        // fuse at a predictable cell well away from the player.
        var ball = new LobbedAreaProjectile(
            CellCenter(19, 1), new Vector2(0f, 600f),
            budget: 6, TileType.Stone, hitId: sim.HitIds.Next(), owner: Faction.Player1);
        if (charged > 0)
            ball = LobbedAreaProjectile.MakeTracking(
                CellCenter(19, 1), 6, TileType.Stone, sim.HitIds.Next(),
                Faction.Player1, EntityId.None, charged);
        sim.SpawnEntity(ball);

        for (int f = 0; f < 120; f++) sim.Step(new PlayerInput());

        int after = SolidCount(terrain, 12, 26, 2, 6);
        output.WriteLine($"charged={charged}: solid {before} → {after}");
        return after - before;
    }

    // The trade the meter buys, at the moment of impact: the same object either builds
    // or destroys depending on what went into it. A plain clod splats into a mound; a
    // charged one detonates and takes terrain with it.
    [Fact]
    public void AChargedClod_CratersWhereAPlainOneMounds()
    {
        int plain   = BurstDelta(charged: 0);
        int boosted = BurstDelta(charged: 2);

        output.WriteLine($"plain delta={plain:+#;-#;0}, charged delta={boosted:+#;-#;0}");
        Assert.True(plain > 0, $"a plain clod should leave a mound, not a hole (delta {plain})");
        Assert.True(boosted < 0, $"a charged clod should crater (delta {boosted})");
    }

    // The cap is what keeps a lucky harvest of a whole charged wall from clearing the
    // screen — the fantasy is a demolition charge, not a nuke.
    [Fact]
    public void ClodCharge_IsCappedForTheBlastMath()
    {
        var ball = LobbedAreaProjectile.MakeTracking(
            Vector2.Zero, blocks: 40, TileType.Stone, hitId: 1, Faction.Player1, EntityId.None, charged: 40);

        output.WriteLine($"raw={ball.ChargedBlocks} effective={ball.EffectiveCharge}");
        Assert.Equal(40, ball.ChargedBlocks);
        Assert.True(ball.EffectiveCharge < ball.ChargedBlocks, "the blast multiplier must be capped");
    }

    // ── 2. The eruption ─────────────────────────────────────────────────────────

    private const int OriginGtx = 8;
    private const int GroundTop = 6;

    private static ChunkMap ErupGround()
    {
        var rows = new List<string>();
        for (int y = 0; y < GroundTop; y++) rows.Add(new string('O', 24));
        for (int y = GroundTop; y < 12; y++) rows.Add(new string('X', 24));
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    // Runs the real eruption gesture (the one EruptionPillarTests drives): charge with
    // the cursor buried, sweep up out of the ground, drag right along the surface, and
    // release while the ball is still fast. Returns the mass the spawned MassBall was
    // born with, plus the terrain so the caller can check what discharged.
    private (float mass, ChunkMap terrain) RunEruption(Action<ChunkMap> stage)
    {
        var chunks = ErupGround();
        stage?.Invoke(chunks);

        var sim = new Simulation(chunks, CellCenter(OriginGtx, GroundTop - 1));
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput());

        PlayerInput Rmb(Vector2 mouse) => new() { RightClick = true, MouseWorldPosition = mouse };

        var chargePos = CellCenter(OriginGtx, GroundTop + 2);
        for (int f = 0; f < 70; f++) sim.Step(Rmb(chargePos));
        Assert.True(sim.Player.Abilities.Meters.CanFireEruption,
            "setup: the underground hold banked too little charge");

        float exitY = CellCenter(OriginGtx, GroundTop - 1).Y;
        float y = chargePos.Y;
        while (y > exitY) { y -= 20f; sim.Step(Rmb(new Vector2(chargePos.X, y))); }
        float x = chargePos.X;
        for (int f = 0; f < 6; f++) { x += 20f; sim.Step(Rmb(new Vector2(x, exitY))); }

        sim.Step(new PlayerInput());   // release → the eruption fires on this step

        // Read the ball on the frame it was born, before it has leaked anything away.
        float mass = 0f;
        foreach (var e in sim.Entities)
            if (e is MassBall ball) mass = MathF.Max(mass, ball.BuildMass);
        return (mass, chunks);
    }

    // The headline: a charged block near the launch site throws a whole EruptMax of its
    // own into the eruption, so the ball is born carrying more than a full meter could
    // ever buy on its own.
    [Fact]
    public void EruptionNearAChargedBlock_ExceedsTheMeter()
    {
        var (plain,   _)       = RunEruption(null);
        var (boosted, terrain) = RunEruption(c =>
        {
            // In the surface row, right where the stroke exits and sweeps — inside the
            // recruit radius of the release point.
            c.Charge.Set(OriginGtx + 2, GroundTop);
            c.Charge.Set(OriginGtx + 3, GroundTop);
        });

        output.WriteLine($"plain mass={plain:F1}, boosted={boosted:F1}, still charged={terrain.Charge.Count}");
        Assert.True(plain > 0f, "setup: the plain gesture should have erupted at all");
        Assert.True(boosted > plain * 2f,
            $"two recruited blocks should roughly triple the eruption; {plain:F1} → {boosted:F1}");
        Assert.Equal(0, terrain.Charge.Count);   // both blocks cashed themselves in
    }

    // Recruitment is radius-limited and only takes what it can reach — a charge staged
    // across the map keeps its tint.
    [Fact]
    public void Recruitment_OnlyTakesBlocksInRadius()
    {
        var chunks = ErupGround();
        chunks.Charge.Set(OriginGtx, GroundTop);        // right under the site
        chunks.Charge.Set(OriginGtx + 20, GroundTop);   // far away

        int taken = BlockEruptionHelpers.RecruitChargedBlocks(
            chunks, CellCenter(OriginGtx, GroundTop - 1));

        output.WriteLine($"taken={taken}, remaining={chunks.Charge.Count}");
        Assert.Equal(1, taken);
        Assert.False(chunks.Charge.IsCharged(OriginGtx, GroundTop));
        Assert.True(chunks.Charge.IsCharged(OriginGtx + 20, GroundTop));
    }

    // A release that DOESN'T erupt must not silently discharge the wall the player was
    // saving. The gate is the meter, so an empty meter is the case to pin.
    [Fact]
    public void AReleaseThatDoesNotErupt_LeavesChargedBlocksAlone()
    {
        var chunks = ErupGround();
        chunks.Charge.Set(OriginGtx, GroundTop);

        var sim = new Simulation(chunks, CellCenter(OriginGtx, GroundTop - 1));
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput());

        // A brief stroke in open air: no time buried, so no charge, so no eruption.
        float exitY = CellCenter(OriginGtx, GroundTop - 2).Y;
        float x = CellCenter(OriginGtx, GroundTop - 2).X;
        for (int f = 0; f < 6; f++)
        {
            x += 20f;
            sim.Step(new PlayerInput { RightClick = true, MouseWorldPosition = new Vector2(x, exitY) });
        }
        sim.Step(new PlayerInput());   // release

        output.WriteLine($"meter={sim.Player.Abilities.Meters.EruptMove:F1}, charged={chunks.Charge.Count}");
        Assert.False(sim.Player.Abilities.Meters.CanFireEruption, "setup: this stroke must not erupt");
        Assert.True(chunks.Charge.IsCharged(OriginGtx, GroundTop),
            "a non-erupting release must not discharge nearby blocks");
    }

    // The meter cap scales with recruited charge rather than flattening it. Without
    // this, cheap material (foam converts ~16× better than stone) would hit MaxBallMass
    // on the meter alone and every recruited block would be worth exactly nothing.
    [Fact]
    public void ConsumeEruptionMass_CapScalesWithRecruitedCharge()
    {
        float Fire(float bonus)
        {
            var m = new BuildMeters { EruptMove = BuildMeters.EruptMax };
            return m.ConsumeEruptionMass(TileType.Foam, bonus);
        }

        float plain   = Fire(0f);
        float boosted = Fire(2f * BuildMeters.EruptMax);

        output.WriteLine($"foam: plain={plain:F0} boosted={boosted:F0} (cap {BuildMeters.MaxBallMass:F0})");
        Assert.Equal(BuildMeters.MaxBallMass, plain, 1);          // cheap material caps out
        Assert.True(boosted > plain * 2.5f,
            $"the cap should scale with the recruited charge; {plain:F0} → {boosted:F0}");
    }
}
