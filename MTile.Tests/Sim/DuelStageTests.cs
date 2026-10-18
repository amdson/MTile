using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// The pure-PvP rooms (Stages/DuelStages.cs): two players and nothing else, both standing
// on clear floor apart from each other, and a killed second player comes back at its own
// start (Stage.Versus) rather than staying dead.
public class DuelStageTests
{
    private static Simulation NewSim(string name)
        => new(new GameConfig { SpawnSecondPlayer = true }, Stages.Get(name));

    [Theory, InlineData("duel_hall"), InlineData("duel_plain")]
    public void HasTwoPlayers_AndNoEntities(string name)
    {
        var g = NewSim(name);
        for (int i = 0; i < 120; i++) g.Step(default, default);
        Assert.Equal(name, Stages.Get(name).Name);
        Assert.Empty(g.Entities.Where(e => !e.IsDead));
        Assert.Single(g.SecondaryPlayers);
        var p0 = g.Player.Body.Position; var p1 = g.SecondaryPlayers[0].Player.Body.Position;
        Assert.True(g.Player.IsAlive && g.SecondaryPlayers[0].Player.IsAlive);
        Assert.True(System.MathF.Abs(p1.X - p0.X) > 40 * Chunk.TileSize, "players should start well apart");
        Assert.True(System.MathF.Abs(p1.Y - p0.Y) < 4f, "both should be standing on the same flat floor");
        // Standing, not falling through or stuck in rock.
        Assert.True(System.MathF.Abs(g.Player.Body.Velocity.Y) < 5f);
    }

    // Block selection is per-player state: both start on the configured material, and
    // P2's number key moves only P2's picker.
    [Fact]
    public void BlockPicker_IsPerPlayer()
    {
        var g = new Simulation(new GameConfig { SpawnSecondPlayer = true, StartingBlockType = "Sand" },
                               Stages.Get("duel_plain"));
        var p1 = g.SecondaryPlayers[0].Player;
        Assert.Equal(TileType.Sand, g.Player.ActiveBlockType);
        Assert.Equal(TileType.Sand, p1.ActiveBlockType);

        g.Step(default, new PlayerInput { Num1 = true });
        g.Step(default, new PlayerInput { Num5 = true });
        Assert.Equal(TileType.Stone, p1.ActiveBlockType);
        Assert.True(p1.SpikesSelected);
        Assert.Equal(TileType.Sand, g.Player.ActiveBlockType);
        Assert.False(g.Player.SpikesSelected);
    }

    [Fact]
    public void HallHasARoof_PlainIsOpen()
    {
        var hall = NewSim("duel_hall"); var plain = NewSim("duel_plain");
        bool RoofAbove(Simulation g)
        {
            var p = g.Player.Body.Position;
            int tx = (int)System.MathF.Floor(p.X / Chunk.TileSize), ty = (int)System.MathF.Floor(p.Y / Chunk.TileSize);
            for (int y = ty; y >= 0; y--) if (g.Chunks.GetCellState(tx, y) == TileState.Solid) return true;
            return false;
        }
        Assert.True(RoofAbove(hall));
        Assert.False(RoofAbove(plain));
    }

    [Theory, InlineData("duel_hall"), InlineData("duel_plain")]
    public void KilledSecondPlayer_RespawnsAtItsStart(string name)
    {
        var g = NewSim(name);
        Assert.True(g.Versus);
        for (int i = 0; i < 30; i++) g.Step(default, default);
        var start = g.SecondaryPlayers[0].Player.Body.Position;
        var p1 = g.SecondaryPlayers[0].Player;
        p1.Body.Position = g.Player.Body.Position + new Vector2(30f, 0f);
        p1.Health = -1f;
        for (int i = 0; i < 30; i++) g.Step(default, default);
        Assert.True(p1.IsAlive);
        Assert.True(Vector2.Distance(p1.Body.Position, start) < 3 * Chunk.TileSize);
    }
}
