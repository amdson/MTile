using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// WizardEnemy (Entities/Enemies/Types/WizardEnemy.cs): fires waves of orbs that pass
// through terrain, raises a pillar when the player closes in, and survives rollback.
public class WizardEnemyTests(ITestOutputHelper output)
{
    private const float FloorTopY = 12 * Chunk.TileSize;

    // A strip 48 tiles wide, floor at row 12, with optional full-height-to-row-(12-h)
    // raised columns.
    private static ChunkMap Course(params (int fromX, int toX, int h)[] raised)
    {
        var rows = new List<string>();
        for (int y = 0; y < 16; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < 48; x++)
            {
                int h = 0;
                foreach (var r in raised) if (x >= r.fromX && x <= r.toX) h = r.h;
                sb.Append(y >= 12 - h ? 'X' : 'O');
            }
            rows.Add(sb.ToString());
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private static Simulation Build(ChunkMap terrain, float playerX, float wizardX)
        => new(terrain, new Vector2(playerX, FloorTopY - 30f),
               g => g.SpawnEntity(new WizardEnemy(new Vector2(wizardX, FloorTopY - 10f))));

    [Fact]
    public void Wizard_WavesDriftThroughAWall_AndHitThePlayer()
    {
        // A 4-block wall between them: the orbs must pass through it.
        var sim = Build(Course((20, 21, 4)), playerX: 12 * Chunk.TileSize, wizardX: 30 * Chunk.TileSize);

        var seen = new HashSet<EntityId>();
        for (int f = 0; f < 900 && sim.Player.Combat.DamageTaken == 0f; f++)
        {
            sim.Step(default);
            foreach (var e in sim.Entities) if (e.Kind == EntityKind.WizardOrb) seen.Add(e.Id);
        }
        output.WriteLine($"orbs spawned {seen.Count}, player dmg {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(seen.Count >= 9, $"Expected three waves of three orbs, saw {seen.Count}.");
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "No orb reached the player through the wall.");
    }

    [Fact]
    public void Wizard_RaisesAPillar_WhenThePlayerClosesIn()
    {
        // Player 70 px to its left — inside the pillar trigger, outside the wave MinRange.
        const float wizardX = 30 * Chunk.TileSize + 5f;
        var sim = Build(Course(), playerX: wizardX - 70f, wizardX: wizardX);
        var w = sim.Entities.Single(e => e.Kind == EntityKind.Wizard);

        int solidAbove = 0;
        for (int f = 0; f < 120; f++)
        {
            sim.Step(default);
            // The pillar stands 3 columns toward the player, but the wizard backs away
            // while it rises — scan the few columns it could have cast into.
            int col = (int)System.MathF.Floor(w.Body.Position.X / Chunk.TileSize) - 3;
            int n = 0;
            for (int c = col - 3; c <= col + 1; c++)
            {
                int m = 0;
                for (int k = 1; k <= 3; k++)
                    if (sim.Chunks.GetCellState(c, 12 - k) == TileState.Solid) m++;
                n = System.Math.Max(n, m);
            }
            solidAbove = System.Math.Max(solidAbove, n);
        }
        output.WriteLine($"pillar height {solidAbove}, last action '{((EnemyEntity)w).CurrentActionName}'");
        Assert.Equal(3, solidAbove);
    }

    [Fact]
    public void Wizard_SurvivesASnapshotRoundTrip()
    {
        // Snapshot mid-volley with a pillar sprouting: orbs, sprouts and the FSM all
        // have to come back exactly.
        const int K = 100, N = 320;
        Simulation Make() => Build(Course(), playerX: 14 * Chunk.TileSize, wizardX: 26 * Chunk.TileSize);
        PlayerInput At(int f) => new() { Right = f % 60 < 20, Space = f % 50 < 3 };

        var live = Make();
        for (int f = 0; f < K; f++) live.Step(At(f));
        var snap = live.Snapshot();

        var trace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); trace.Add(Probe(live)); }
        live.Restore(snap);
        for (int f = K, i = 0; f < N; f++, i++)
        {
            live.Step(At(f));
            Assert.Equal(trace[i], Probe(live));
        }
        output.WriteLine($"identical across {trace.Count} frames; final entity count {live.Entities.Count}");
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)}|pct{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}\n");
        return sb.ToString();
    }

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);
}
