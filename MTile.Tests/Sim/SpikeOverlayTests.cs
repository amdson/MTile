using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Spike overlays (Plans/BLOCK_OWNERSHIP_SPIKES.md §2): picker key 5 selects spikes, the
// RMB build gesture overlays an existing SOLID cell (never air) for 2x a stone placement,
// the cell is reinforced 2x and loses the overlay when it breaks, and opposing bodies
// touching it take velocity-scaled contact damage on a per-victim cooldown. The placer's
// side — and a co-op partner — never does. All of it snapshots and replays.
public class SpikeOverlayTests(ITestOutputHelper output)
{
    private const int FloorRow = 14;
    private const float T = Chunk.TileSize;

    // A wide floor with tall headroom (so a drop can build real speed) and a pillar on
    // the right for wall contacts.
    private static ChunkMap Arena()
    {
        var rows = new List<string>();
        for (int r = 0; r < FloorRow; r++)
            rows.Add(r >= FloorRow - 4 ? "OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOXX" : "OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO");
        rows.Add("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        rows.Add("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
        return SimTerrain.FromAscii(string.Join("\n", rows));
    }

    private static float FloorTop => FloorRow * T;
    private static int CellX(float worldX) => (int)MathF.Floor(worldX / T);
    private static Vector2 StandAt(float x) => new(x, FloorTop - 20f);

    private static readonly TileSpikes.Side P1Side = new(Faction.Player1, Teams.Player);
    private static readonly TileSpikes.Side P2Side = new(Faction.Player2, Teams.Player2);

    // Spike a run of floor cells centred under world x.
    private static void SpikeFloor(ChunkMap chunks, float x, TileSpikes.Side side, int halfWidth = 3)
    {
        int c = CellX(x);
        for (int gx = c - halfWidth; gx <= c + halfWidth; gx++) chunks.Spikes.Set(gx, FloorRow, side);
    }

    private static Simulation TwoPlayers(float p1x, float p2x, bool coop = false)
    {
        Simulation sim;
        if (coop)
        {
            var stage = new Stage
            {
                Name = "spike-coop", Terrain = Arena, PlayerSpawn = StandAt(p1x), Coop = true, Populate = _ => { },
            };
            sim = new Simulation(new GameConfig { SpawnSecondPlayer = false }, stage);
        }
        else sim = new Simulation(Arena(), StandAt(p1x));
        sim.AddSecondaryPlayer(StandAt(p2x));
        return sim;
    }

    private static void Run(Simulation sim, int frames, Func<int, PlayerInput> p0 = null, Func<int, PlayerInput> p1 = null)
    {
        for (int f = 0; f < frames; f++)
            sim.Step(p0?.Invoke(f) ?? default, p1?.Invoke(f) ?? default);
    }

    // Per-frame health trace of one player, recording each HP drop.
    private static List<float> Drops(Simulation sim, PlayerCharacter who, int frames,
                                     Func<int, PlayerInput> p0 = null, Func<int, PlayerInput> p1 = null)
    {
        var drops = new List<float>();
        float prev = who.Health;
        for (int f = 0; f < frames; f++)
        {
            sim.Step(p0?.Invoke(f) ?? default, p1?.Invoke(f) ?? default);
            if (who.Health < prev - 1e-4f) drops.Add(prev - who.Health);
            prev = who.Health;
        }
        return drops;
    }

    // ── Placement ───────────────────────────────────────────────────────────────

    [Fact]
    public void Num5_SelectsSpikes_AndNumberKeysReturnToMaterial()
    {
        var sim = new Simulation(Arena(), StandAt(60f));
        Run(sim, 2, f => new PlayerInput { Num5 = true });
        Assert.True(sim.Player.SpikesSelected);
        Run(sim, 1, f => new PlayerInput { Num1 = true });
        Assert.False(sim.Player.SpikesSelected);
        Assert.Equal(TileType.Stone, sim.ActiveBlockType);
    }

    [Fact]
    public void Spikes_CannotBePlacedInAir()
    {
        var sim = new Simulation(Arena(), StandAt(60f));
        Run(sim, 30);   // settle
        var air = new Vector2(60f + 2 * T, FloorTop - 2.5f * T);
        int ax = CellX(air.X), ay = (int)MathF.Floor(air.Y / T);
        Assert.Equal(TileState.Empty, sim.Chunks.GetCellState(ax, ay));

        Run(sim, 1, f => new PlayerInput { Num5 = true, MouseWorldPosition = air });
        Run(sim, 20, f => new PlayerInput { RightClick = true, MouseWorldPosition = air });

        Assert.Equal(0, sim.Chunks.Spikes.Count);
        // ...and the held RMB didn't fall through to building something there instead.
        Assert.Equal(TileState.Empty, sim.Chunks.GetCellState(ax, ay));
        Assert.Empty(sim.Chunks.ActiveSprouts);
    }

    [Fact]
    public void Spikes_OnStone_CostTwiceStone_AndMarkCellWithPlacerSide()
    {
        var sim = new Simulation(Arena(), StandAt(60f));
        Run(sim, 30);
        var target = new Vector2(60f + T, FloorTop + 0.5f * T);   // floor cell, in reach
        int gx = CellX(target.X);
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(gx, FloorRow));
        Assert.Equal(TileType.Stone, sim.Chunks.GetCellType(gx, FloorRow));

        Run(sim, 1, f => new PlayerInput { Num5 = true, MouseWorldPosition = target });
        var m = sim.Player.Abilities.Meters;
        float before = m.BuildMove + m.EruptMove + m.Build;
        Run(sim, 1, f => new PlayerInput { RightClick = true, MouseWorldPosition = target });
        float after = m.BuildMove + m.EruptMove + m.Build;

        Assert.True(sim.Chunks.Spikes.TryGet(gx, FloorRow, out var side));
        Assert.Equal(Faction.Player1, side.Faction);
        Assert.Equal(1, sim.Chunks.Spikes.Count);

        float stone = MaterialStrengths.BuildCostFor(TileType.Stone);
        Assert.Equal(2f * stone, MaterialStrengths.SpikeBuildCost, 4);
        // Meters regen/refill a little inside the frame; the spend dominates.
        float spent = before - after;
        output.WriteLine($"stone {stone:F3}  spike cost {MaterialStrengths.SpikeBuildCost:F3}  measured spend {spent:F3}");
        Assert.InRange(spent, 2f * stone - 0.6f, 2f * stone + 0.05f);

        // Holding on the same cell doesn't re-spike or re-charge.
        float hold0 = m.BuildMove + m.EruptMove + m.Build;
        Run(sim, 10, f => new PlayerInput { RightClick = true, MouseWorldPosition = target });
        Assert.True(m.BuildMove + m.EruptMove + m.Build >= hold0 - 1e-3f);
    }

