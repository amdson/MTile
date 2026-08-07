using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Block-eruption behaviour: charge inside the ground, sweep out, release fast.
//
// Since the release-time-upgrade redesign there is no BlockEruptionAction: the charged
// stroke paints as soon as it leaves the ground, and BlockPaintAction.Exit upgrades it
// into a free-flying MassBall when RMB comes up with banked charge and a fast-moving
// ball. The contract pinned here is the upgrade's defining property: the deposit
// continues PAST where the stroke ended, because the ball keeps its release velocity —
// and a slow (parked) release does NOT erupt at all, it was just painting.
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
        // 40 columns of solid ground, 5 rows deep.
        var sb = new System.Text.StringBuilder();
        for (int row = 0; row < 5; row++)
            sb.Append(new string('X', 40)).Append('\n');
        // Top-left ASCII char maps to tile (originTileX, GroundTop).
        return SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: GroundTop);
    }

    // Count solid cells above the original ground surface.
    private static int BlocksAbove(ChunkMap chunks)
    {
        int blocks = 0;
        for (int gty = GroundTop - 1; gty >= GroundTop - 12; gty--)
            for (int gtx = OriginGtx - 10; gtx <= OriginGtx + 20; gtx++)
                if (chunks.GetCellState(gtx, gty) == TileState.Solid) blocks++;
        return blocks;
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

    // Runs the real gesture end to end: charge inside the ground, sweep up out of it
    // (the same hold becomes a paint stroke), drag RIGHT along just above the surface,
    // and let go. `holdFramesBeforeRelease` is the A/B knob — 0 releases mid-drag with
    // the ball still fast (the eruption upgrade), a dozen frames parks the cursor so
    // the critically-damped ball decays below EruptReleaseSpeed first (no upgrade, the
    // stroke was just painting). Both release from the SAME cursor position.
    //
    // The carry is measured HORIZONTALLY on purpose. A vertical flick sends the ball up
    // past its own scaffolding — the deposit needs a few frames to solidify before it
    // can support the next cell — so unsupported mass dies and a faster release measures
    // as a SHORTER pillar. Dragging along the ground keeps support under the whole
    // coast, which isolates the property being tested from the ball-outruns-its-
    // scaffolding limitation. Returns how far right the deposit reached + block count.
    private (int reach, int blocks) RunEruption(int holdFramesBeforeRelease)
    {
        var chunks = BuildGround();
        var sim = new Simulation(chunks, CellCenter(OriginGtx, GroundTop - 1));
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput());

        // Plain RMB, pressed with the cursor buried — the charge half of the gesture.
        PlayerInput Rmb(Vector2 mouse) => new() { RightClick = true, MouseWorldPosition = mouse };

        // Charge below the surface until the meters can fund an eruption.
        var chargePos = CellCenter(OriginGtx, GroundTop + 2);
        for (int f = 0; f < 70; f++) sim.Step(Rmb(chargePos));
        Assert.True(sim.Player.Abilities.Meters.CanFireEruption,
            "the underground hold banked too little charge — lengthen the hold");

        // Sweep up out of the ground and drag right along just above the surface,
        // fast enough that the damped ball is carrying real speed. The whole time the
        // stroke must remain BlockPaintAction — there is no separate eruption state.
        float exitY = CellCenter(OriginGtx, GroundTop - 1).Y;
        float y = chargePos.Y;
        while (y > exitY) { y -= 20f; sim.Step(Rmb(new Vector2(chargePos.X, y))); }
        float x = chargePos.X;
        for (int f = 0; f < 6; f++) { x += 20f; sim.Step(Rmb(new Vector2(x, exitY))); }
        Assert.True(sim.Player.CurrentAction is BlockPaintAction,
            "the charged stroke should stay in BlockPaintAction after leaving the ground");

        for (int f = 0; f < holdFramesBeforeRelease; f++) sim.Step(Rmb(new Vector2(x, exitY)));

        sim.Step(new PlayerInput());                                  // release
        for (int f = 0; f < 180; f++) sim.Step(new PlayerInput());    // fly + grow

        return (RightmostReach(chunks), BlocksAbove(chunks));
    }

    // The defining property of the release-time upgrade: released with the ball in
    // motion, the stroke erupts and the deposit carries onward past the release point;
    // released from rest, the same charge + same cursor position is just the end of a
    // paint stroke and the deposit stops where the painting stopped.
    [Fact]
    public void ReleasedInMotion_CarriesFartherThanReleasedFromRest()
    {
        var moving = RunEruption(holdFramesBeforeRelease: 0);
        var parked = RunEruption(holdFramesBeforeRelease: 12);

        _out.WriteLine($"released in motion: {moving.blocks} blocks, reach +{moving.reach} cells");
        _out.WriteLine($"released from rest: {parked.blocks} blocks, reach +{parked.reach} cells");

        Assert.True(moving.blocks > 0, "the gesture deposited nothing at all");
        Assert.True(moving.reach > parked.reach,
            $"a release in motion should erupt and carry farther than one from rest " +
            $"(+{moving.reach} vs +{parked.reach} cells) — the upgrade isn't firing " +
            "or the ball isn't keeping its velocity.");
    }

    // The no-surprise rule, restated for the upgrade design: an ordinary paint stroke
    // (never buried, no charge banked) must NOT erupt no matter how fast the release
    // is. Fast-flick a plain stroke and let go mid-motion: the deposit must stay within
    // the painter's reach of the player rather than sailing off as a ball.
    [Fact]
    public void FastReleaseWithoutCharge_DoesNotErupt()
    {
        var chunks = BuildGround();
        var sim = new Simulation(chunks, CellCenter(OriginGtx, GroundTop - 1));
        for (int f = 0; f < 10; f++) sim.Step(new PlayerInput());

        PlayerInput Rmb(Vector2 mouse) => new() { RightClick = true, MouseWorldPosition = mouse };

        // Paint briefly just above the surface, then flick hard right and release
        // mid-motion. No time in solid → no charge → no upgrade.
        float exitY = CellCenter(OriginGtx, GroundTop - 1).Y;
        float x = CellCenter(OriginGtx, GroundTop - 1).X;
        for (int f = 0; f < 20; f++) sim.Step(Rmb(new Vector2(x, exitY)));
        for (int f = 0; f < 6; f++) { x += 24f; sim.Step(Rmb(new Vector2(x, exitY))); }
        sim.Step(new PlayerInput());
        for (int f = 0; f < 180; f++) sim.Step(new PlayerInput());

        int reach = RightmostReach(chunks);
        // BuildReach (64px = 4 cells, x2 chained) bounds what painting alone can lay
        // down; an erupted ball would coast far past it.
        _out.WriteLine($"uncharged fast release: reach +{reach} cells");
        Assert.True(reach <= 9,
            $"an uncharged stroke reached +{reach} cells — a MassBall erupted without charge.");
    }
}
