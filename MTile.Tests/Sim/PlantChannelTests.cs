using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;
using static MTile.Tests.Sim.PlantTestArena;

namespace MTile.Tests.Sim;

// Planting is a crouched channel (BlockPlaceAction seed mode, Plans/PLANT_SEEDS_PLAN.md §3).
// One input: RMB held on the seed bar, on the ground, on a site legal from a crouch makes the
// player crouch (the seed crouch, EnvironmentContext.SeedCrouch — not the Down key); the
// channel starts on the first crouched frame and plants + pays after
// PlantTuning.SeedChannelFrames. Releasing, jumping, a hit or the site leaving reach cancels
// it with nothing planted or spent.
public class PlantChannelTests(ITestOutputHelper output)
{
    private const int Row = FloorRow - 1;

    private static Simulation OnSeedBar(Simulation sim = null)
    {
        sim ??= Make();
        Run(sim, 0.5f);
        sim.Step(new PlayerInput { F = true });
        sim.Step(default);
        Assert.True(sim.Player.SeedBarActive);
        Assert.Equal(SeedKind.Spitter, sim.Player.SelectedSeed);
        return sim;
    }

    private static PlayerInput Hold(int cx, bool down = false, int cy = Row)
        => new() { RightClick = true, Down = down, MouseWorldPosition = CellCenter(cx, cy) };

    private static float Pool(Simulation s) => s.Player.Abilities.Meters.BuildMove + s.Player.Abilities.Meters.EruptMove;
    private static bool Crouched(Simulation s) => s.Player.CurrentState is CrouchedState;
    private static float Progress(Simulation s) => BlockPlaceAction.SeedChannelProgress(s.Player.CurrentActionVars);

    // RMB alone, from standing: crouches, channels, plants exactly one, pays once, then lets
    // the player stand while RMB is still held.
    [Fact]
    public void RmbAlone_OnALegalSite_Crouches_Channels_AndPlantsExactlyOne()
    {
        var sim = OnSeedBar();
        Assert.IsType<StandingState>(sim.Player.CurrentState);
        float before = Pool(sim);
        int plantedAt = -1;
        for (int f = 1; f <= PlantTuning.SeedChannelFrames + 10 && plantedAt < 0; f++)
        {
            sim.Step(Hold(5));
            if (f == 1) Assert.True(Crouched(sim), "the press frame crouches");
            if (Plants(sim).Count > 0) plantedAt = f;
            else Assert.True(Crouched(sim), $"crouched through the channel (frame {f})");
        }
        output.WriteLine($"press -> plant: {plantedAt} frames (SeedChannelFrames {PlantTuning.SeedChannelFrames})");
        Assert.Equal(PlantTuning.SeedChannelFrames, plantedAt);   // press frame counts as frame 1
        var plant = Assert.Single(Plants(sim));
        Assert.Equal((5, Row), (plant.CellX, plant.CellY));
        Assert.True(Pool(sim) < before, "paid on completion");

        float after = Pool(sim);
        for (int i = 0; i < 60; i++) sim.Step(Hold(5));        // held on: one plant, not pinned low
        Assert.Single(Plants(sim));
        Assert.True(Pool(sim) >= after, "nothing more spent");
        Assert.False(Crouched(sim), "the seed crouch ends once the plant is down");
    }

    // Holding Down already still works the same way.
    [Fact]
    public void AlreadyCrouchingWithDown_StillPlants()
    {
        var sim = OnSeedBar();
        Crouch(sim);
        for (int i = 0; i < PlantTuning.SeedChannelFrames; i++) sim.Step(Hold(5, down: true));
        Assert.Single(Plants(sim));
        Assert.True(Crouched(sim), "Down keeps the player low");
    }

    // Illegal (out of reach, stone, open air, occupied-by-nothing-but-air above the head):
    // RMB does not crouch the player at all, and nothing starts.
    [Theory]
    [InlineData(8, Row)]          // three cells off: out of reach
    [InlineData(4, Row - 4)]      // open air above the head: no root
    [InlineData(4, FloorRow)]     // inside the floor
    public void RmbOnAnIllegalCell_DoesNotCrouch(int cx, int cy)
    {
        var sim = OnSeedBar();
        for (int i = 0; i < PlantTuning.SeedChannelFrames + 20; i++)
        {
            sim.Step(Hold(cx, cy: cy));
            Assert.False(Crouched(sim), $"crouched at {i}");
            Assert.True(Progress(sim) < 0f);
        }
        Assert.Empty(Plants(sim));
    }

    [Fact]
    public void RmbOnStone_DoesNotCrouch()
    {
        var sim = Make();
        sim.Player.Body.Position = Spawn(32);
        OnSeedBar(sim);
        for (int i = 0; i < 40; i++) { sim.Step(Hold(33)); Assert.False(Crouched(sim)); }
        Assert.Empty(Plants(sim));
    }

    [Fact]
    public void RmbOnTheBlockBar_NeverCrouches()
    {
        var sim = Make();
        Run(sim, 0.5f);
        Assert.False(sim.Player.SeedBarActive);
        for (int i = 0; i < 40; i++) { sim.Step(Hold(5)); Assert.False(Crouched(sim)); }
        Assert.Empty(Plants(sim));
    }