    [Fact]
    public void TrySpikeCell_SpendsExactlySpikeCost_AndRefusesAirAndRespike()
    {
        var chunks = Arena();
        var ab = new PlayerAbilityState();
        var ctx = new EnvironmentContext { Chunks = chunks, Faction = Faction.Player2, Team = Teams.Player2 };
        float pool = ab.Meters.BuildMove + ab.Meters.EruptMove;

        Assert.False(BlockPlaceAction.TrySpikeCell(ctx, ab, 3, FloorRow - 1));   // air
        Assert.Equal(pool, ab.Meters.BuildMove + ab.Meters.EruptMove);

        Assert.True(BlockPlaceAction.TrySpikeCell(ctx, ab, 3, FloorRow));
        Assert.Equal(pool - MaterialStrengths.SpikeBuildCost, ab.Meters.BuildMove + ab.Meters.EruptMove, 4);
        Assert.True(chunks.Spikes.TryGet(3, FloorRow, out var s));
        Assert.Equal(Faction.Player2, s.Faction);

        Assert.False(BlockPlaceAction.TrySpikeCell(ctx, ab, 3, FloorRow));       // already spiked
        Assert.Equal(pool - MaterialStrengths.SpikeBuildCost, ab.Meters.BuildMove + ab.Meters.EruptMove, 4);
    }

    // ── Reinforcement / break ───────────────────────────────────────────────────

    [Fact]
    public void SpikedCell_TakesTwiceTheHits_AndLosesOverlayOnBreak()
    {
        var chunks = Arena();
        float hp = TileDamage.MaxHPFor(TileType.Stone);
        float hit = hp / 4f;

        int Hits(int gx)
        {
            for (int n = 1; n < 100; n++)
                if (chunks.DamageCell(gx, FloorRow, hit)) return n;
            return -1;
        }

        int plain = Hits(2);
        chunks.Spikes.Set(5, FloorRow, P1Side);
        int spiked = Hits(5);
        output.WriteLine($"plain {plain} hits, spiked {spiked} hits");
        Assert.Equal(4, plain);
        Assert.Equal(2 * plain, spiked);
        Assert.False(chunks.Spikes.IsSpiked(5, FloorRow));
        Assert.Equal(TileState.Empty, chunks.GetCellState(5, FloorRow));

        // Any break path clears it (BreakCell is the single funnel).
        chunks.Spikes.Set(8, FloorRow, P1Side);
        Assert.True(chunks.BreakCell(8, FloorRow));
        Assert.Equal(0, chunks.Spikes.Count);
    }

