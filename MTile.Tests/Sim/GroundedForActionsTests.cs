using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// A vault lifts the body clear of the floor for a few frames, so the raw ground probe
// misses — but it is footwork, and an attack out of it must be the GROUND variant
// (MovementState.GroundedForActions). A slash mid-vault used to come out as AirSlash1.
public class GroundedForActionsTests(ITestOutputHelper output)
{
    [Fact]
    public void SlashDuringVault_IsAGroundSlash()
    {
        // The one-block vault fixture from SimulationTests.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOO
            OOOOOOOOXXXXXXXXOOOO
            XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        var sim = new Simulation(terrain, new Vector2(12f, 3 * Chunk.TileSize - PlayerCharacter.Radius));
        var p = sim.Player;

        PlayerInput Run(bool click = false) => new()
        {
            Right = true, LeftClick = click,
            MouseWorldPosition = p.Body.Position + new Vector2(60f, 0f),
        };

        // Run in until the vault has the body off the probe.
        int f = 0;
        while (!(p.CurrentState is ParkourState && !p.IsGrounded) && f++ < 240) sim.Step(Run());
        Assert.True(p.CurrentState is ParkourState, $"never vaulted (state {p.CurrentStateName})");
        Assert.False(p.IsGrounded);

        // Tap LMB: the click intent is issued on release.
        sim.Step(Run(click: true));
        string action = p.CurrentActionName;
        for (int i = 0; i < 6 && action is "NullAction" or "ReadyAction"; i++)
        {
            sim.Step(Run());
            action = p.CurrentActionName;
        }
        output.WriteLine($"state {p.CurrentStateName}, action {action}");
        Assert.Equal(nameof(GroundSlash1), action);
    }
}
