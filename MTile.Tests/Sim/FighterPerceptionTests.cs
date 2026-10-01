using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The sensing boundary (Plans/FIGHTER_DESIGN_PLAN.md §16): what a fighter brain can
// learn, when, and at what price.
//
//   * The tell: an opponent mid-windup is visible as such through EnemyTarget.
//   * Reaction frames: the exact read is served N frames late, from the history ring.
//   * Price: an exact read costs energy; an unpaid read returns the last bought view
//     with a growing age; the coarse read is free and tile-snapped.
//   * Sight: a hidden target is frozen where it was last seen, or tracked coarsely
//     when memory was bought. Every fighter raycasts.
//   * Probes: drop / wall / headroom ahead are reported where they are.
//   * All of it round-trips through a snapshot bit for bit.
public class FighterPerceptionTests(ITestOutputHelper output)
{
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    // With originTileX -4: a wall two tiles thick at tiles 8-9, a pit at tiles 20-21, and
    // a low ceiling (row 5) over tiles 24-28. Positions below are in tiles × TileSize.
    private static ChunkMap Course() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXOOOOOOOOOOOOOOXXXXXOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXOOXXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXOOXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float FloorTopY = 8 * Chunk.TileSize;   // 128
    private static readonly PlayerInput Idle = default;

    // A brain that writes what it sensed into Scratch so the test can read it:
    //   F0,F1 = exact read position   I0 = exact read age (frames)   I1 = exact Tell
    //   F2,F3 = coarse read position  F3 unused for probes; probes go to I1/F2 when asked
    private sealed class ProbeBrain : FighterController
    {
        public bool AskExact  { get; init; } = true;
        public int  ProbeDir  { get; init; } = 0;    // 0 ⇒ no probe
        public bool Attack    { get; init; } = false;
        public bool Walk      { get; init; } = false;

        protected override EnemyInput Decide(FighterSenses s, ref BrainScratch m)
        {
            var c = s.TargetCoarse();
            m.F2 = c.Position.X; m.F3 = c.Position.Y;
            if (AskExact)
            {
                var t = s.Target(out int age);
                m.F0 = t.Position.X; m.F1 = t.Position.Y;
                m.I0 = age; m.I1 = (int)t.Tell;
            }
            if (ProbeDir != 0)
            {
                var p = s.Probe(ProbeDir);
                m.I1 = p.Known ? 1 : 0;
                m.F0 = p.DropAhead; m.F1 = p.WallAhead; m.I0 = p.Headroom;
            }
            var input = new EnemyInput { AimWorld = c.Position, WantAttack = Attack };
            if (Walk) input.MoveDir.X = 1f;
            return input;
        }
    }

    private static EnemyBlueprint Sensor(ProbeBrain brain, float energyMax, float regen = 0f,
                                         int reaction = 0, bool remembers = false, bool los = true,
                                         bool melee = false) => new()
    {
        Kind          = EntityKind.Sparring,
        Radius        = 10f,
        Health        = 4f,
        Mass          = 1f,
        FrictionScale = 0.10f,
        EnergyMax     = energyMax,
        EnergyRegen   = regen,
        ReactionFrames  = reaction,
        RemembersTarget = remembers,
        TargetMemory    = los,            // TracksTarget: the raycast
        GroundPower     = 150f,
        Movement = () => new() { new EnemyIdleState(), new EnemyChaseState(), new EnemyAttackHoldState() },
        Actions  = () => melee ? new() { new EnemyMeleeAction() } : new(),
        Controller = brain,
    };

    private static Simulation Build(EnemyBlueprint bp, Vector2 pos, Vector2 playerPos, Action<Simulation> more = null) =>
        new(Floor(), playerPos, g => { g.SpawnEntity(new BlueprintEnemy(bp, pos)); more?.Invoke(g); });

    // ── Tell ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Tell_ShowsTheOpponentsWindup()
    {
        // A melee fighter 26 px from the player swings; our sensor stands beside it on
        // another team and reads the tell through the exact read.
        var striker = new EnemyBlueprint
        {
            Kind = EntityKind.Sparring, Radius = 10f, Health = 4f, Mass = 1f, Team = 3,
            Movement = () => new() { new EnemyIdleState(), new EnemyAttackHoldState() },
            Actions  = () => new() { new EnemyMeleeAction() },
            Controller = new StationaryAimController { AlertRange = 100f },
        };
        var sensorBp = Sensor(new ProbeBrain(), energyMax: 10f, regen: 1f);
        // The sensor is team 2 and far from the player; the striker (team 3) is its
        // nearest opponent. The striker's nearest opponent is the player (team 0).
        var sim = new Simulation(Floor(), new Vector2(40f, FloorTopY - 12f), g =>
        {
            g.SpawnEntity(new BlueprintEnemy(striker,  new Vector2(66f, FloorTopY - 11f)));
            g.SpawnEntity(new BlueprintEnemy(sensorBp, new Vector2(120f, FloorTopY - 11f)));
        });
        var strikerE = (EnemyEntity)sim.Entities[0];
        var sensorE  = (EnemyEntity)sim.Entities[1];

        bool sawWindup = false;
        for (int f = 0; f < 120 && !sawWindup; f++)
        {
            sim.Step(Idle);
            if (strikerE.CurrentActionName == nameof(EnemyMeleeAction) && strikerE.TellProgress is > 0f and < 1f)
                sawWindup = sensorE.Scratch.I1 == (int)ActionKind.Melee;
        }
        Assert.True(sawWindup, "the sensor never saw the striker's melee windup through its exact read");
        Assert.Equal(strikerE.Id, sensorE.CurrentTargetId);
    }

