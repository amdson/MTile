using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// wall_dummy: the stage builds, P2 exists as an AFK dummy beside the wall, and with no
// input it stays at its post.
public class WallDummyStageTests
{
    [Fact]
    public void Dummy_StandsAfk_BesideTheWall()
    {
        var stage = Stages.Get("wall_dummy");
        Assert.Equal("wall_dummy", stage.Name);
        Assert.True(stage.IdleSecondary);

        var sim = new Simulation(new GameConfig(), stage);
        Assert.Single(sim.SecondaryPlayers);
        var dummy = sim.SecondaryPlayers[0].Player;
        var start = dummy.Body.Position;
        for (int f = 0; f < 120; f++) sim.Step(default);

        Assert.True(dummy.IsGrounded, $"dummy should be standing, is {dummy.CurrentStateName}");
        Assert.True(Vector2.Distance(start, dummy.Body.Position) < 8f, "an AFK dummy should not wander");
        float wallFace = 60 * Chunk.TileSize;
        Assert.InRange(wallFace - dummy.Body.Position.X, 10f, 50f);
    }
}
