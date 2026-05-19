using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Block-eruption gesture in the two-state design (BlockReadyAction →
// BlockEruptionAction). The canonical "works" gesture:
//
//   1. Press RMB with cursor over a solid cell (the wall the player wants
//      to push tiles out of).
//   2. Hold for >= MinChargeToArm seconds while the cursor stays in solid.
//   3. Sweep cursor out of solid (the "ignition") — BlockReady.Exit sets
//      BlockEruptionArmed; BlockEruption picks up on the same frame.
//   4. Release RMB — BlockEruption.Exit calls EruptionPlanner.Plan and
//      sprouts appear in the chunk map.
//
// This test scripts that gesture with a 3-second underground charge and a
// fast sweep up to air, then asserts the FSM transitioned through both
// action states and the planner actually deposited tile sprouts.
public class BlockEruptionTests(ITestOutputHelper output)
{
    [Fact]
    public void ChargeUnderground_SweepUp_Release_DepositsSprouts()
    {
        // 16-wide column, top 10 rows air, bottom 6 rows solid. Player stands
        // on top of row 10 at y=141 (same rest pose as JumpFromCompressedTests).
        // Cursor charge target: row 12 (y in [192..208]) — comfortably inside
        // the solid floor. Sweep target: row 5 (y in [80..96]) — air above.
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

        var player = new PlayerCharacter(new Vector2(80f, 141f));
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl   = new Controller();
        var hb     = new HitboxWorld();
        var hu     = new HurtboxWorld();
        const float dt = 1f / 30f;
        var g = new Vector2(0f, 600f);

        Vector2 cursorInSolid = new(80f, 200f);   // row 12, solid
        Vector2 cursorInAir   = new(80f, 88f);    // row 5,  air

        // 1) Settle (no input). Lets the player land + StandingState take over.
        for (int i = 0; i < 30; i++)
        {
            ctrl.InjectInput(new PlayerInput { MouseWorldPosition = cursorInSolid });
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);
        }
        Assert.Contains("Standing", player.CurrentStateName);

        // 2) Charge: hold RMB with cursor in solid for ~3 seconds (90 frames @
        //    1/30). BlockReady should activate on the press-edge frame and stay
        //    current throughout.
        bool sawBlockReady = false;
        for (int f = 0; f < 90; f++)
        {
            ctrl.InjectInput(new PlayerInput
            {
                RightClick         = true,
                MouseWorldPosition = cursorInSolid,
            });
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);

            if (player.CurrentActionName == "BlockReadyAction") sawBlockReady = true;
        }
        output.WriteLine($"After 3s charge: action={player.CurrentActionName}");
        Assert.True(sawBlockReady, "BlockReadyAction never became current during the underground charge.");
        Assert.Equal("BlockReadyAction", player.CurrentActionName);

        // 3) Sweep up: snap cursor to air. On the next Update, BlockReady's
        //    CheckConditions returns false → Exit arms BlockEruption →
        //    BlockEruption.Enter same frame. Hold a few frames so the FSM
        //    settles into BlockEruption and accumulates at least one path
        //    sample beyond the seed.
        bool sawBlockEruption = false;
        for (int f = 0; f < 5; f++)
        {
            ctrl.InjectInput(new PlayerInput
            {
                RightClick         = true,
                MouseWorldPosition = cursorInAir,
            });
            player.Update(ctrl, terrain, hb, hu, dt);
            PhysicsWorld.StepSwept(bodies, terrain, dt, g);

            if (player.CurrentActionName == "BlockEruptionAction") sawBlockEruption = true;
        }
        output.WriteLine($"After sweep: action={player.CurrentActionName}");
        Assert.True(sawBlockEruption, "BlockEruptionAction never became current after the sweep.");

        // 4) Release. The action's Exit runs with !RightClick → EruptionPlanner.Plan
        //    fires. ChunkMap accumulates Pending/Growing sprouts.
        int sproutsBefore = terrain.Graph.Growing.Count + terrain.Graph.Pending.Count;
        ctrl.InjectInput(new PlayerInput { MouseWorldPosition = cursorInAir });
        player.Update(ctrl, terrain, hb, hu, dt);
        PhysicsWorld.StepSwept(bodies, terrain, dt, g);

        int sproutsAfter = terrain.Graph.Growing.Count + terrain.Graph.Pending.Count;
        output.WriteLine($"Sprouts before release={sproutsBefore}, after={sproutsAfter}, action={player.CurrentActionName}");

        Assert.True(sproutsAfter > sproutsBefore,
            $"Expected EruptionPlanner.Plan to deposit sprouts on release, but Graph counts didn't grow ({sproutsBefore} → {sproutsAfter}).");
    }
}
