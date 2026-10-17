using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// SpearmanEnemy (Entities/Enemies/Types/SpearmanEnemy.cs): skewers a player who runs
// or jumps straight at it, throws them back, guards its arc while the spear is in
// hand, is open while it regrows, can't reach (or guard) steeply below, and stabs
// projectiles out of the air at the cost of its spear.
public class SpearmanEnemyTests(ITestOutputHelper output)
{
    private const float FloorTopY = 12 * Chunk.TileSize;   // floor surface, tile row 12

    // 48 tiles wide, rows 0..15, floor at row 12; `raised` lifts columns by h tiles.
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
    private static float StandY(int h) => Top(h) - SpearmanEnemy.HalfHeight - 1f;

    private static (Simulation sim, SpearmanEnemy s) Flat(float playerX, float spearX)
    {
        var sim = new Simulation(Course(), new Vector2(playerX, FloorTopY - 30f),
            g => g.SpawnEntity(new SpearmanEnemy(new Vector2(spearX, StandY(0)))));
        return (sim, (SpearmanEnemy)sim.Entities.Single(e => e.Kind == EntityKind.Spearman));
    }

    // Drive the player with `input` until the first hit lands; report where it was
    // when it did and how far it ended up thrown back.
    private float SpearX;   // the spearman's x on the frame the hit landed

