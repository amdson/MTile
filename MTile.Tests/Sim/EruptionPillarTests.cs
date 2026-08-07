using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Block-eruption behaviour: charge on a block, sweep up, release.
//
// This used to drive EruptionPlanner directly and assert a monotonically-narrowing
// pillar — a property of that planner's radius falloff, which no longer exists. The
// eruption is now a MassBall entity coasting out of the release point and leaking into
// TileMassField, so the contract that actually matters is the one the planner could only
// fake: the deposit continues PAST where the cursor was when the button came up. That's
// what the first test pins, driven through the real FSM rather than a planner call.
public class EruptionPillarTests
{
    private readonly ITestOutputHelper _out;
    public EruptionPillarTests(ITestOutputHelper output) => _out = output;

    private const int TileSz = Chunk.TileSize;

    // Solid ground from gty >= GroundTop; open air above. Origin sits on the surface.
    private const int GroundTop = 10;
    private const int OriginGtx = 20;

    private static Vector2 CellCenter(int gtx, int gty)
        => new(gtx * TileSz + TileSz * 0.5f, gty * TileSz + TileSz * 0.5f);

    private static ChunkMap BuildGround()
    {
        // 30 columns of solid ground, 5 rows deep, centred under the origin column.
        var sb = new System.Text.StringBuilder();
        for (int row = 0; row < 5; row++)
            sb.Append(new string('X', 40)).Append('\n');
        // Top-left ASCII char maps to tile (originTileX, GroundTop).
        return SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: GroundTop);
    }

    // Count solid cells per row above the original ground surface, keyed by gty.
    private static Dictionary<int, int> SolidWidthsAbove(ChunkMap chunks)
    {
        var widths = new Dictionary<int, int>();
        for (int gty = GroundTop - 1; gty >= GroundTop - 12; gty--)
        {
            int count = 0;
            for (int gtx = OriginGtx - 10; gtx <= OriginGtx + 10; gtx++)
                if (chunks.GetCellState(gtx, gty) == TileState.Solid) count++;
            widths[gty] = count;
        }
        return widths;
    }

    // How far right of the origin column the deposit reached (0 if nothing landed).
    private static int RightmostReach(ChunkMap chunks)
    {
        int reach = 0;
        for (int gty = GroundTop - 1; gty >= GroundTop - 12; gty--)
            for (int gtx = OriginGtx - 10; gtx <= OriginGtx + 20; gtx++)
                if (chunks.GetCellState(gtx, gty) == TileState.Solid && gtx - OriginGtx > reach)
                    reach = gtx - OriginGtx;
        return reach;
    }

    // Runs the real gesture end to end: charge deep inside the ground, flick up out of it
    // (that crossing is what arms), then drag RIGHT along just above the surface and let
    // go. `holdFramesBeforeRelease` is the A/B knob — 0 releases mid-drag, a dozen frames
    // parks the cursor first so the critically-damped ball catches up and stops. Both
    // release from the SAME cursor position.
    //
    // The carry is measured HORIZONTALLY on purpose. A vertical flick sends the ball up
    // past its own scaffolding — the deposit needs a few frames to solidify before it can
    // support the next cell — so unsupported mass dies and a faster release measures as a
    // SHORTER pillar. Dragging along the ground keeps support under the whole coast, which
    // isolates the property being tested from the ball-outruns-its-scaffolding limitation.
    // Returns how far right the deposit reached, and the total block count.
    private (int reach, int blocks) RunEruption(int holdFramesBeforeRelease)
    {
        var chunks = BuildGround();
        var sim = new Simulation(chunks, CellCenter(OriginGtx, GroundTop - 1));
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput());

        // Plain RMB, pressed with the cursor buried — the charge half of the gesture.
        PlayerInput Rmb(Vector2 mouse) => new() { RightClick = true, MouseWorldPosition = mouse };

        // Charge from deep in the ground; the solid→air crossing of the flick below is
        // what arms (no speed gate — the crossing itself is the signal).
        var chargePos = CellCenter(OriginGtx, GroundTop + 4);
        for (int f = 0; f < 70; f++) sim.Step(Rmb(chargePos));   // ~2.3s charge, well past MinChargeToArm

        // Flick up out of the ground — this crossing is what arms the eruption.
        float y = chargePos.Y;
        float exitY = CellCenter(OriginGtx, GroundTop - 1).Y;
        while (y > exitY) { y -= 20f; sim.Step(Rmb(new Vector2(chargePos.X, y))); }
        // The flag is raised inside the crossing frame's Update; the FSM picks it up on
        // the next scan, so BlockEruptionAction is only current one frame later.
        sim.Step(Rmb(new Vector2(chargePos.X, exitY)));

        Assert.True(sim.Player.CurrentAction is BlockEruptionAction,
            "eruption never armed — adjust the gesture so the test exercises the real path");

        // Drag right along the surface. Stays inside BlockEruptionAction's short arming
        // window, so the release still counts as an eruption.
        float x = chargePos.X;
        for (int f = 0; f < 6; f++) { x += 20f; sim.Step(Rmb(new Vector2(x, exitY))); }

        for (int f = 0; f < holdFramesBeforeRelease; f++) sim.Step(Rmb(new Vector2(x, exitY)));

        sim.Step(new PlayerInput());                                 // release → spawn the ball
        for (int f = 0; f < 180; f++) sim.Step(new PlayerInput());    // fly + grow

        var widths = SolidWidthsAbove(chunks);
        int blocks = 0;
        for (int gty = GroundTop - 1; gty >= GroundTop - 12; gty--) blocks += widths[gty];
        return (RightmostReach(chunks), blocks);
    }

    // The defining property of the ball-based eruption, and the one the old planner could
    // only fake by extrapolating its puller off the end of a recorded path: released
    // mid-sweep, the ball keeps the velocity the sweep gave it and carries the deposit
    // onward. Measured as an A/B against the identical gesture released from rest — same
    // charge, same cursor position, only the motion differs — so it isolates the coast
    // from everything else that shapes the deposit.
    [Fact]
    public void ReleasedInMotion_CarriesFartherThanReleasedFromRest()
    {
        var moving = RunEruption(holdFramesBeforeRelease: 0);
        var parked = RunEruption(holdFramesBeforeRelease: 10);

        _out.WriteLine($"released in motion: {moving.blocks} blocks, reach +{moving.reach} cells");
        _out.WriteLine($"released from rest: {parked.blocks} blocks, reach +{parked.reach} cells");

        Assert.True(moving.blocks > 0, "the eruption deposited nothing at all");
        Assert.True(moving.reach > parked.reach,
            $"a release in motion should carry farther than one from rest " +
            $"(+{moving.reach} vs +{parked.reach} cells) — the ball isn't keeping its velocity.");
    }

    // Repro for the bug the user suspected: "normal right-click block placement fires
    // at the same time as the eruption." Drives the FULL Simulation (so HandleBuildInput
    // runs) through the real gesture — charge on a block, then sweep up out of the ground
    // — with the cursor kept within build reach of the player, then asserts that NO tiles
    // are placed while the eruption is being charged.
    //
    // Regression guard, now structural: BlockReadyAction does not place tiles at all
    // (painting moved to plain RMB / BlockPaintAction), so the arming sweep has nothing to
    // drag-place. Kept because the guarantee still matters — a future rebind that puts
    // placement back on Shift+RMB would break it here rather than in a playtest.
    [Fact]
    public void ChargingEruption_DoesNotAlsoDragPlaceBuildTiles()
    {
        // Flat ground; player will rest on the surface near the origin column.
        var chunks = BuildGround();
        var playerSpawn = CellCenter(OriginGtx, GroundTop - 1);   // just above the surface
        var sim = new Simulation(chunks, playerSpawn);

        // Let the player settle on the ground with no input.
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput());

        // Cursor cells, all within BuildReach (64px) of the player:
        //   charge: a SOLID cell at/just below the surface (no placement possible there)
        //   sweep:  empty cells climbing straight up out of the ground
        // Charge below the surface; the swept cells above it are checked for strays.
        var chargeCell = CellCenter(OriginGtx, GroundTop + 4);         // solid
        var sweepCells = new[]
        {
            CellCenter(OriginGtx, GroundTop - 1),
            CellCenter(OriginGtx, GroundTop - 2),
            CellCenter(OriginGtx, GroundTop - 3),
        };

        // Plain RMB. Pressed with the cursor buried, the hold is the charge gesture; the
        // flick out of solid is what arms the eruption.
        PlayerInput Rmb(Vector2 mouse) => new() { RightClick = true, MouseWorldPosition = mouse };

        // Charge ~2.3s on the solid cell (well past MinChargeToArm = 1.0s).
        for (int f = 0; f < 70; f++) sim.Step(Rmb(chargeCell));

        // Perform the upward sweep (RMB still held — NOT released, so the eruption has
        // not fired yet). The empty cells the cursor crosses start Empty; before release,
        // ONLY HandleBuildInput's drag-place can fill them, so any that turn non-Empty are
        // stray build tiles placed concurrently with the eruption gesture.
        float sy = chargeCell.Y;
        float topY = sweepCells[^1].Y;
        while (sy > topY) { sy -= 20f; sim.Step(Rmb(new Vector2(chargeCell.X, sy))); }
        for (int f = 0; f < 3; f++) sim.Step(Rmb(new Vector2(chargeCell.X, topY)));

        Assert.True(sim.Player.CurrentAction is BlockEruptionAction,
            "eruption never armed — adjust the gesture so the test exercises the real path");

        int strays = 0;
        foreach (var c in sweepCells)
        {
            int gtx = (int)(c.X / TileSz), gty = (int)(c.Y / TileSz);
            var state = chunks.GetCellState(gtx, gty);
            _out.WriteLine($"swept cell ({gtx},{gty}) state={state}");
            if (state != TileState.Empty) strays++;
        }

        // While an eruption is charging/arming, the held RMB must NOT also be drag-building.
        Assert.True(strays == 0,
            $"{strays} stray build tile(s) were drag-placed during the eruption gesture " +
            "— HandleBuildInput fires at the same time as the eruption (it only suppresses " +
            "drag-place once CurrentAction is BlockEruptionAction, not during the BlockReady charge/arm).");
    }

    // Experiment for the hypothesis: "if the player drags slowly, HandleBuildInput
    // (which runs before the player update) fills cells under the cursor and the
    // in→out sweep that arms BlockEruption never registers." Runs the SAME gesture
    // at two drag speeds and reports whether BlockEruptionAction ever activates.
    [Theory]
    [InlineData("slow", 4f)]    // ~4 px/frame: cursor lingers in each cell
    [InlineData("fast", 48f)]   // ~3 cells/frame: cursor outruns single-cell placement
    public void DragSpeed_VsEruptionArming(string label, float pxPerFrame)
    {
        var chunks = BuildGround();
        var sim = new Simulation(chunks, CellCenter(OriginGtx, GroundTop - 1));
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput());

        // Plain RMB. Pressed with the cursor buried, the hold is the charge gesture; the
        // flick out of solid is what arms the eruption.
        PlayerInput Rmb(Vector2 mouse) => new() { RightClick = true, MouseWorldPosition = mouse };

        // Charge ~2.3s on a solid cell just below the surface (within build reach,
        // but solid → no placement → charge accumulates cleanly).
        var chargePos = CellCenter(OriginGtx, GroundTop);
        for (int f = 0; f < 70; f++) sim.Step(Rmb(chargePos));

        // Drag straight up out of the ground at the given speed, sampling many frames.
        bool eruptionActivated = false;
        float y = chargePos.Y;
        float topY = CellCenter(OriginGtx, GroundTop - 3).Y;   // 3 cells up, into open air
        while (y > topY)
        {
            y -= pxPerFrame;
            sim.Step(Rmb(new Vector2(chargePos.X, y)));
            if (sim.Player.CurrentAction is BlockEruptionAction) eruptionActivated = true;
        }
        // Hold briefly at the top, then release.
        for (int f = 0; f < 3; f++)
        {
            sim.Step(Rmb(new Vector2(chargePos.X, topY)));
            if (sim.Player.CurrentAction is BlockEruptionAction) eruptionActivated = true;
        }
        sim.Step(new PlayerInput());   // release
        for (int f = 0; f < 60; f++) sim.Step(new PlayerInput());   // grow

        int blocks = 0;
        for (int gty = GroundTop - 1; gty >= GroundTop - 12; gty--)
            for (int gtx = OriginGtx - 10; gtx <= OriginGtx + 10; gtx++)
                if (chunks.GetCellState(gtx, gty) == TileState.Solid) blocks++;

        _out.WriteLine($"[{label}] eruptionActivated={eruptionActivated}, solid blocks above ground after release={blocks}");
    }

    private static bool IsMonotonicNonIncreasing(Dictionary<int, int> widths, out string why)
    {
        why = "";
        if (widths[GroundTop - 1] == 0) { why = "no blocks formed above the ground"; return false; }
        int prev = widths[GroundTop - 1];
        for (int gty = GroundTop - 2; gty >= GroundTop - 12; gty--)
        {
            if (widths[gty] > prev)
            {
                why = $"thickness increased going up: row {gty + 1} had {prev}, row {gty} has {widths[gty]}";
                return false;
            }
            prev = widths[gty];
        }
        return true;
    }
}