    // ── Contact damage ──────────────────────────────────────────────────────────

    [Fact]
    public void OpposingPlayer_StandingOnSpikes_TakesTickingDamage()
    {
        var sim = TwoPlayers(40f, 200f);
        var p2 = sim.SecondaryPlayers[0].Player;
        Run(sim, 40);   // both settle on the floor
        float start = p2.Health;
        SpikeFloor(sim.Chunks, p2.Body.Position.X, P1Side);

        var drops = Drops(sim, p2, 120);   // 2 s on P1's spikes
        output.WriteLine($"P2 drops: {string.Join(", ", drops.ConvertAll(d => d.ToString("F3")))}  total {start - p2.Health:F3}");
        Assert.InRange(drops.Count, 3, 5);      // ~one tick per 0.5 s cooldown
        foreach (var d in drops) Assert.Equal(SpikeContactSystem.ContactDamage, d, 2);
        Assert.Equal(sim.Player.MaxHealth, sim.Player.Health);
    }

    [Fact]
    public void Placer_AndOwnSide_TakeNoSpikeDamage()
    {
        var sim = TwoPlayers(40f, 200f);
        var p1 = sim.Player;
        var p2 = sim.SecondaryPlayers[0].Player;
        Run(sim, 40);
        SpikeFloor(sim.Chunks, p1.Body.Position.X, P1Side);   // P1 on its own spikes
        SpikeFloor(sim.Chunks, p2.Body.Position.X, P2Side);   // P2 on its own spikes
        Run(sim, 120);
        Assert.Equal(p1.MaxHealth, p1.Health);
        Assert.Equal(p2.MaxHealth, p2.Health);
    }

    [Fact]
    public void Coop_PartnerTakesNoDamageFromPlacersSpikes()
    {
        var sim = TwoPlayers(40f, 200f, coop: true);
        Assert.True(sim.Coop);
        var p2 = sim.SecondaryPlayers[0].Player;
        Run(sim, 40);
        SpikeFloor(sim.Chunks, p2.Body.Position.X, P1Side);
        Run(sim, 120);
        Assert.Equal(p2.MaxHealth, p2.Health);
    }

    [Fact]
    public void KnockedIntoSpikes_AtSpeed_HurtsMuchMore()
    {
        // Dropped from height onto P1's spiked floor: ~400 px/s of normal approach.
        float dropY = FloorTop - 140f;
        var sim = new Simulation(Arena(), StandAt(40f));
        sim.AddSecondaryPlayer(new Vector2(200f, dropY));
        var p2 = sim.SecondaryPlayers[0].Player;
        SpikeFloor(sim.Chunks, 200f, P1Side);

        float vyAtLanding = 0f;
        float before = p2.Health, firstTick = 0f;
        for (int f = 0; f < 90 && firstTick == 0f; f++)
        {
            vyAtLanding = p2.Body.Velocity.Y;
            sim.Step(default, default);
            if (p2.Health < before - 1e-4f) firstTick = before - p2.Health;
        }
        output.WriteLine($"knocked-in: approach {vyAtLanding:F0} px/s  first tick {firstTick:F3} HP " +
                         $"(walk-contact tick {SpikeContactSystem.ContactDamage:F2})");
        Assert.True(vyAtLanding > 300f, $"drop only reached {vyAtLanding} px/s");
        Assert.True(firstTick >= 4f * SpikeContactSystem.ContactDamage,
            $"impact tick {firstTick} should dwarf the walking tick");
        Assert.True(firstTick <= SpikeContactSystem.MaxImpactDamage + 1e-3f);
        Assert.Equal(SpikeContactSystem.DamageForSpeed(vyAtLanding), firstTick, 1);
    }

