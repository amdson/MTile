using System.Linq;
using Microsoft.Xna.Framework;
using Xunit;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// M0 plumbing (Plans/PLANT_SEEDS_PLAN.md §3): F swaps the number row between the block bar
// and the seed bar (press edge), each bar keeps its own selection, both ride the player
// snapshot, and on the seed bar both plain RMB and Shift+RMB plant ONE seed per press —
// no paint stroke, no eruption charge.
public class PlantSeedBarTests
{
    private const int Row = FloorRow - 1;

    private static void Press(Simulation s, PlayerInput input, int frames = 1)
    {
        for (int i = 0; i < frames; i++) s.Step(input);
    }

    private static void ToggleBar(Simulation s)
    {
        Press(s, new PlayerInput { F = true });
        Press(s, default);
    }

    [Fact]
    public void F_TogglesOnPressEdge_AndHoldingDoesNotRepeat()
    {
        var sim = Make();
        Run(sim, 0.2f);
        Assert.False(sim.Player.SeedBarActive);
        Press(sim, new PlayerInput { F = true }, frames: 20);
        Assert.True(sim.Player.SeedBarActive);
        Press(sim, default);
        Press(sim, new PlayerInput { F = true });
        Assert.False(sim.Player.SeedBarActive);
    }

    [Fact]
    public void EachBar_RemembersItsOwnSelection()
    {
        var sim = Make();
        Run(sim, 0.2f);
        var startBlock = sim.Player.ActiveBlockType;
        ToggleBar(sim);
        Press(sim, new PlayerInput { Num3 = true });
        Assert.Equal(SeedKind.Thorns, sim.Player.SelectedSeed);
        Assert.Equal(startBlock, sim.Player.ActiveBlockType);   // number keys drove the seed bar only

        ToggleBar(sim);
        Press(sim, new PlayerInput { Num4 = true });
        Assert.Equal(TileType.Foam, sim.Player.ActiveBlockType);
        Assert.Equal(SeedKind.Thorns, sim.Player.SelectedSeed);

        ToggleBar(sim);
        Assert.True(sim.Player.SeedBarActive);
        Assert.Equal(SeedKind.Thorns, sim.Player.SelectedSeed);
        Assert.Equal(TileType.Foam, sim.Player.ActiveBlockType);
    }

    [Fact]
    public void BarAndSeed_SurviveSnapshotRestore()
    {
        var sim = Make();
        Run(sim, 0.2f);
        ToggleBar(sim);
        Press(sim, new PlayerInput { Num5 = true });
        var snap = sim.Snapshot();

        ToggleBar(sim);
        Press(sim, new PlayerInput { Num1 = true });
        Assert.False(sim.Player.SeedBarActive);

        sim.Restore(snap);
        Assert.True(sim.Player.SeedBarActive);
        Assert.Equal(SeedKind.BlockEater, sim.Player.SelectedSeed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeedBar_RmbPress_PlantsOneSeed_AndNeverPaintsOrCharges(bool shift)
    {
        var sim = Make();
        Run(sim, 0.5f);
        ToggleBar(sim);
        Press(sim, new PlayerInput { Num1 = true });
        var m = sim.Player.Abilities.Meters;
        float before = m.BuildMove + m.EruptMove;

        var at = CellCenter(6, Row);
        // Held for a second, dragged across three cells: still one plant.
        for (int i = 0; i < 60; i++)
            sim.Step(new PlayerInput { RightClick = true, Shift = shift,
                                       MouseWorldPosition = at + new Vector2(i * 0.5f, 0f) });
        Press(sim, default);

        var plant = Assert.Single(Plants(sim));
        Assert.Equal(6, plant.CellX);
        Assert.Equal(Row, plant.CellY);
        Assert.Equal(0f, m.EruptMove);                               // no eruption charge
        Assert.False(sim.Chunks.Graph.TryGet(6, Row, out _));        // no block requested
        Assert.Equal(TileState.Empty, sim.Chunks.GetCellState(7, Row));
        Assert.Equal(TileState.Empty, sim.Chunks.GetCellState(8, Row));
        // Paid the seed's price (the working pool refilled a little over the hold).
        Assert.True(m.BuildMove + m.EruptMove < before);
    }

    [Fact]
    public void SeedBar_EachPressPlantsOne_UpToTheCap()
    {
        var sim = Make();
        Run(sim, 0.5f);
        ToggleBar(sim);
        foreach (int x in new[] { 2, 4, 6, 7 })
        {
            Press(sim, new PlayerInput { RightClick = true, MouseWorldPosition = CellCenter(x, Row) }, 2);
            Press(sim, default);
            Run(sim, 4.5f);   // let the working pool refill between plants (cost scales with stone's)
        }
        Assert.Equal(PlantTuning.PlantCapPerPlayer, Plants(sim).Count);
    }

    [Fact]
    public void BlockBar_PlainRmb_StillPaints()
    {
        var sim = Make();
        Run(sim, 0.5f);
        for (int i = 0; i < 30; i++)
            sim.Step(new PlayerInput { RightClick = true, MouseWorldPosition = CellCenter(6, Row) });
        Assert.Empty(Plants(sim));
        Assert.NotEqual(TileState.Empty, sim.Chunks.GetCellState(6, Row));
    }
}
