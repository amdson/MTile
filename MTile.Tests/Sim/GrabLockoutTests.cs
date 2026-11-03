using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Regression pins for two grab bugs found playing the bot (2026-11-03):
//
//  1. A grabbed player could still start actions whose preconditions forgot the
//     per-action BlocksAttack check (Pulse, BlockPaint/Place, BlockGrab). The gate is
//     now central (ActionState.AllowedWhileGrabbed, enforced by PlayerCharacter's
//     selection loop and RecoveryAction's lookahead): while held, only the struggle
//     (GrabbedSlash) and the neutral/recovery states can be entered.
//  2. The latch box was a ±28.8 px square 19 px ahead — it caught a body ~54 px away,
//     as far as a Slash 1 reaches, ~41 px above/below, and from behind. It is now body
//     contact plus a little (a victim centre within ~37 px ahead).
public class GrabLockoutTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly float RestY = 2f * Chunk.TileSize - PlayerCharacter.Radius;
    private static readonly Vector2 AttackerStart = new(70f, RestY);
    private static readonly Vector2 MouseRight    = new(250f, RestY);

    // Settle, then press-and-hold Shift+LMB aimed right: the grab fires at frame 10,
    // latches at the end of its 20f startup and holds while LMB stays down.
    private static InputScript GrabHold() => new InputScript()
        .For(10, new PlayerInput { MouseWorldPosition = MouseRight })
        .Forever(new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = MouseRight });

    private static SimConfigMulti Build(InputScript attacker, InputScript victim, float victimDx,
                                        float victimDy = 0f, int frames = 70) => new SimConfigMulti
    {
        Terrain = FlatGround(),
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[]
        {
            new SimPlayer { StartPosition = AttackerStart, Script = attacker },
            new SimPlayer { StartPosition = AttackerStart + new Vector2(victimDx, victimDy),
                            Script = victim, Faction = Faction.Neutral },
        },
    };

    // ---- 1. Lock-out ---------------------------------------------------------------

    private static readonly HashSet<string> AllowedWhileGrabbed =
        new() { nameof(NullAction), nameof(RecoveryAction), nameof(GrabbedSlash) };

    // The victim idles through the grab's windup (latched at ~frame 30), then, while
    // held, tries everything: a held RMB (BlockPaintAction — one of the actions with no
    // grab check of its own), an LMB hold-swipe-release (the stab/charge gesture), and a
    // Shift+RMB (BlockPlace). Whatever it ENTERS while flagged grabbed must be in the
    // allowed set.
    private static InputScript VictimTriesEverything()
    {
        var left = new Vector2(0f, RestY);
        var s = new InputScript()
            .For(34, default(PlayerInput))
            .For(12, new PlayerInput { RightClick = true, MouseWorldPosition = left })
            .For(2,  new PlayerInput { MouseWorldPosition = left });
        // Hold LMB, then swipe it 40 px while held, then release: the Stab gesture.
        var m = left;
        s.For(15, new PlayerInput { LeftClick = true, MouseWorldPosition = m });
        for (int i = 0; i < 4; i++)
        {
            m.Y -= 10f;
            s.For(1, new PlayerInput { LeftClick = true, MouseWorldPosition = m });
        }
        s.For(3, new PlayerInput { MouseWorldPosition = m });
        s.For(8, new PlayerInput { Shift = true, RightClick = true, MouseWorldPosition = left });
        return s.Forever(new PlayerInput { MouseWorldPosition = left });
    }

    [Fact]
    public void Grabbed_Victim_CannotStartNonStruggleActions()
    {
        var entered = new List<string>();
        bool sawGrabbed = false;
        string prev = null;
        SimRunner.RunMulti(Build(GrabHold(), VictimTriesEverything(), victimDx: 25f, frames: 110),
            onFrame: (f, ps) =>
            {
                var v = ps[1];
                string cur = v.CurrentActionName;
                if (v.Combat.GrabbedActive)
                {
                    sawGrabbed = true;
                    if (cur != prev) entered.Add($"{f}:{cur}");
                    Assert.True(AllowedWhileGrabbed.Contains(cur),
                        $"frame {f}: grabbed victim entered {cur} (only the struggle may fire while held)");
                }
                prev = cur;
            });
        output.WriteLine("entered while grabbed: " + string.Join(", ", entered));
        Assert.True(sawGrabbed, "The victim should have been grabbed.");
    }

    // Control for the test above: with nobody grabbing, the same held RMB DOES start
    // BlockPaintAction — so the lock-out, not the script, is what kept it out.
    [Fact]
    public void Ungrabbed_SameInput_StartsBlockPaint()
    {
        bool sawPaint = false;
        SimRunner.RunMulti(Build(InputScript.Always(default), VictimTriesEverything(), victimDx: 25f, frames: 110),
            onFrame: (f, ps) => { if (ps[1].CurrentActionName == nameof(BlockPaintAction)) sawPaint = true; });
        Assert.True(sawPaint, "Control: an ungrabbed player holding RMB should enter BlockPaintAction.");
    }

    // ...while the struggle still fires: a grabbed victim tapping LMB enters GrabbedSlash.
    [Fact]
    public void Grabbed_Victim_CanStillStruggle()
    {
        var mouseLeft = new Vector2(0f, 20f);
        var click   = new PlayerInput { Left = true, LeftClick = true, MouseWorldPosition = mouseLeft };
        var noClick = new PlayerInput { Left = true, MouseWorldPosition = mouseLeft };
        var victim = new InputScript().For(34, default(PlayerInput));
        for (int i = 0; i < 4; i++) victim.For(1, click).For(3, noClick);
        victim.Forever(noClick);

        bool sawStruggle = false;
        SimRunner.RunMulti(Build(GrabHold(), victim, victimDx: 25f),
            onFrame: (f, ps) => { if (ps[1].CurrentActionName == nameof(GrabbedSlash)) sawStruggle = true; });
        Assert.True(sawStruggle, "A grabbed victim must still be able to fire the struggle slash.");
    }

    // ---- 2. Reach --------------------------------------------------------------------

    // Slash 1's far reach from the body centre (the hit shape's tip at 1.5 × ArcRadius).
    private static readonly float Slash1Reach = SlashLikeAction.BaseArcRadius * 1.5f;

    private bool Grabs(float dx, float dy = 0f)
    {
        bool grabbed = false;
        SimRunner.RunMulti(Build(GrabHold(), InputScript.Always(default), dx, dy),
            onFrame: (f, ps) => { if (ps[1].Combat.GrabbedActive) grabbed = true; });
        output.WriteLine($"victim at dx={dx:F1} dy={dy:F1}: grabbed={grabbed}");
        return grabbed;
    }

    [Fact]
    public void Grab_CatchesAtCloseRange()
    {
        Assert.True(Grabs(16f), "A grab should catch a body standing right against the grabber.");
        Assert.True(Grabs(30f), "A grab should catch a body a short step away (~30 px centre to centre).");
    }

    [Fact]
    public void Grab_MissesBeyondShortRange()
    {
        output.WriteLine($"Slash 1 reach = {Slash1Reach:F1} px");
        // At Slash 1's own reach and beyond: the old box (±28.8 px square, 19 px ahead)
        // latched these.
        Assert.False(Grabs(Slash1Reach), $"A grab must not reach as far as a Slash 1 ({Slash1Reach:F1} px).");
        Assert.False(Grabs(2f * Slash1Reach), $"A grab must not reach 2× Slash 1 ({2f * Slash1Reach:F1} px).");
        // Behind the grabber.
        Assert.False(Grabs(-20f), "A grab must not catch a body behind the grabber.");
    }
}