    // ── Reaction frames ──────────────────────────────────────────────────────

    [Fact]
    public void ReactionFrames_ServeTheViewThatManyFramesOld()
    {
        const int R = 4;
        var sim = Build(Sensor(new ProbeBrain(), energyMax: 10f, regen: 5f, reaction: R),
                        new Vector2(200f, FloorTopY - 11f), new Vector2(60f, FloorTopY - 12f));
        var bot = (EnemyEntity)sim.Entities[0];
        var playerX = new List<float>();
        // Walk the player right so its position changes every frame.
        for (int f = 0; f < 40; f++)
        {
            sim.Step(new PlayerInput { Right = true });
            playerX.Add(sim.Player.Body.Position.X);
            if (f >= 12)
            {
                // The view pushed on frame f is the player's position at the START of
                // that frame's entity update (players step first), so the newest view is
                // this frame's; the exact read is R views back.
                // Players update before entities but integrate AFTER them, so the view
                // pushed on frame f is the player's position at the end of frame f-1.
                Assert.Equal(R, bot.Scratch.I0);
                Assert.InRange(bot.Scratch.F0, playerX[f - R - 1] - 0.01f, playerX[f - R - 1] + 0.01f);
            }
        }
        output.WriteLine($"player moved {playerX[^1] - playerX[0]:F1}px; bot saw it {R} frames late throughout");
    }

    // ── Price ────────────────────────────────────────────────────────────────

    [Fact]
    public void ExactRead_CostsEnergy_AndUnpaidReadsGoStale()
    {
        var k = FighterCosts.Current;
        // Exactly two reads' worth, no regen.
        var sim = Build(Sensor(new ProbeBrain(), energyMax: k.SenseTargetCost * 2f),
                        new Vector2(200f, FloorTopY - 11f), new Vector2(60f, FloorTopY - 12f));
        var bot = (EnemyEntity)sim.Entities[0];

        sim.Step(Idle);
        Assert.InRange(bot.Energy, k.SenseTargetCost - 1e-4f, k.SenseTargetCost + 1e-4f);
        Assert.Equal(0, bot.Scratch.I0);
        sim.Step(Idle);
        Assert.InRange(bot.Energy, -1e-4f, 1e-4f);
        Assert.Equal(0, bot.Scratch.I0);
        // Broke: the same view comes back, one frame older each step.
        float boughtX = bot.Scratch.F0;
        for (int i = 1; i <= 5; i++)
        {
            sim.Step(new PlayerInput { Right = true });
            Assert.Equal(i, bot.Scratch.I0);
            Assert.Equal(boughtX, bot.Scratch.F0);
        }
        // The coarse read is free and keeps moving — snapped to the coarse grid.
        float q = FighterCosts.Current.CoarseQuantPx;
        Assert.NotEqual(bot.Scratch.F2, boughtX);
        Assert.InRange(MathF.Abs((bot.Scratch.F2 - q * 0.5f) % q), -1e-3f, 1e-3f);
    }

    // ── Sight ────────────────────────────────────────────────────────────────

    [Fact]
    public void HiddenTarget_FreezesWithoutMemory_TracksCoarselyWithIt()
    {
        // The wall is tiles 8-9; the sensor stands at tile 5, the player at tile 14 and
        // walks LEFT toward the wall (right would reach the pit at tile 20).
        float ts = Chunk.TileSize;
        Vector2 botPos = new(5f * ts + 4f, FloorTopY - 11f), playerPos = new(14f * ts + 4f, FloorTopY - 12f);
        var frozen = new Simulation(Course(), playerPos, g => g.SpawnEntity(new BlueprintEnemy(
            Sensor(new ProbeBrain(), 10f, 5f, remembers: false), botPos)));
        var memory = new Simulation(Course(), playerPos, g => g.SpawnEntity(new BlueprintEnemy(
            Sensor(new ProbeBrain(), 10f, 5f, remembers: true), botPos)));
        var fb = (EnemyEntity)frozen.Entities[0];
        var mb = (EnemyEntity)memory.Entities[0];

        for (int f = 0; f < 30; f++) { frozen.Step(new PlayerInput { Left = true }); memory.Step(new PlayerInput { Left = true }); }
        Assert.False(fb.PlayerVisible, "the wall should block line of sight");
        float px = frozen.Player.Body.Position.X;
        Assert.True(px < playerPos.X - 20f, $"player should have walked (x {px:F0} from {playerPos.X:F0})");

        // Without memory: the view is stuck at the seed (where it first "saw" the player —
        // the start position, at full age).
        Assert.InRange(fb.Scratch.F0, playerPos.X - 1f, playerPos.X + 1f);
        // With memory: the view tracks, to within a tile.
        Assert.InRange(mb.Scratch.F0, px - Chunk.TileSize, px + Chunk.TileSize);
        Assert.Equal((int)ActionKind.Special, mb.Scratch.I1);   // no tell through a wall
        output.WriteLine($"player at {px:F0}; frozen saw {fb.Scratch.F0:F0}, memory saw {mb.Scratch.F0:F0}");
    }