    [Fact]
    public void LaunchedSideways_IntoSpikedWall_HurtsMoreThanWalking()
    {
        // P2 launched right at 400 px/s into P1's spiked pillar (columns 36-37), from
        // a couple of tiles out.
        var sim = new Simulation(Arena(), StandAt(40f));
        sim.AddSecondaryPlayer(StandAt(360f));
        var p2 = sim.SecondaryPlayers[0].Player;
        Run(sim, 30);
        for (int gy = FloorRow - 4; gy < FloorRow; gy++) sim.Chunks.Spikes.Set(36, gy, P1Side);
        p2.Body.Velocity = new Vector2(400f, -150f);

        float before = p2.Health, firstTick = 0f, vx = 0f;
        for (int f = 0; f < 60 && firstTick == 0f; f++)
        {
            vx = p2.Body.Velocity.X;
            sim.Step(default, default);
            if (p2.Health < before - 1e-4f) firstTick = before - p2.Health;
        }
        output.WriteLine($"wall: approach {vx:F0} px/s  first tick {firstTick:F3} HP");
        Assert.True(firstTick > SpikeContactSystem.ContactDamage, $"tick {firstTick}");
    }

    [Fact]
    public void OpposingEntity_OnSpikes_TakesDamage_SameSideEntityDoesNot()
    {
        var sim = new Simulation(Arena(), StandAt(40f));
        var enemy = EntityFactory.Ball(new Vector2(150f, FloorTop - 12f));
        enemy.Faction = Faction.Enemy;
        var friendly = EntityFactory.Ball(new Vector2(260f, FloorTop - 12f));
        friendly.Faction = Faction.Player1;
        sim.SpawnEntity(enemy);
        sim.SpawnEntity(friendly);
        Run(sim, 40);
        SpikeFloor(sim.Chunks, enemy.Body.Position.X, P1Side);
        SpikeFloor(sim.Chunks, friendly.Body.Position.X, P1Side);
        float e0 = enemy.Health, f0 = friendly.Health;
        Run(sim, 60);
        output.WriteLine($"enemy {e0:F2} -> {enemy.Health:F2}, friendly {f0:F2} -> {friendly.Health:F2}");
        Assert.True(enemy.Health < e0);
        Assert.Equal(f0, friendly.Health);
    }

    // ── Snapshot / rollback ─────────────────────────────────────────────────────

    [Fact]
    public void SpikeState_SurvivesSnapshotRestore_AndReplaysIdentically()
    {
        var sim = TwoPlayers(40f, 200f);
        var p2 = sim.SecondaryPlayers[0].Player;
        Run(sim, 40);
        SpikeFloor(sim.Chunks, p2.Body.Position.X, P1Side);
        Run(sim, 10);   // a tick lands → a live cooldown entry

        var snap = sim.Snapshot();
        ulong snapSum = sim.Checksum();
        int snapSpikes = sim.Chunks.Spikes.Count;

        // P1 selects spikes and lays one more on the floor beside itself mid-window, so the
        // replay crosses a placement as well as contact ticks.
        var target = new Vector2(sim.Player.Body.Position.X + T, FloorTop + 0.5f * T);
        PlayerInput P0(int f) => f == 3 ? new PlayerInput { Num5 = true, MouseWorldPosition = target }
                               : f is >= 5 and < 8 ? new PlayerInput { RightClick = true, MouseWorldPosition = target }
                               : default;

        var sums = new List<ulong>();
        var hp = new List<float>();
        for (int f = 0; f < 90; f++) { sim.Step(P0(f), default); sums.Add(sim.Checksum()); hp.Add(p2.Health); }
        Assert.Equal(snapSpikes + 1, sim.Chunks.Spikes.Count);
        Assert.True(p2.Health < p2.MaxHealth);

        sim.Restore(snap);
        Assert.Equal(snapSum, sim.Checksum());
        Assert.Equal(snapSpikes, sim.Chunks.Spikes.Count);   // the later placement rolled back
        Assert.False(sim.Player.SpikesSelected);

        for (int f = 0; f < 90; f++)
        {
            sim.Step(P0(f), default);
            Assert.Equal(sums[f], sim.Checksum());
            Assert.Equal(hp[f], p2.Health);
        }
    }

    [Fact]
    public void Checksum_SeesSpikeOverlay()
    {
        var sim = new Simulation(Arena(), StandAt(40f));
        Run(sim, 5);
        ulong a = sim.Checksum();
        sim.Chunks.Spikes.Set(10, FloorRow, P1Side);
        ulong b = sim.Checksum();
        Assert.NotEqual(a, b);
        sim.Chunks.Spikes.Clear(10, FloorRow);
        sim.Chunks.Spikes.Set(10, FloorRow, P2Side);
        Assert.NotEqual(b, sim.Checksum());   // side is part of it, not just presence
    }
}
