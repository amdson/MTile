using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behaviour + determinism gates for Entities/Enemies/Types/AspidEnemy.cs.
//
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~AspidEnemyTests"
public class AspidEnemyTests(ITestOutputHelper output)
{
    // One solid floor row at tile y 0 (world y 0), 80 tiles wide, open above.
    private static ChunkMap Floor() =>
        SimTerrain.FromAscii(new string('X', 80), originTileX: -40, originTileY: 0);

    private static readonly Vector2 PlayerSpawn = new(0f, -30f);
    private static readonly PlayerInput Idle = default;

    [Fact]
    public void Aspid_HoldsItsStandOffAboveThePlayer()
    {
        var sim = new Simulation(Floor(), PlayerSpawn,
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Aspid, new Vector2(300f, -40f))));
        var aspid = sim.Entities.First(e => e.Kind == EntityKind.Aspid);

        for (int f = 0; f < 60 * 8; f++) sim.Step(Idle);

        var   to   = aspid.Body.Position - sim.Player.Body.Position;
        float dist = to.Length();
        output.WriteLine($"aspid offset ({to.X:F0},{to.Y:F0}) dist {dist:F0}");

        Assert.False(aspid.IsDead);
        Assert.InRange(dist, 75f, 150f);                // around PreferredRange (110)
        Assert.True(to.Y < -30f, "aspid should hover above the player");
    }

    [Fact]
    public void Aspid_FiresFansOfThreeAndHitsAStandingPlayer()
    {
        var sim = new Simulation(Floor(), PlayerSpawn,
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Aspid, new Vector2(90f, -110f))));

        int maxBalls = 0;
        for (int f = 0; f < 60 * 6; f++)
        {
            sim.Step(Idle);
            maxBalls = Math.Max(maxBalls, sim.Entities.Count(e => e.Kind == EntityKind.AspidFireball));
        }
        output.WriteLine($"max fireballs alive {maxBalls}, player damage {sim.Player.Combat.DamageTaken:F2}");

        Assert.True(maxBalls >= 3, $"expected a 3-fireball volley, saw at most {maxBalls} alive");
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "a standing player should eat a fireball");
    }

    [Fact]
    public void Aspid_SpreadsOutFromOtherEnemies()
    {
        // Three spawned almost on top of each other, all wanting the same hover point.
        var sim = new Simulation(Floor(), PlayerSpawn, g =>
        {
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Aspid, new Vector2(100f, -100f)));
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Aspid, new Vector2(104f, -100f)));
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Aspid, new Vector2(100f, -104f)));
        });

        for (int f = 0; f < 60 * 5; f++) sim.Step(Idle);

        var ps = sim.Entities.Where(e => e.Kind == EntityKind.Aspid && !e.IsDead)
                             .Select(e => e.Body.Position).ToList();
        Assert.Equal(3, ps.Count);
        float minGap = float.MaxValue;
        for (int i = 0; i < ps.Count; i++)
            for (int j = i + 1; j < ps.Count; j++)
                minGap = MathF.Min(minGap, Vector2.Distance(ps[i], ps[j]));
        output.WriteLine($"min pairwise gap {minGap:F1}px");

        Assert.True(minGap > 30f, $"aspids bunched up (min gap {minGap:F1}px)");
    }

    [Fact]
    public void Aspid_SurvivesASnapshotRoundTrip()
    {
        // K lands mid-volley so fireballs and a tracking windup are in flight.
        const int K = 100, N = 300;

        Simulation Build() => new(Floor(), PlayerSpawn, g =>
        {
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Aspid, new Vector2( 90f, -110f)));
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Aspid, new Vector2(-80f,  -90f)));
        });

        PlayerInput At(int f) => new() { Right = f % 50 < 20, Left = f % 50 >= 30 && f % 50 < 45, Space = f % 40 < 3 };

        var live = Build();
        for (int f = 0; f < K; f++) live.Step(At(f));
        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); liveTrace.Add(Probe(live)); }

        live.Restore(snap);
        for (int f = K; f < N; f++)
        {
            live.Step(At(f));
            var replay = Probe(live);
            if (replay != liveTrace[f - K])
                output.WriteLine($"Divergence at frame {f}:\nLIVE:\n{liveTrace[f - K]}\nREPLAY:\n{replay}");
            Assert.Equal(liveTrace[f - K], replay);
        }
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)}|hp{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}\n");
        return sb.ToString();
    }

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
}
