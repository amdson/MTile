using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behaviour + determinism gates for the Warlord boss (Entities/Enemies/Types/WarlordEnemy.cs),
// in the shape of TemplateEnemyTests. Each real behaviour the boss has — closing in and
// swinging, leaping across a gap, caging the player in foam that then decays — gets one
// headless test, and the whole thing gets a snapshot round-trip.
//
// Run just these:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~WarlordEnemyTests"
public class WarlordEnemyTests(ITestOutputHelper output)
{
    // Flat stone floor, solid at tile y = 3, 70 tiles wide (world x -44 .. 726).
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float FloorTopY = 3 * Chunk.TileSize;
    private static readonly Vector2 PlayerAt = new(60f, FloorTopY - 12f);
    private static Vector2 BossAt(float x) => new(x, FloorTopY - WarlordEnemy.Radius - 1f);

    private static readonly PlayerInput Idle = default;

    [Fact]
    public void Warlord_ClosesTheDistanceAndLandsAHit()
    {
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new WarlordEnemy(BossAt(300f))));
        var boss = First(sim);
        float startX = boss.Body.Position.X;

        var seen = new HashSet<string>();
        for (int f = 0; f < 600; f++)
        {
            sim.Step(Idle);
            if (boss.CurrentActionName is { Length: > 0 } a) seen.Add(a);
        }

        Assert.True(startX - boss.Body.Position.X > 120f,
            $"Warlord didn't close the distance (Δx {startX - boss.Body.Position.X:F1}px).");
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "Warlord never landed a hit.");
        output.WriteLine($"Closed {startX - boss.Body.Position.X:F0}px; player lost {sim.Player.Combat.DamageTaken:F2} HP; " +
                         $"actions seen: {string.Join(", ", seen)}.");
    }

    [Fact]
    public void Warlord_LeapsAcrossAGapAndSlams()
    {
        // 320 px away: past LeapMin, outside the ram band, so the opener has to be a leap.
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new WarlordEnemy(BossAt(380f))));
        var boss = First(sim);

        bool leapt = false, airborne = false;
        float minY = boss.Body.Position.Y;
        int landedFrame = -1;
        for (int f = 0; f < 240; f++)
        {
            sim.Step(Idle);
            if (boss.CurrentActionName == nameof(WarlordLeapAction)) leapt = true;
            minY = System.MathF.Min(minY, boss.Body.Position.Y);
            if (boss.Body.Position.Y < FloorTopY - WarlordEnemy.Radius - 20f) airborne = true;
            if (leapt && airborne && landedFrame < 0 && boss.Body.Position.Y > FloorTopY - WarlordEnemy.Radius - 3f)
                landedFrame = f;
        }

        Assert.True(leapt, "Warlord never chose its leap against a far target.");
        Assert.True(airborne, $"Warlord never left the ground (min y {minY:F1}).");
        Assert.True(landedFrame > 0, "Warlord never came back down.");
        float dx = System.MathF.Abs(boss.Body.Position.X - sim.Player.Body.Position.X);
        Assert.True(dx < 140f, $"Warlord landed {dx:F0}px from the player — the arc missed.");
        output.WriteLine($"Leapt, apex {FloorTopY - minY:F0}px up, landed frame {landedFrame}, {dx:F0}px from the player.");
    }

    [Fact]
    public void Warlord_CagesThePlayerInFoamThatDecays()
    {
        // Prime the brain so its first opening is a cage: Scratch.I1 is "big moves since
        // the last cage" and the controller cages at CageEvery (3). 150 px apart sits
        // inside the cage band (50..230) and outside the cleave.
        var sim  = new Simulation(Floor(), PlayerAt, g =>
        {
            var w = new WarlordEnemy(BossAt(PlayerAt.X + 150f));
            w.Scratch.I1 = 3;
            g.SpawnEntity(w);
        });
        var boss   = First(sim);
        var chunks = sim.Chunks;

        int playerCol = (int)System.MathF.Floor(PlayerAt.X / Chunk.TileSize);
        int floorRow  = 3;
        bool Wall(int side) => chunks.GetCellState(playerCol + side * 4, floorRow - 1) == TileState.Solid
                            && chunks.GetCellType (playerCol + side * 4, floorRow - 1) == TileType.Foam;

        int cagedFrame = -1;
        for (int f = 0; f < 240 && cagedFrame < 0; f++)
        {
            sim.Step(Idle);
            if (Wall(-1) && Wall(+1)) cagedFrame = f;
        }
        Assert.True(cagedFrame > 0, "No foam cage appeared on both sides of the player.");
        Assert.Equal(nameof(WarlordCageAction), boss.CurrentActionName);

        // The walls are temporary: foam decay (or the boss's own slam) clears them.
        int gone = -1;
        for (int f = 0; f < 420 && gone < 0; f++)
        {
            sim.Step(Idle);
            bool any = false;
            for (int side = -1; side <= 1; side += 2)
            for (int k = 4; k <= 5; k++)
            for (int h = 1; h <= 6; h++)
                if (chunks.GetCellState(playerCol + side * k, floorRow - h) != TileState.Empty) any = true;
            if (!any) gone = f;
        }
        Assert.True(gone >= 0, "The cage never cleared.");
        output.WriteLine($"Caged at frame {cagedFrame}; cleared {gone} frames later.");
    }

    [Fact]
    public void Warlord_RestsAtHalfHealthThenEnrages()
    {
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new WarlordEnemy(BossAt(PlayerAt.X + 120f))));
        var boss = First(sim);

        // Knock it to the threshold before its first decision, so the rest window is
        // not shared with an attack already in flight (which is allowed to finish).
        boss.Health = WarlordEnemy.MaxHp * 0.5f;
        sim.Step(Idle);
        Assert.True(boss.Exhausted, "Crossing half health should open the exhausted window.");
        Assert.True(boss.Enraged,   "Crossing half health should flip the phase bit.");
        Assert.Equal("", boss.CurrentActionName);

        // While exhausted it starts nothing and goes nowhere.
        float x0 = boss.Body.Position.X;
        int restFrames = 0;
        while (boss.Exhausted && restFrames < 600) { sim.Step(Idle); restFrames++; }
        Assert.InRange(restFrames, 60, 120);                       // ≈ RestSeconds (1.5 s)
        Assert.True(System.MathF.Abs(boss.Body.Position.X - x0) < 4f, "Warlord moved while exhausted.");

        // Then it fights again, enraged.
        bool acted = false;
        for (int f = 0; f < 300 && !acted; f++) { sim.Step(Idle); acted = boss.CurrentActionName.Length > 0; }
        Assert.True(acted, "Warlord never resumed attacking after its rest.");
        Assert.True(boss.Enraged);
        output.WriteLine($"Rested {restFrames} frames, then resumed with {boss.CurrentActionName}.");
    }

    [Fact]
    public void Warlord_SurvivesASnapshotRoundTrip()
    {
        const int K = 90, N = 420;

        Simulation Build() => new(Floor(), PlayerAt, g => g.SpawnEntity(new WarlordEnemy(BossAt(260f))));

        // Restless input so the pair stay in contact and every attack gets a chance.
        PlayerInput At(int f) => new() { Right = f % 50 < 20, Left = f % 50 >= 35, Space = f % 45 < 3 };

        var live = Build();
        for (int f = 0; f < K; f++) live.Step(At(f));
        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); liveTrace.Add(Probe(live)); }

        live.Restore(snap);
        var replayTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); replayTrace.Add(Probe(live)); }

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
        output.WriteLine($"Round-trip identical across {liveTrace.Count} frames.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static WarlordEnemy First(Simulation sim)
    {
        foreach (var e in sim.Entities) if (e is WarlordEnemy w) return w;
        Assert.Fail("No Warlord in the simulation.");
        return null;
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)};")
          .Append($"{Bits(p.Body.Velocity.X)},{Bits(p.Body.Velocity.Y)}|")
          .Append($"{p.CurrentStateName}/{p.CurrentActionName}|dmg{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
        {
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}");
            if (e is WarlordEnemy w)
                sb.Append($"|{w.CurrentActionName}|s{w.Scratch.I0},{w.Scratch.I1},{Bits(w.Scratch.F0)},{Bits(w.Scratch.F2)},{Bits(w.Scratch.F3)}");
            sb.Append('\n');
        }
        sb.Append($"T|{sim.Chunks.TerrainHash}\n");
        return sb.ToString();
    }

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);
}
