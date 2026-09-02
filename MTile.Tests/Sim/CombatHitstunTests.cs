using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Multi-player combat sims. Exercises SimRunner.RunMulti by scripting a two-player
// scenario: one attacker spamming slashes, one victim holding a movement key.
public class CombatHitstunTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    // Slash hits a stationary victim → Combat.HitstunActive fires → Space presses
    // during the hitstun window do not produce JumpingState.
    [Fact]
    public void Hitstun_BlocksJumpAfterSlashLands()
    {
        // Flat ground; two players stand on top.
        //   Row 0..1: empty (sky)
        //   Row 2:    solid ground (top edge y=32)
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Place attacker at x=70, victim at x=95 (≈25 px apart) — weapon-reach geometry,
        // independent of the tile grid. Y = floor top - Radius (touching from above, so
        // both start at rest with no fall-settle needed). Floor top = row 2, scaled off
        // Chunk.TileSize.
        float floorTopY = 2 * Chunk.TileSize;
        var attackerStart = new Vector2(70f, floorTopY - PlayerCharacter.Radius);
        var victimStart   = new Vector2(95f, floorTopY - PlayerCharacter.Radius);
        // Mouse far to the right of the attacker so SlashLikeAction.ComputeSlashDir
        // returns (+1, 0). PlayerCharacter.Facing defaults to +1 — no input needed
        // to orient the attacker.
        var mouseAhead    = new Vector2(200f, 28f);

        // Attacker: 13-frame settle, then one click (1 frame LMB-down at frame 13).
        // Both players now start already touching the floor (no fall-settle needed,
        // per the Y formula above), which lands the slash a couple ticks sooner than
        // the old fall-then-land rig this test was originally timed against — trimmed
        // here so the hit still lands (hitstun first true at frame 16) strictly BEFORE
        // the victim's first jump-press edge below. Gating only works one-directional:
        // HitstunActive read at frame N reflects the hit resolved during frame N-1, so
        // a jump pressed the very same frame the hit lands is not yet gated — the hit
        // must land at least one frame ahead of the press to prove the gate.
        var attackerScript = new InputScript()
            .For   (13, new PlayerInput { MouseWorldPosition = mouseAhead })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead })
            .Forever   (new PlayerInput { MouseWorldPosition = mouseAhead });

        // Victim: idle until frame 17, then pulse Space (1f on, 3f off) repeatedly.
        // Hitstun is already active by frame 16 (see above), so every Space
        // press-edge below raises JumpJustPressed while HitstunActive is true.
        // Without the hitstun gate, victim would enter JumpingState. With the
        // gate, they stay grounded.
        var jumpPressed = new PlayerInput { Space = true };
        var noInput = default(PlayerInput);
        var victimScript = new InputScript()
            .For(17, noInput)
            .For(1, jumpPressed).For(3, noInput)
            .For(1, jumpPressed).For(3, noInput)
            .For(1, jumpPressed).For(3, noInput)
            .For(1, jumpPressed).For(3, noInput)
            .For(1, jumpPressed).For(3, noInput)
            .Forever(noInput);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 60,
            Dt      = Dt,
            Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
                // Tag the victim Neutral so CombatSystem's same-faction filter
                // doesn't skip the attacker's Player-faction slash hitbox.
                new SimPlayer { StartPosition = victimStart,   Script = victimScript,
                                Faction = Faction.Neutral },
            },
        };

        var hitstunPerFrame = new List<bool>(cfg.Frames);
        bool sawHitstun = false;
        float? finalVictimDamage = null;

        var traces = SimRunner.RunMulti(cfg,
            onFrame: (f, ps) =>
            {
                bool h = ps[1].Combat.HitstunActive;
                hitstunPerFrame.Add(h);
                if (h) sawHitstun = true;
            },
            outPlayers: ps => finalVictimDamage = ps[1].Combat.DamageTaken);

        SimReport.WriteCsv(traces[0], "hitstun_attacker", outputDir: null);
        SimReport.WriteCsv(traces[1], "hitstun_victim",   outputDir: null);

        // Diagnostic: dump the frame where hitstun first fired and the victim's
        // state at that frame. Helps when retuning slash timing breaks the test.
        for (int f = 0; f < hitstunPerFrame.Count; f++)
        {
            if (!hitstunPerFrame[f]) continue;
            output.WriteLine($"first hitstun at frame {f}, victim state={traces[1][f].State}, damage={finalVictimDamage}");
            break;
        }

        // 1) The slash must actually land. Direct hits no longer chip HP (Phase 5) —
        //    connection is observed via the victim's escalation damage rising.
        Assert.NotNull(finalVictimDamage);
        Assert.True(finalVictimDamage > 0f,
            $"Slash never connected — victim damage stayed at {finalVictimDamage}. Check geometry / mouse direction.");

        // 2) Hitstun must have fired at some point.
        Assert.True(sawHitstun, "Expected Combat.HitstunActive to be true at some frame after the slash landed.");

        // 3) During every hitstun frame, the victim must not be in a jump state.
        //    (CoveredJumpState is now gated by the capability mask too, but it
        //    can't fire on flat ground without an overhead ceiling anyway, so the
        //    "Jumping"-name check below remains sufficient here.)
        for (int f = 0; f < traces[1].Length; f++)
        {
            if (!hitstunPerFrame[f]) continue;
            Assert.False(IsJumpingState(traces[1][f].State),
                $"Frame {f}: victim entered {traces[1][f].State} while HitstunActive — hitstun gate failed.");
        }
    }

    // Sanity: with NO attacker contact, the same victim script DOES produce a
    // jump. Confirms the test rig isn't accidentally suppressing jumps for some
    // unrelated reason — without this, "no JumpingState during hitstun" would
    // be vacuously true if jumps never fire under any conditions.
    [Fact]
    public void Hitstun_Sanity_VictimJumpsWhenNotHit()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Same victim script, same starting layout — but the "attacker" never
        // clicks. Victim should land jumps as soon as Space edges occur after
        // settling.
        var attackerStart = new Vector2(70f, 20f);
        var victimStart   = new Vector2(95f, 20f);

        var attackerScript = InputScript.Always(default);
        var jumpHeld = new PlayerInput { Right = true, Space = true };
        var rightOnly = new PlayerInput { Right = true };
        var victimScript = new InputScript()
            .For(10, default(PlayerInput))
            .For(1, jumpHeld).For(3, rightOnly)
            .For(1, jumpHeld).For(3, rightOnly)
            .Forever(rightOnly);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 30,
            Dt      = Dt,
            Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
                new SimPlayer { StartPosition = victimStart,   Script = victimScript   },
            },
        };

        var traces = SimRunner.RunMulti(cfg);

        bool sawJump = false;
        foreach (var sf in traces[1])
            if (IsJumpingState(sf.State)) { sawJump = true; break; }

        Assert.True(sawJump, "Sanity check failed: victim never jumped even without being hit.");
    }

    private static bool IsJumpingState(string name) =>
        name == "JumpingState"
        || name == "RunningJumpState"
        || name == "DoubleJumpingState"
        || name == "WallJumpingState";

    // A high-knockback hit (Stab, KnockbackMagnitude=380) crosses CombatState's
    // StunImpulseThreshold (350) and flips StunActive — verified via the victim
    // entering StunnedState. The complementary case (Slash1, knockback 60, below
    // threshold) is already covered by Hitstun_BlocksJumpAfterSlashLands which
    // sees the victim stay in StandingState (only hitstun fires, not stun).
    [Fact]
    public void Stun_StabHit_PutsVictimInStunnedState()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var attackerStart = new Vector2(70f, 20f);
        var victimStart   = new Vector2(95f, 20f);
        // Mouse swipe: press at x=120, release at x=180. Hold for 8 frames (>6)
        // with 60px horizontal swipe (>12) → InputParser emits Stab intent on
        // release. StabAction.Enter consumes it; the active hitbox window lands
        // on the still-stationary victim.
        var pressMouse   = new Vector2(120f, 28f);
        var releaseMouse = new Vector2(180f, 28f);

        var attackerScript = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = pressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = pressMouse })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = releaseMouse })
            .Forever   (new PlayerInput { MouseWorldPosition = releaseMouse });

        // Victim idle the whole sim — we just want to observe the state machine
        // post-hit; no input interference.
        var victimScript = InputScript.Always(default);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 60,
            Dt      = Dt,
            Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
                new SimPlayer { StartPosition = victimStart,   Script = victimScript,
                                Faction = Faction.Neutral },
            },
        };

        bool sawStunActive   = false;
        bool sawStunnedState = false;
        bool sawHitstun      = false;

        var traces = SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            if (ps[1].Combat.StunActive)    sawStunActive = true;
            if (ps[1].Combat.HitstunActive) sawHitstun   = true;
            if (ps[1].CurrentStateName == "StunnedState") sawStunnedState = true;
        });

        Assert.True(sawHitstun,      "Stab should have landed and set HitstunActive.");
        Assert.True(sawStunActive,   "Stab knockback (380) > StunImpulseThreshold (350) — StunActive should fire.");
        Assert.True(sawStunnedState, "Victim should have entered StunnedState while stunned.");
    }

    // Single-player crush test: drop the player from height onto solid ground.
    // Free-fall velocity at impact exceeds CrushImpulseThreshold (700 px/s, vs
    // ~270 for a max-hold self-jump landing and ≤580 for sand impacts), so HP
    // should drop. A normal short fall (no excess) leaves HP at MaxHealth.
    [Fact]
    public void Crush_HardFallDealsDamage()
    {
        // Tall column of empty above flat ground. Need vy > 700 at impact for
        // crush damage to fire; from rest under gravity 600, that's ~408 px of
        // free fall. Use 32 rows of empty above the floor to give plenty of
        // headroom (~480 px drop ⇒ vy ≈ 760 at impact).
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
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Floor top = row 32 (Chunk.TileSize-scaled). Drop the same ~492px fall used before the
        // grid change (impact vy depends only on fall height and gravity, both grid-independent) —
        // impact vy ≈ 770, comfortably past the 700 CrushImpulseThreshold.
        float floorTopY = 32 * Chunk.TileSize;
        var cfg = new SimConfig
        {
            Terrain       = terrain,
            StartPosition = new Vector2(120f, floorTopY - 492f),
            Script        = InputScript.Always(default),
            Frames        = 60,
            Dt            = Dt,
            Gravity       = new Vector2(0f, Gravity),
        };

        var player = new PlayerCharacter(cfg.StartPosition);
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl   = new Controller();
        var hb     = new HitboxWorld();
        var hu     = new HurtboxWorld();
        float startHealth = player.Health;
        float minHealth   = startHealth;

        for (int f = 0; f < cfg.Frames; f++)
        {
            ctrl.InjectInput(cfg.Script.Get(f, null));
            cfg.Terrain.TickSprouts(cfg.Dt);
            player.Update(ctrl, cfg.Terrain, hb, hu, cfg.Dt);
            PhysicsWorld.StepSwept(bodies, cfg.Terrain, cfg.Dt, cfg.Gravity);
            if (player.Health < minHealth) minHealth = player.Health;
        }

        Assert.True(minHealth < startHealth,
            $"Hard fall should chip HP via crush damage. startHealth={startHealth} minHealth={minHealth}");
    }

    // Negative case: a Slash1 (KnockbackMagnitude=200) is well under the stun
    // threshold (350). Should set HitstunActive (gates jump) but NOT StunActive.
    [Fact]
    public void Stun_Slash1_DoesNotStun()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var attackerStart = new Vector2(70f, 20f);
        var victimStart   = new Vector2(95f, 20f);
        var mouseAhead    = new Vector2(200f, 28f);

        var attackerScript = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = mouseAhead })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead })
            .Forever   (new PlayerInput { MouseWorldPosition = mouseAhead });
        var victimScript = InputScript.Always(default);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 30,
            Dt      = Dt,
            Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
                new SimPlayer { StartPosition = victimStart,   Script = victimScript,
                                Faction = Faction.Neutral },
            },
        };

        bool sawStun    = false;
        bool sawHitstun = false;
        var traces = SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            if (ps[1].Combat.StunActive)    sawStun    = true;
            if (ps[1].Combat.HitstunActive) sawHitstun = true;
        });

        Assert.True (sawHitstun, "Slash1 should still set hitstun.");
        Assert.False(sawStun,    "Slash1 knockback (200) is below stun threshold (350) — must not stun.");
    }
}
