using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// "Dumb" verification of the block-eruption shape contract: charging on a block,
// then sweeping straight up and releasing, should deposit a PILLAR whose thickness
// decreases monotonically with height (wide base, narrow tip).
//
// This drives EruptionPlanner directly (no FSM / input plumbing) so it isolates the
// planner's shape from the gesture-capture and build-input layers. The samples list
// is exactly what BlockEruptionAction would hand it: a zero-velocity seed at the
// ignition cell (gives the wide base) followed by an upward sweep.
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

    // Seed at the ignition cell (zero velocity → widest base radius), then a straight
    // upward sweep of `steps` samples climbing `risePx` total.
    private static List<PathSample> StraightUpSweep(Vector2 origin, int steps, float risePx)
    {
        var samples = new List<PathSample> { new(origin, Vector2.Zero) };
        var up = new Vector2(0f, -180f);
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            samples.Add(new PathSample(origin + new Vector2(0f, -risePx * t), up));
        }
        return samples;
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

    [Fact]
    public void PriorityField_SweepUp_FormsMonotonicallyNarrowingPillar()
    {
        var chunks = BuildGround();
        var origin = CellCenter(OriginGtx, GroundTop);   // ignition cell = top solid row
        var samples = StraightUpSweep(origin, steps: 16, risePx: 80f);

        // Generous budget so top-K never clips the shape — this is a pure shape test.
        EruptionPlanner.Plan(chunks, origin, samples, budget: 200,
            EruptionPlannerMode.PriorityField, TileType.Stone);

        // Grow every sprout to completion. SproutLifetime is 0.1s; the pillar chains
        // ~10 rows, so 300 frames (10s) is comfortably enough.
        for (int f = 0; f < 300; f++) chunks.TickSprouts(1f / 30f);

        var widths = SolidWidthsAbove(chunks);

        // Dump the profile so a failure shows the actual shape.
        for (int gty = GroundTop - 1; gty >= GroundTop - 12; gty--)
            _out.WriteLine($"row {gty} (height {GroundTop - gty}): width {widths[gty]}");

        // The pillar must exist and reach up off the ground.
        Assert.True(widths[GroundTop - 1] > 0, "no blocks formed above the ground");

        Assert.True(IsMonotonicNonIncreasing(widths, out string why), why);
    }

    // Repro for the bug the user suspected: "normal right-click block placement fires
    // at the same time as the eruption." Drives the FULL Simulation (so HandleBuildInput
    // runs) through the real gesture — charge on a block, then sweep up out of the ground
    // — with the cursor kept within build reach of the player, then asserts that NO tiles
    // are placed while the eruption is being charged.
    //
    // Regression guard for the build-vs-eruption merge: drag-build now lives inside
    // BlockReadyAction and is suppressed once the charge passes MinChargeToArm (the
    // player is committed to an eruption, not painting), so the arming sweep no longer
    // drag-places a stray tile. Before that fix this asserted-clean cell came back Solid.
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
        var chargeCell = CellCenter(OriginGtx, GroundTop);             // solid
        var sweepCells = new[]
        {
            CellCenter(OriginGtx, GroundTop - 1),
            CellCenter(OriginGtx, GroundTop - 2),
            CellCenter(OriginGtx, GroundTop - 3),
        };

        PlayerInput Rmb(Vector2 mouse) => new() { RightClick = true, MouseWorldPosition = mouse };

        // Charge ~2.3s on the solid cell (well past MinChargeToArm = 1.0s).
        for (int f = 0; f < 70; f++) sim.Step(Rmb(chargeCell));

        // Perform the upward sweep (RMB still held — NOT released, so the eruption has
        // not fired yet). The empty cells the cursor crosses start Empty; before release,
        // ONLY HandleBuildInput's drag-place can fill them, so any that turn non-Empty are
        // stray build tiles placed concurrently with the eruption gesture.
        foreach (var c in sweepCells)
            for (int f = 0; f < 3; f++) sim.Step(Rmb(c));

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
