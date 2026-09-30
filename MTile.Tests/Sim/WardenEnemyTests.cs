using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// WardenEnemy (Entities/Enemies/Types/WardenEnemy.cs): hops steps up to two blocks,
// is stopped by three, lands its smash, and is armored except while swinging.
public class WardenEnemyTests(ITestOutputHelper output)
{
    private const float FloorTopY = 12 * Chunk.TileSize;   // floor surface, tile row 12

    // A strip 48 tiles wide whose ground height (in tiles above the floor) is given
    // per column; rows 0..15, floor at row 12.
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

    private static float Top(int h) => FloorTopY - h * Chunk.TileSize;

    [Fact]
    public void Warden_HopsOneAndTwoBlockSteps_AndLandsItsSmash()
    {
        // Warden on the floor at x≈40; the ground rises 1 block at x 25..32 and 3 blocks
        // (a further 2) at x 0..24, where the player stands.
        var terrain = Course((25, 32, 1), (0, 24, 3));
        var sim = new Simulation(terrain, new Vector2(12 * Chunk.TileSize, Top(3) - 30f),
            g => g.SpawnEntity(new WardenEnemy(new Vector2(40 * Chunk.TileSize, FloorTopY - 16f))));
        var w = (WardenEnemy)sim.Entities.Single(e => e.Kind == EntityKind.Warden);

        float minY = w.Body.Position.Y;
        for (int f = 0; f < 1200 && sim.Player.Combat.DamageTaken == 0f; f++)
        {
            sim.Step(default);
            minY = System.MathF.Min(minY, w.Body.Position.Y);
        }

        output.WriteLine($"warden at ({w.Body.Position.X:F0},{w.Body.Position.Y:F1}), rose {FloorTopY - 16f - minY:F1}px, " +
                         $"player dmg {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(w.Body.Position.Y < Top(3) - 10f, "Warden never got up onto the 3-block level.");
        Assert.True(sim.Player.Combat.DamageTaken >= 2f, "Warden never landed its smash.");
    }

    [Fact]
    public void Warden_CannotHopAThreeBlockWall()
    {
        // Player on the left floor, a 3-block wall at x 20..21, Warden on the right.
        var terrain = Course((20, 21, 3));
        var sim = new Simulation(terrain, new Vector2(8 * Chunk.TileSize, FloorTopY - 30f),
            g => g.SpawnEntity(new WardenEnemy(new Vector2(34 * Chunk.TileSize, FloorTopY - 16f))));
        var w = sim.Entities.Single(e => e.Kind == EntityKind.Warden);

        for (int f = 0; f < 900; f++) sim.Step(default);

        output.WriteLine($"warden x {w.Body.Position.X:F0} (wall right face {22 * Chunk.TileSize})");
        Assert.True(w.Body.Position.X > 22 * Chunk.TileSize, "Warden got past a 3-block wall.");
        Assert.True(w.Body.Position.X < 26 * Chunk.TileSize, "Warden never walked up to the wall.");
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void Warden_IsArmored_UntilItSwings()
    {
        var terrain = Course();
        // Player 30 px away: inside the smash trigger, so the windup starts at once.
        var sim = new Simulation(terrain, new Vector2(20 * Chunk.TileSize, FloorTopY - 30f),
            g => g.SpawnEntity(new WardenEnemy(new Vector2(20 * Chunk.TileSize + 60f, FloorTopY - 16f))));
        var w = (WardenEnemy)sim.Entities.Single(e => e.Kind == EntityKind.Warden);

        Hitbox Slash() => new(w.Body.Bounds, hitId: 999_999, damage: 0.5f,
            knockbackImpulse: new Vector2(300f, 0f), Faction.Player1, sim.Player.Id);

        // Armored while walking in: the hit is echoed back, nothing lands.
        sim.Step(default);
        Assert.False(w.IsOpen);
        var vBefore = w.Body.Velocity;
        var echoed  = w.OnHit(Slash(), default);
        Assert.Equal(new Vector2(300f, 0f), echoed);
        Assert.Equal(0.5f, w.Health);
        Assert.Equal(vBefore, w.Body.Velocity);   // no knockback

        // Once it commits to the smash it is open, and one slash kills it.
        int f = 0;
        for (; f < 300 && !w.IsOpen; f++) sim.Step(default);
        output.WriteLine($"opened after {f} frames");
        Assert.True(w.IsOpen, "Warden never started its smash.");
        w.OnHit(Slash(), default);
        Assert.True(w.IsDead, "An open Warden should die to one slash.");
    }

    [Fact]
    public void Warden_SurvivesASnapshotRoundTrip()
    {
        const int K = 90, N = 300;
        Simulation Build() => new(Course((25, 32, 1)), new Vector2(18 * Chunk.TileSize, FloorTopY - 30f),
            g => g.SpawnEntity(new WardenEnemy(new Vector2(36 * Chunk.TileSize, FloorTopY - 16f))));

        PlayerInput At(int f) => new() { Right = f % 50 < 10, Space = f % 45 < 3 };

        var live = Build();
        for (int f = 0; f < K; f++) live.Step(At(f));
        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); liveTrace.Add(Probe(live)); }
        live.Restore(snap);
        for (int f = K, i = 0; f < N; f++, i++)
        {
            live.Step(At(f));
            Assert.Equal(liveTrace[i], Probe(live));
        }
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)}|pct{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}|{e.Color.PackedValue}\n");
        return sb.ToString();
    }

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);
}
