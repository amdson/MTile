using Microsoft.Xna.Framework;
using Xunit;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// Planting is a crouched channel (BlockPlaceAction seed mode, Plans/PLANT_SEEDS_PLAN.md §3):
// RMB pressed while crouched on the seed bar latches the cell; PlantTuning.SeedChannelFrames
// (1.5 s) of crouched hold plant it and pay once. Anything that ends the crouch, a hit,
// releasing, or the site leaving reach cancels it with nothing planted or spent.
public class PlantChannelTests
{
    private const int Row = FloorRow - 1;

    private static Simulation OnSeedBar()
    {
        var sim = Make();
        Run(sim, 0.5f);
        sim.Step(new PlayerInput { F = true });
        sim.Step(default);
        Assert.True(sim.Player.SeedBarActive);
        Assert.Equal(SeedKind.Spitter, sim.Player.SelectedSeed);
        return sim;
    }

    private static PlayerInput Hold(int cx, bool down = true)
        => new() { RightClick = true, Down = down, MouseWorldPosition = CellCenter(cx, Row) };

    private static float Pool(Simulation s) => s.Player.Abilities.Meters.BuildMove + s.Player.Abilities.Meters.EruptMove;

    [Fact]
    public void Channel_CompletesAfter90Frames_AndPlantsExactlyOne()
    {
        Assert.Equal(90, PlantTuning.SeedChannelFrames);
        var sim = OnSeedBar();
        Crouch(sim);
        float before = Pool(sim);
        for (int i = 0; i < PlantTuning.SeedChannelFrames - 1; i++)
        {
            sim.Step(Hold(5));
            Assert.Empty(Plants(sim));
            Assert.True(BlockPlaceAction.SeedChannelProgress(sim.Player.CurrentActionVars) >= 0f, $"channel live at {i}");
        }
        sim.Step(Hold(5));   // frame 90
        var plant = Assert.Single(Plants(sim));
        Assert.Equal((5, Row), (plant.CellX, plant.CellY));
        Assert.True(Pool(sim) < before, "paid on completion");
        float after = Pool(sim);
        for (int i = 0; i < 120; i++) sim.Step(Hold(5));   // holding on plants nothing more
        Assert.Single(Plants(sim));
        Assert.True(Pool(sim) >= after, "nothing more spent");
    }

    [Fact]
    public void NotCrouched_NothingStarts()
    {
        var sim = OnSeedBar();
        Run(sim, 0.3f);
        Assert.IsNotType<CrouchedState>(sim.Player.CurrentState);
        float before = Pool(sim);
        for (int i = 0; i < PlantTuning.SeedChannelFrames + 30; i++)
        {
            sim.Step(Hold(5, down: false));
            Assert.True(BlockPlaceAction.SeedChannelProgress(sim.Player.CurrentActionVars) < 0f);
        }
        Assert.Empty(Plants(sim));
        Assert.True(Pool(sim) >= before);
    }

    public enum Cut { StandUp, Jump, Release, Hit, CrouchWalkAway }

    // Interrupted halfway, then the gesture carries on as if nothing happened (RMB + Down
    // held again — except Release, which stays up so no new press starts a fresh channel):
    // the channel is cancelled, not paused — nothing planted, nothing spent.
    [Theory]
    [InlineData(Cut.StandUp)]
    [InlineData(Cut.Jump)]
    [InlineData(Cut.Release)]
    [InlineData(Cut.Hit)]
    [InlineData(Cut.CrouchWalkAway)]
    public void Interrupted_Channel_PlantsNothing_AndSpendsNothing(Cut cut)
    {
        var sim = OnSeedBar();
        Crouch(sim);
        int target = cut == Cut.CrouchWalkAway ? 3 : 5;
        float before = Pool(sim);
        for (int i = 0; i < 45; i++) sim.Step(Hold(target));
        Assert.True(BlockPlaceAction.SeedChannelProgress(sim.Player.CurrentActionVars) > 0f);

        switch (cut)
        {
            case Cut.StandUp:
                for (int i = 0; i < 15; i++) sim.Step(Hold(target, down: false));
                break;
            case Cut.Jump:
                sim.Step(new PlayerInput { RightClick = true, Space = true, MouseWorldPosition = CellCenter(target, Row) });
                for (int i = 0; i < 40; i++) sim.Step(Hold(target, down: false));
                break;
            case Cut.Release:
                for (int i = 0; i < 5; i++) sim.Step(new PlayerInput { Down = true });
                break;
            case Cut.Hit:
                sim.Player.Combat.OnHitRegistered(sim.Player.Frame, 60f, Dt);
                sim.Step(Hold(target));
                break;
            case Cut.CrouchWalkAway:
                for (int i = 0; i < 40; i++)
                    sim.Step(new PlayerInput { RightClick = true, Down = true, Right = true,
                                               MouseWorldPosition = CellCenter(target, Row) });
                break;
        }
        Assert.True(BlockPlaceAction.SeedChannelProgress(sim.Player.CurrentActionVars) < 0f, "channel cancelled");

        var resume = cut == Cut.Release ? new PlayerInput { Down = true } : Hold(target);
        for (int i = 0; i < PlantTuning.SeedChannelFrames + 30; i++) sim.Step(resume);
        Assert.Empty(Plants(sim));
        Assert.True(Pool(sim) >= before, "nothing spent");
    }

    // Rollback: snapshot mid-channel, play on through completion, restore, replay — the
    // channel state rides ActionVars, so both runs agree to the bit.
    [Fact]
    public void SnapshotRestore_MidChannel_ReplaysIdentically()
    {
        var sim = OnSeedBar();
        Crouch(sim);
        for (int i = 0; i < 40; i++) sim.Step(Hold(5));
        int framesAtSnap = sim.Player.CurrentActionVars.SeedChannelFrames;
        Assert.True(framesAtSnap > 0);
        var snap = sim.Snapshot();

        for (int i = 0; i < 70; i++) sim.Step(Hold(5));
        Assert.Single(Plants(sim));
        ulong first = sim.Checksum();

        // Disturb the live channel state, then restore over it.
        sim.Restore(snap);
        Assert.Equal(framesAtSnap, sim.Player.CurrentActionVars.SeedChannelFrames);
        Assert.True(sim.Player.CurrentActionVars.SeedArmed);
        Assert.Empty(Plants(sim));
        for (int i = 0; i < 70; i++) sim.Step(Hold(5));
        Assert.Single(Plants(sim));
        Assert.Equal(first, sim.Checksum());
    }
}
