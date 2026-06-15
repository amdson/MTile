using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The training stage's dummy (Stages "training"): a secondary PlayerCharacter
// driven by a stage ticker that periodically slashes/stabs without moving, and
// auto-resets to its home spot on death or displacement. Terrain here is a
// hand-built plateau (the real stage loads Levels/training.json via TitleContent,
// which isn't available headless); the Populate delegate is the real one.
public class TrainingStageTests(ITestOutputHelper output)
{
    private static readonly Vector2 DummyHome = new(8f, 75f);

    private static Simulation BuildSim()
    {
        // Plateau floor at tile y = 6 (world y = 96), tiles x ∈ [-8, 7] — covers
        // the dummy home (tile 0) and the player spawn (-120 → tile -7.5).
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXX", originTileX: -8, originTileY: 0);
        return new Simulation(terrain, new Vector2(-120f, 60f),
                              Stages.Get("training").Populate);
    }

    [Fact]
    public void Dummy_SlashesAndStabs_WithoutWandering()
    {
        var sim = BuildSim();
        var dummy = sim.SecondaryPlayers[0].Player;

        var actions = new HashSet<string>();
        float maxDriftSeen = 0f;
        // 4 attack cycles (150 frames each at 60 fps) → 2 slash turns + 2 stab turns.
        for (int f = 0; f < 620; f++)
        {
            sim.Step(default);
            actions.Add(dummy.CurrentActionName);
            maxDriftSeen = System.MathF.Max(maxDriftSeen,
                Vector2.Distance(dummy.Body.Position, DummyHome));
        }
        output.WriteLine($"actions seen: {string.Join(",", actions)}; max drift {maxDriftSeen:F1}");

        Assert.Contains("GroundSlash1", actions);
        Assert.Contains("StabAction",   actions);
        // "Without moving": un-attacked, the dummy stays parked near home
        // (settle drop + facing-flip nudges only).
        Assert.True(maxDriftSeen < 40f,
            $"Dummy wandered {maxDriftSeen:F1} px from home without being hit.");
    }

    [Fact]
    public void Dummy_RespawnsOnDeath_AndOnDisplacement()
    {
        var sim = BuildSim();
        var dummy = sim.SecondaryPlayers[0].Player;
        for (int f = 0; f < 30; f++) sim.Step(default);   // settle on the floor

        // Death → reset with full health at home.
        dummy.Health = 0f;
        sim.Step(default);
        Assert.Equal(dummy.MaxHealth, dummy.Health);
        Assert.True(Vector2.Distance(dummy.Body.Position, DummyHome) < 20f,
            $"Dummy not back home after death-reset (at {dummy.Body.Position}).");

        // Knocked far away (e.g. off the plateau) → reset home.
        dummy.Body.Position += new Vector2(500f, 0f);
        sim.Step(default);
        Assert.True(Vector2.Distance(dummy.Body.Position, DummyHome) < 20f,
            $"Dummy not back home after displacement-reset (at {dummy.Body.Position}).");
    }
}