    private (float hitX, float minX, float dmg) Approach(Simulation sim, System.Func<int, PlayerInput> input)
    {
        int f = 0;
        var sp = (SpearmanEnemy)sim.Entities.Single(e => e.Kind == EntityKind.Spearman);
        for (; f < 600 && sim.Player.Combat.DamageTaken == 0f; f++)
        {
            sim.Step(input(f));
        }
        SpearX = sp.Body.Position.X;
        float hitX = sim.Player.Body.Position.X;
        float minX = hitX;
        for (int k = 0; k < 60; k++)
        {
            sim.Step(default); minX = System.MathF.Min(minX, sim.Player.Body.Position.X);
        }
        output.WriteLine($"hit on frame {f} at x {hitX:F0}, thrown back to {minX:F0}, dmg {sim.Player.Combat.DamageTaken:F2}");
        return (hitX, minX, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void Spearman_SkewersAPlayerWhoRunsAtIt_AndThrowsThemBack()
    {
        var (sim, s) = Flat(8 * Chunk.TileSize, 30 * Chunk.TileSize);
        var (hitX, minX, dmg) = Approach(sim, _ => new PlayerInput { Right = true });
        Assert.True(dmg >= 1f, "Running straight at the spearman should get stabbed.");
        Assert.True(hitX < SpearX, "Player got past the spear before it landed.");
        Assert.True(hitX - minX > Spear.Reach, "The stab should throw the player back out past its reach.");
    }

    [Fact]
    public void Spearman_SkewersAPlayerWhoJumpsAtIt()
    {
        var (sim, s) = Flat(8 * Chunk.TileSize, 30 * Chunk.TileSize);
        // Walk in, then jump the last stretch (jump whenever within ~110 px).
        var (hitX, minX, dmg) = Approach(sim, f => new PlayerInput
        {
            Right = true,
            Space = s.Body.Position.X - sim.Player.Body.Position.X < 110f,
        });
        Assert.True(dmg >= 1f, "Jumping at the spearman should get stabbed.");
        Assert.True(hitX < SpearX + 4f, "Player cleared the spearman before the stab landed.");
        Assert.True(hitX - minX > Spear.Reach, "The stab should throw a jumper back out past its reach.");
    }

    [Fact]
    public void Spearman_GuardsItsArc_UntilTheSpearIsSpent()
    {
        var (sim, s) = Flat(10 * Chunk.TileSize, 30 * Chunk.TileSize);
        sim.Step(default);
        Assert.True(s.SpearReady);

        var front = s.Body.Position + new Vector2(-20f, 0f);   // player side, level
        Hitbox Slash(Vector2 from) => new(s.Body.Bounds, hitId: 999_999, damage: 0.5f,
            knockbackImpulse: new Vector2(300f, 0f), Faction.Player1, sim.Player.Id, origin: from);

        float hp = s.Health;
        Assert.Equal(new Vector2(300f, 0f), s.OnHit(Slash(front), default));
        Assert.Equal(hp, s.Health);
        // Overhead is guarded too.
        s.OnHit(Slash(s.Body.Position + new Vector2(-5f, -30f)), default);
        Assert.Equal(hp, s.Health);

        s.SpendSpear();
        Assert.False(s.SpearReady);
        s.OnHit(Slash(front), default);
        Assert.Equal(hp - 0.5f, s.Health, 3);
    }

    [Fact]
    public void Spearman_CannotReachOrGuardSteeplyBelow()
    {
        // Spearman near the lip of a 3-block ledge (x 20..47); player on the floor tucked
        // under that lip.
        var terrain = Course((20, 47, 3));
        var sim = new Simulation(terrain, new Vector2(19 * Chunk.TileSize, FloorTopY - 30f),
            g => g.SpawnEntity(new SpearmanEnemy(new Vector2(21 * Chunk.TileSize, StandY(3)))));
        var s = (SpearmanEnemy)sim.Entities.Single(e => e.Kind == EntityKind.Spearman);

        for (int f = 0; f < 300; f++) sim.Step(default);
        output.WriteLine($"spearman ({s.Body.Position.X:F0},{s.Body.Position.Y:F0}) player ({sim.Player.Body.Position.X:F0},{sim.Player.Body.Position.Y:F0})");
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
        Assert.True(s.Body.Position.Y < Top(3), "Spearman walked off its ledge.");

        float hp = s.Health;
        s.OnHit(new Hitbox(s.Body.Bounds, 999_999, 0.5f, new Vector2(0f, -200f), Faction.Player1,
                           sim.Player.Id, origin: sim.Player.Body.Position), default);
        Assert.True(s.Health < hp, "A hit from below should land on a spearman holding its spear.");
    }

    [Fact]
    public void Spearman_StabsAProjectileOutOfTheAir_AndIsLeftOpen()
    {
        // Player far off to the left, out of the stab's reach; a clod comes in from the right.
        var (sim, s) = Flat(4 * Chunk.TileSize, 30 * Chunk.TileSize);
        sim.Step(default);
        var ball = new MassBall(s.Body.Position + new Vector2(150f, -6f), new Vector2(-320f, 0f), 400f,
                                TileType.Dirt, Faction.Player1);
        sim.SpawnEntity(ball);

        float hp = s.Health;
        bool parried = false;
        for (int f = 0; f < 60 && !ball.IsDead; f++)
        {
            sim.Step(default);
            parried |= s.CurrentActionName == nameof(SpearParryAction);
        }
        output.WriteLine($"ball dead {ball.IsDead} at ({ball.Body.Position.X:F0},{ball.Body.Position.Y:F0}), regrow {s.SpearRegrow:F2}");
        Assert.True(parried, "Spearman never parried the incoming projectile.");
        Assert.True(ball.IsDead, "The projectile survived the parry.");
        Assert.True(ball.Body.Position.X > s.Body.Position.X + 8f, "The projectile reached the body before the parry.");
        Assert.Equal(hp, s.Health);
        Assert.False(s.SpearReady, "A parry should spend the spear.");
    }

    [Fact]
    public void Spearman_SurvivesASnapshotRoundTrip()
    {
        const int K = 90, N = 360;
        Simulation Build() => new(Course((30, 34, 1)), new Vector2(10 * Chunk.TileSize, FloorTopY - 30f),
            g => g.SpawnEntity(new SpearmanEnemy(new Vector2(26 * Chunk.TileSize, StandY(0)))));

        PlayerInput At(int f) => new() { Right = f % 70 < 30, Left = f % 70 >= 50, Space = f % 45 < 3 };

        var live = Build();
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
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)}|dmg{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
        {
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}");
            if (e is SpearmanEnemy s) sb.Append($"|rg{Bits(s.SpearRegrow)}|{s.CurrentActionName}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);
}