    // ── Probes ───────────────────────────────────────────────────────────────

    [Fact]
    public void Probe_ReportsDropWallAndHeadroom()
    {
        // Standing in tile 18: the pit starts at tile 20 (two tiles right); to the left
        // the wall is at tiles 8-9 — nine tiles away, beyond the probe's range — and the
        // ceiling over tiles 24-28 is not here. A second probe from tile 12 sees the wall
        // three tiles to its left.
        var pos = new Vector2(18 * Chunk.TileSize + 5f, FloorTopY - 11f);
        var right = new Simulation(Course(), new Vector2(600f, FloorTopY - 12f), g => g.SpawnEntity(new BlueprintEnemy(
            Sensor(new ProbeBrain { AskExact = false, ProbeDir = +1 }, 10f, 5f), pos)));
        var left  = new Simulation(Course(), new Vector2(600f, FloorTopY - 12f), g => g.SpawnEntity(new BlueprintEnemy(
            Sensor(new ProbeBrain { AskExact = false, ProbeDir = -1 }, 10f, 5f), new Vector2(12 * Chunk.TileSize + 5f, FloorTopY - 11f))));
        right.Step(Idle); left.Step(Idle);
        var rb = (EnemyEntity)right.Entities[0];
        var lb = (EnemyEntity)left.Entities[0];

        Assert.Equal(1, rb.Scratch.I1);
        Assert.InRange(rb.Scratch.F0, 1.0f * Chunk.TileSize, 2.0f * Chunk.TileSize);   // drop 1.5 tiles ahead
        Assert.Equal(FighterSenses.ProbeRangePx, rb.Scratch.F1);                       // no wall to the right
        Assert.Equal(4, rb.Scratch.I0);                                                // open sky
        Assert.Equal(FighterSenses.ProbeRangePx, lb.Scratch.F0);                       // floor runs to the wall
        Assert.InRange(lb.Scratch.F1, 2.0f * Chunk.TileSize, 3.0f * Chunk.TileSize);   // wall 2.5 tiles left
        output.WriteLine($"right: drop {rb.Scratch.F0:F0} wall {rb.Scratch.F1:F0} head {rb.Scratch.I0}; left: drop {lb.Scratch.F0:F0} wall {lb.Scratch.F1:F0}");
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void SensesAndHistory_RoundTripBitIdentical()
    {
        // A paying, reacting, probing, attacking brain against a walking player.
        var bp  = Sensor(new ProbeBrain { ProbeDir = +1, Attack = true, Walk = true }, 3f, 0.8f, reaction: 5, remembers: true, melee: true);
        var sim = Build(bp, new Vector2(120f, FloorTopY - 11f), new Vector2(60f, FloorTopY - 12f));
        PlayerInput At(int f) => new() { Right = f % 50 < 30, Space = f % 37 < 3 };

        const int K = 40, N = 200;
        for (int f = 0; f < K; f++) sim.Step(At(f));
        var snap = sim.Snapshot();
        var live = new List<string>(); var liveSum = new List<ulong>();
        for (int f = K; f < K + N; f++) { sim.Step(At(f)); live.Add(Probe(sim)); liveSum.Add(sim.Checksum()); }
        sim.Restore(snap);
        var replay = new List<string>(); var replaySum = new List<ulong>();
        for (int f = K; f < K + N; f++) { sim.Step(At(f)); replay.Add(Probe(sim)); replaySum.Add(sim.Checksum()); }
        for (int i = 0; i < N; i++)
        {
            if (live[i] != replay[i]) output.WriteLine($"divergence at step {i}:\nLIVE {live[i]}\nREPL {replay[i]}");
            Assert.Equal(live[i], replay[i]);
            Assert.Equal(liveSum[i], replaySum[i]);
        }
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        foreach (var e in sim.Entities)
            if (e is EnemyEntity en)
                sb.Append($"{Bits(en.Body.Position.X)},{Bits(en.Body.Position.Y)}|e{Bits(en.Energy)}|{en.CurrentActionName}|")
                  .Append($"s{Bits(en.Scratch.F0)},{Bits(en.Scratch.F1)},{en.Scratch.I0},{en.Scratch.I1}|")
                  .Append($"h{en.Targets.Count},{en.Targets.Head},{Bits(en.Targets.Back(0).Position.X)}|m{en.SenseMem.TargetFrame},{en.SenseMem.ProbeFrame}\n");
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");
}
