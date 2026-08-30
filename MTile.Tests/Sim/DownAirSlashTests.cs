using System;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// DownAirSlash — the air click aimed into the bottom sextant (60° wedge centred on
// straight down). Two things to guard:
//
//   1. The aim gate actually discriminates. A downward air click must select
//      DownAirSlash, and a sideways one must still select the ordinary AirSlash1 —
//      the whole move is worthless if it either steals every air click or never fires.
//   2. The pogo. Connecting with an entity below has to leave the attacker moving
//      UP, from a dive, in one hit. That's the part the plain additive recoil
//      couldn't do (see DownAirSlash.ApplyActionForces).
public class DownAirSlashTests(ITestOutputHelper output)
{
    private const float Dt      = 1f / 30f;
    private const float Gravity = 600f;

    // Rows 0-3 empty (room to fall through), row 4 solid ground.
    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    // Victim stands on the ground row (tiles 64..80 ⇒ feet at y=64, centre at 52).
    private static readonly Vector2 VictimStart = new(95f, 52f);
    // Attacker starts 45 px above the victim's head, falling in under gravity. The
    // arc apex sits ArcRadius (12 · 1.5 · 1.75 · 1.25 ≈ 39) below the body with a
    // ±19.7 region, so the box sweeps the victim during the active window.
    private static readonly Vector2 AttackerStart = new(95f, 7f);

    // Straight below the attacker — dead centre of the sextant.
    private static readonly Vector2 MouseBelow = new(95f, 300f);
    // Level with the attacker and far to the right — well outside the wedge.
    private static readonly Vector2 MouseRight = new(300f, 7f);

    // Idle for `delay` frames (so the fall carries the attacker into range), then one
    // click press-edge, then hold the aim. A single edge: the Click intent fires once.
    private static InputScript ClickAfter(int delay, Vector2 mouse) => new InputScript()
        .For(delay, new PlayerInput { MouseWorldPosition = mouse })
        .For(1,     new PlayerInput { LeftClick = true, MouseWorldPosition = mouse })
        .Forever(   new PlayerInput { MouseWorldPosition = mouse });

    private static SimConfigMulti Build(InputScript attacker, bool withVictim) => new SimConfigMulti
    {
        Terrain = FlatGround(),
        Frames  = 40,
        Dt      = Dt,
        Gravity = new Vector2(0f, Gravity),
        Players = withVictim
            ? new[]
              {
                  new SimPlayer { StartPosition = AttackerStart, Script = attacker },
                  new SimPlayer { StartPosition = VictimStart, Faction = Faction.Neutral,
                                  Script = InputScript.Always(default) },
              }
            : new[]
              {
                  new SimPlayer { StartPosition = AttackerStart, Script = attacker },
              },
    };

    // The gate, downward half: an air click inside the wedge picks the down-air, not
    // the stock AirSlash1 it used to collapse onto.
    [Fact]
    public void AirClickBelow_SelectsDownAirSlash()
    {
        bool sawDownAir = false, sawAirSlash1 = false;
        SimRunner.RunMulti(Build(ClickAfter(4, MouseBelow), withVictim: false),
            onFrame: (f, ps) =>
            {
                if (ps[0].CurrentActionName == "DownAirSlash") sawDownAir = true;
                if (ps[0].CurrentActionName == "AirSlash1")    sawAirSlash1 = true;
            });

        Assert.True(sawDownAir, "An air click straight below should fire DownAirSlash.");
        Assert.False(sawAirSlash1, "DownAirSlash should win the bid outright, not tie with AirSlash1.");
    }

    // The gate, sideways half: outside the wedge nothing changes — the ordinary air
    // slash still fires. This is the regression that matters, since DownAirSlash sits
    // at passive 52 and would otherwise out-bid the whole air kit.
    [Fact]
    public void AirClickSideways_StillSelectsOrdinaryAirSlash()
    {
        bool sawDownAir = false, sawAirSlash1 = false;
        SimRunner.RunMulti(Build(ClickAfter(4, MouseRight), withVictim: false),
            onFrame: (f, ps) =>
            {
                if (ps[0].CurrentActionName == "DownAirSlash") sawDownAir = true;
                if (ps[0].CurrentActionName == "AirSlash1")    sawAirSlash1 = true;
            });

        Assert.False(sawDownAir, "A level air click is outside the bottom sextant — DownAirSlash must not fire.");
        Assert.True(sawAirSlash1, "A level air click should still produce the ordinary AirSlash1.");
    }

    // The pogo. The attacker is falling the whole time; connecting with the victim
    // below must leave them moving upward — and by roughly the authored PogoSpeed
    // (300 px/s), not by whatever the recoil formula happened to yield.
    [Fact]
    public void DownAirOntoEntity_PogosAttackerUpward()
    {
        float minVy = float.MaxValue;   // y is DOWN: the most negative Vy is the peak of the bounce
        bool  connected = false;
        SimRunner.RunMulti(Build(ClickAfter(4, MouseBelow), withVictim: true),
            onFrame: (f, ps) =>
            {
                float vy = ps[0].Body.Velocity.Y;
                if (vy < minVy) minVy = vy;
                if (ps[1].Combat.HitstunActive) connected = true;
            });

        output.WriteLine($"min Vy = {minVy}, victim hitstun seen = {connected}");
        Assert.True(connected, "The down-air should have connected with the victim below.");
        // PogoSpeed is 300; assert comfortably inside it so the test survives retuning
        // but still fails if the bounce degrades to the old additive nudge (which,
        // against a ~150 px/s dive, left Vy positive).
        Assert.True(minVy <= -250f,
            $"Expected a down-air connect to pogo the attacker upward (Vy ≈ -300); observed min Vy = {minVy}");
    }

    // Control: the same dive into OPEN AIR — nothing to push off — never goes upward.
    // Confirms the bounce above came from the connect, not from anything incidental in
    // the fall. The ground is parked far below so the chop can't reach it either: the
    // first run of this test used the short terrain and "whiffed" straight into stone,
    // which pogoed at -345 px/s. That's the tile path, not a whiff.
    [Fact]
    public void DownAirWhiff_NoPogo()
    {
        // 16 empty rows (256 px) before the ground row — the attacker starts at y=7
        // and is still ~40 px above it after all 25 frames of fall.
        var tall = SimTerrain.FromAscii(@"
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

        var cfg = new SimConfigMulti
        {
            Terrain = tall,
            Frames  = 25,
            Dt      = Dt,
            Gravity = new Vector2(0f, Gravity),
            Players = new[] { new SimPlayer { StartPosition = AttackerStart,
                                              Script = ClickAfter(4, MouseBelow) } },
        };

        float minVy = float.MaxValue;
        bool  sawDownAir = false;
        SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            minVy = MathF.Min(minVy, ps[0].Body.Velocity.Y);
            if (ps[0].CurrentActionName == "DownAirSlash") sawDownAir = true;
        });

        output.WriteLine($"min Vy (whiff) = {minVy}");
        Assert.True(sawDownAir, "The whiff control must actually have fired the move.");
        Assert.True(minVy > -1f,
            $"A whiffed down-air must not bounce the attacker; observed min Vy = {minVy}");
    }
}
