using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// COMBAT_FEEL_PLAN Phases 1-2 regression tests.
//
// Phase 1 — hits displace: during combat hitstun the victim's self-control is
// muted (modifier scalars) and the speed-cap brakes are skipped
// (PreserveExternalVelocity), so knockback carries the body even while the
// victim holds a direction.
//
// Phase 2 — holding fields: GroundSlash1/2 broadcast a stateless ForceField each
// frame that servo-pulls the victim toward the arc focus, keeping them in range
// for the combo follow-up instead of shoving them out of it.
public class CombatFeelTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private const float Gravity = 600f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    // A stab (impulse 380, control-muting hitstun ~0.5 s) launches the victim
    // rightward. The victim holds LEFT (into the knockback) the whole time —
    // pre-Phase-1 the walk-speed brake + full accel erased the knockback within
    // a couple of frames; now the body must still displace meaningfully.
    [Fact]
    public void Hitstun_KnockbackDisplaces_DespiteHoldingAgainstIt()
    {
        var attackerStart = new Vector2(70f, 20f);
        var victimStart   = new Vector2(95f, 20f);
        // Press at 120, hold 8 frames (> click window), release at 180 → Stab.
        var pressMouse   = new Vector2(120f, 28f);
        var releaseMouse = new Vector2(180f, 28f);

        var attackerScript = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = pressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = pressMouse })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = releaseMouse })
            .Forever   (new PlayerInput { MouseWorldPosition = releaseMouse });

        // Victim idles in range through the stab's active window (stab enters
        // frame 24, hitbox opens ~frame 28), then fights the rightward knockback
        // by holding Left for the rest of the sim. Idling through the hit keeps
        // the pre-hit velocity at zero so the measurement is purely "impulse vs.
        // the victim's counter-input during hitstun".
        var victimScript = new InputScript()
            .For(29, default(PlayerInput))
            .Forever(new PlayerInput { Left = true });

        var cfg = new SimConfigMulti
        {
            Terrain = FlatGround(),
            Frames  = 70,
            Dt      = Dt,
            Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
                new SimPlayer { StartPosition = victimStart,   Script = victimScript,
                                Faction = Faction.Neutral },
            },
        };

        int firstHitstunFrame = -1;
        var traces = SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            if (firstHitstunFrame < 0 && ps[1].Combat.HitstunActive) firstHitstunFrame = f;
        });

        Assert.True(firstHitstunFrame > 0, "Stab never connected — no hitstun observed.");

        // Displacement is measured from the position when the hit registered —
        // everything after that is knockback vs. the victim's held-Left input.
        float baseX = traces[1][firstHitstunFrame - 1].X;
        float maxX = baseX;
        for (int f = firstHitstunFrame; f < Math.Min(firstHitstunFrame + 20, traces[1].Length); f++)
            maxX = MathF.Max(maxX, traces[1][f].X);
        output.WriteLine($"hit at frame {firstHitstunFrame}, base X={baseX:F1}, max X={maxX:F1}");

        Assert.True(maxX >= baseX + 8f,
            $"Knockback was erased by the victim's own input — max displacement only {maxX - baseX:F1} px. " +
            "Hitstun control-mute / PreserveExternalVelocity not carrying the impulse.");
    }

    // GroundSlash1's holding field: the victim is hit at slash range and then
    // holds RIGHT (away from the attacker) — through the slash + recovery
    // continuation the field must pull them back toward the arc focus, so by the
    // end of the hold window they sit NO FURTHER than where they started.
    [Fact]
    public void HoldField_Slash1_KeepsVictimInRange_DespiteWalkingAway()
    {
        var attackerStart = new Vector2(70f, 20f);
        var victimStart   = new Vector2(95f, 20f);
        var mouseAhead    = new Vector2(200f, 28f);

        // S1 enters on the release at frame 16; hitbox active ~frames 16-17;
        // hold field broadcasts for the whole slash (~4 frames) + the recovery
        // continuation (~3 frames).
        var attackerScript = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = mouseAhead })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead })
            .Forever   (new PlayerInput { MouseWorldPosition = mouseAhead });

        // Victim stands still until just after the hit, then tries to escape
        // rightward for the rest of the sim.
        var victimScript = new InputScript()
            .For(18, default(PlayerInput))
            .Forever(new PlayerInput { Right = true });

        var cfg = new SimConfigMulti
        {
            Terrain = FlatGround(),
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

        bool sawHitstun = false;
        var traces = SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            if (ps[1].Combat.HitstunActive) sawHitstun = true;
        });

        Assert.True(sawHitstun, "Slash never connected — no hitstun observed.");

        // End of the hold window (slash ends ~frame 20, recovery continuation
        // ~frame 23): despite holding Right since frame 18, the victim must not
        // have gained ground. (Without the field they'd be ~5-8 px right by now;
        // with it they're pulled toward the focus in front of the attacker.)
        float xAtHoldEnd = traces[1][24].X;
        output.WriteLine($"victim X at frame 24 = {xAtHoldEnd:F1} (start {victimStart.X})");
        Assert.True(xAtHoldEnd <= victimStart.X + 1f,
            $"Victim escaped the S1 hold — X={xAtHoldEnd:F1} (started {victimStart.X}). " +
            "Holding field not pulling them into the combo.");

        // Sanity: the hold is temporary. Once the field + hitstun lapse, the
        // victim walking right must actually get away — no permanent suction.
        float xLate = traces[1][^1].X;
        output.WriteLine($"victim X at end = {xLate:F1}");
        Assert.True(xLate > victimStart.X + 10f,
            $"Victim never escaped after the hold ended (X={xLate:F1}) — field outliving its action?");
    }
}