    // Pressed in the air: nothing — not on landing either, for the rest of that hold.
    [Fact]
    public void AirborneRmb_DoesNothing()
    {
        var sim = OnSeedBar();
        sim.Step(new PlayerInput { Space = true });
        for (int i = 0; i < 4; i++) sim.Step(default);
        Assert.False(sim.Player.IsGrounded);
        float before = Pool(sim);
        for (int i = 0; i < 120; i++)
        {
            sim.Step(Hold(5));
            Assert.False(Crouched(sim), $"crouched at {i}");
        }
        Assert.True(sim.Player.IsGrounded, "landed while still holding");
        Assert.Empty(Plants(sim));
        Assert.True(Pool(sim) >= before);
    }

    public enum Cut { Release, Jump, Hit, CrouchWalkAway }

    // Interrupted halfway, then the gesture carries on (RMB held again — except Release,
    // which stays up so no new press starts a fresh channel): the channel is cancelled, not
    // paused — nothing planted, nothing spent — and the seed crouch is gone.
    [Theory]
    [InlineData(Cut.Release)]
    [InlineData(Cut.Jump)]
    [InlineData(Cut.Hit)]
    [InlineData(Cut.CrouchWalkAway)]
    public void Interrupted_Channel_PlantsNothing_AndSpendsNothing(Cut cut)
    {
        var sim = OnSeedBar();
        int target = cut == Cut.CrouchWalkAway ? 3 : 5;
        float before = Pool(sim);
        // Cut halfway; the crouch-walk starts early, so leaving reach beats completion.
        int pre = cut == Cut.CrouchWalkAway ? 4 : PlantTuning.SeedChannelFrames / 2;
        for (int i = 0; i < pre; i++) sim.Step(Hold(target, down: cut == Cut.CrouchWalkAway));
        Assert.True(Progress(sim) > 0f);

        switch (cut)
        {
            case Cut.Release:
                for (int i = 0; i < 20; i++) sim.Step(default);
                Assert.False(Crouched(sim), "releasing RMB stands the player up");
                break;
            case Cut.Jump:
                sim.Step(new PlayerInput { RightClick = true, Space = true, MouseWorldPosition = CellCenter(target, Row) });
                for (int i = 0; i < 40; i++) sim.Step(Hold(target));
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
        Assert.True(Progress(sim) < 0f, "channel cancelled");

        var resume = cut == Cut.Release ? default : Hold(target);
        for (int i = 0; i < PlantTuning.SeedChannelFrames + 30; i++) sim.Step(resume);
        Assert.Empty(Plants(sim));
        Assert.True(Pool(sim) >= before, "nothing spent");
        if (cut != Cut.CrouchWalkAway) Assert.False(Crouched(sim), "no lingering seed crouch");
    }

    // The seed crouch is not the Down key: standing at a platform's edge with RMB held on a
    // plantable cell never drops off the edge (Down's dropdown), and LMB from the seed
    // crouch is not a crouch slash (control: with Down it is).
    [Fact]
    public void SeedCrouch_DoesNotDropOffALedge()
    {
        var sim = Make();
        for (int x = 7; x < 64; x++)
            for (int y = FloorRow; y < 16; y++)
                sim.Chunks.EditorSetCell(x, y, TileState.Empty, TileType.Dirt);
        sim.Player.Body.Position = Spawn(6);
        OnSeedBar(sim);
        Assert.IsType<StandingState>(sim.Player.CurrentState);
        for (int i = 0; i < PlantTuning.SeedChannelFrames + 30; i++)
        {
            sim.Step(Hold(5));
            Assert.IsNotType<DropdownState>(sim.Player.CurrentState);
            Assert.True(sim.Player.Body.Bounds.Left < 7 * Ts, $"slid off the ledge at {i}");
        }
        Assert.Single(Plants(sim));
        Run(sim, 0.5f);
        Assert.True(sim.Player.IsGrounded, "still on the ledge");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeedCrouch_Lmb_IsNotACrouchSlash(bool holdDown)
    {
        var sim = OnSeedBar();
        for (int i = 0; i < 10; i++) sim.Step(Hold(5, down: holdDown));
        Assert.True(Crouched(sim));
        bool crouchSlash = false;
        var lmb = Hold(5, down: holdDown); lmb.LeftClick = true;
        sim.Step(lmb);
        for (int i = 0; i < 15; i++)
        {
            sim.Step(Hold(5, down: holdDown));
            crouchSlash |= sim.Player.CurrentAction is CrouchSlash;
        }
        Assert.Equal(holdDown, crouchSlash);
    }

    // Rollback: snapshot mid-channel (seed crouch latched, channel counting), play on through
    // completion, restore, replay — both runs agree to the bit.
    [Fact]
    public void SnapshotRestore_MidChannel_ReplaysIdentically()
    {
        var sim = OnSeedBar();
        int half = PlantTuning.SeedChannelFrames / 2;
        for (int i = 0; i < half; i++) sim.Step(Hold(5));
        int framesAtSnap = sim.Player.CurrentActionVars.SeedChannelFrames;
        Assert.True(framesAtSnap > 0);
        var snap = sim.Snapshot();

        int n = PlantTuning.SeedChannelFrames + 20;
        for (int i = 0; i < n; i++) sim.Step(Hold(5));
        Assert.Single(Plants(sim));
        ulong first = sim.Checksum();
        var pos = sim.Player.Body.Position;

        sim.Restore(snap);
        Assert.Equal(framesAtSnap, sim.Player.CurrentActionVars.SeedChannelFrames);
        Assert.True(sim.Player.CurrentActionVars.SeedArmed);
        Assert.True(Crouched(sim));
        Assert.Empty(Plants(sim));
        for (int i = 0; i < n; i++) sim.Step(Hold(5));
        Assert.Single(Plants(sim));
        Assert.Equal(first, sim.Checksum());
        Assert.Equal(pos, sim.Player.Body.Position);
    }
}
