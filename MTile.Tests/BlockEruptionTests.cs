using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The block-eruption gesture, since the release-time-upgrade redesign, is ONE action
// end to end: BlockPaintAction. The canonical "works" gesture:
//
//   1. Press RMB with cursor over a solid cell — the hold latches as a charge;
//      BuildMeters banks eruption charge while the cursor stays buried.
//   2. Sweep the cursor out of solid — the SAME stroke simply starts painting.
//      Nothing arms, nothing is forfeited by waiting.
//   3. Release RMB while the ball is moving fast — BlockPaintAction.Exit upgrades
//      the stroke into an eruption and spawns the MassBall.
//
// This harness drives player.Update directly with no spawner or entity stepping, so
// the ball itself can't be observed here — EruptionPillarTests runs the full
// Simulation for that. What this test pins is the FSM shape: the whole gesture stays
// inside BlockPaintAction (the old BlockEruptionAction stole the stroke for an arming
// window, which is exactly the bug the redesign removed), the charge banks, and the
// release ends the action.
public class BlockEruptionTests(ITestOutputHelper output)
{
    [Fact]
    public void ChargeUnderground_SweepUp_StaysOnePaintAction()
    {
        // 16-wide column, top 10 rows air, bottom 6 rows solid. Player stands
        // on top of row 10 (rest pose shared with JumpFromCompressedTests: floorTop
        // - 19, an empirically-settled offset). Cursor charge target: row 12,
        // comfortably inside the solid floor. Sweep target: row 5, air above.
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXX
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        float floorTopY = 10 * Chunk.TileSize;
        var player = new PlayerCharacter(new Vector2(80f, floorTopY - 19f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl   = new Controller();
        var hb     = new HitboxWorld();
        var hu     = new HurtboxWorld();
        const float dt = 1f / 30f;
        var g = new Vector2(0f, 600f);

        Vector2 cursorInSolid = new(80f, 12 * Chunk.TileSize + Chunk.TileSize / 2f);   // row 12, solid
        Vector2 cursorInAir   = new(80f, 5 * Chunk.TileSize + Chunk.TileSize / 2f);    // row 5,  air

        // 1) Settle (no input). Lets the player land + StandingState take over.
        for (int i = 0; i < 30; i++)
        {
            ctrl.InjectInput(new PlayerInput { MouseWorldPosition = cursorInSolid });
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
        }
        Assert.Contains("Standing", player.CurrentStateName);

        // 2) Charge: hold RMB with cursor in solid for ~3 seconds. BlockPaint should
        //    activate on the press frame, stay current, and bank eruption charge.
        for (int f = 0; f < 90; f++)
        {
            ctrl.InjectInput(new PlayerInput
            {
                RightClick         = true,
                MouseWorldPosition = cursorInSolid,
            });
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
        }
        output.WriteLine($"After 3s charge: action={player.CurrentActionName}, " +
                         $"erupt={player.Abilities.Meters.EruptMove:F1}");
        Assert.Equal("BlockPaintAction", player.CurrentActionName);
        Assert.True(player.Abilities.Meters.CanFireEruption,
            "an underground hold should bank enough charge to fire.");

        // 3) Sweep up into air, button still held. The stroke must NOT change action —
        //    it demotes to painting inside the same BlockPaintAction hold.
        for (int f = 0; f < 5; f++)
        {
            ctrl.InjectInput(new PlayerInput
            {
                RightClick         = true,
                MouseWorldPosition = cursorInAir,
            });
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
            Assert.Equal("BlockPaintAction", player.CurrentActionName);
        }

        // 4) Release ends the gesture. The eruption upgrade (charge + fast ball at
        //    release) fires in Exit and needs a spawner to observe — covered by
        //    EruptionPillarTests against the full Simulation.
        ctrl.InjectInput(new PlayerInput { MouseWorldPosition = cursorInAir });
        player.Update(ctrl, terrain, hb, hu, dt);
        PhysicsWorld.StepSwept(bodies, terrain, dt, g);

        output.WriteLine($"After release: action={player.CurrentActionName}");
        Assert.False(player.CurrentAction is BlockPaintAction,
            "Releasing RMB should end the paint/charge gesture.");
    }
}
