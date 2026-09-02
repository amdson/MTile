using System;
using System.Linq;
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

    // Number of open rows needed above a solid floor row so the floor TOP sits at or
    // just below `targetY` px, on whatever Chunk.TileSize is compiled in. Originally
    // authored (at TileSize=16) as exact row counts — 4/7/16 — chosen so the floor
    // landed exactly on 64/112/256 px; this reproduces the same targets on any grid
    // so the fall-before-landing timing this file depends on doesn't change with a
    // tile rescale.
    private static int FloorRows(float targetY) => (int)MathF.Ceiling(targetY / Chunk.TileSize);

    private static string Ground(int openRows, int width = 28)
    {
        string open  = new string('O', width);
        string solid = new string('X', width);
        return string.Join("\n", Enumerable.Repeat(open, openRows).Append(solid));
    }

    // Open rows down to a floor whose top sits at ~64 px (as it did at TileSize=16),
    // giving the same room to fall through before landing.
    private static ChunkMap FlatGround() => SimTerrain.FromAscii(Ground(FloorRows(64f)), originTileX: 0, originTileY: 0);

    private static readonly float FloorTopY = FloorRows(64f) * Chunk.TileSize;
    // Same mirrored ArcRadius as DownAirSlash (Radius · 1.5 · 1.75 · ArcRadiusScale),
    // independent of Chunk.TileSize.
    private const float ArcReach = PlayerCharacter.Radius * 1.5f * 1.75f * 1.25f;
    // Frames from the click edge to the hitbox opening: one frame for the FSM to
    // pick up the click and enter the action, then HurtboxStartSeconds (10/60s) of
    // travel at this test's Dt — both independent of the grid.
    private const int HurtboxOpenLag = 1 + 5; // 1 + (10f/60f) / Dt, Dt = 1/30

    // Displacement of a body under constant gravity `g` over `n` fixed-dt steps,
    // starting from rest (semi-implicit Euler: v += g*dt; pos += v*dt each step,
    // matching PhysicsWorld.StepSwept) — i.e. how far a falling body travels
    // before the hitbox opens.
    private static float FreefallDrop(int n) => Dt * (Gravity * Dt * n * (n + 1) / 2f);

    // The fold corrector holds a standing body at FoldHoverOffset above ground
    // contact, not "floor top minus Radius" (CORRECTOR_CONSOLIDATION_PLAN §3.1) —
    // this is the actual settled centre height, so the victim starts at rest
    // instead of drifting into position over the first several frames.
    private static readonly Vector2 VictimStart =
        new(95f, FloorTopY - PlayerCharacter.Radius - MovementConfig.Current.FoldHoverOffset);
    // Attacker starts high enough that, falling from rest, its swing apex
    // (ArcReach below the body) reaches the victim's head exactly as the hitbox
    // opens (HurtboxOpenLag frames after the ClickAfter(4, …) click below).
    private static readonly Vector2 AttackerStart =
        new(95f, VictimStart.Y - ArcReach - FreefallDrop(4 + HurtboxOpenLag));

    // Straight below the attacker — dead centre of the sextant.
    private static readonly Vector2 MouseBelow = new(95f, 300f);
    // Level with the attacker and far to the right — well outside the wedge.
    private static readonly Vector2 MouseRight = new(300f, AttackerStart.Y);

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
    // below must leave them moving upward — and by an amount inside the authored
    // [PogoSpeed, PogoMaxSpeed] band, not by whatever the recoil formula happened to
    // yield. Both ends are asserted: the floor catches a regression to the old additive
    // nudge, and the ceiling catches the clamp being bypassed.
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
        // Band is [PogoSpeed, PogoMaxSpeed] = [140, 270]. Asserting the band rather than
        // a point keeps this alive across retuning — it was [300, 520] until the bounce
        // was found to out-climb a real jump — while still failing if the bounce
        // degrades to the old additive nudge (which, against a ~150 px/s dive, left Vy
        // positive) or escapes the clamp upward.
        Assert.True(minVy <= -140f,
            $"Expected a down-air connect to pogo the attacker upward by at least PogoSpeed; observed min Vy = {minVy}");
        Assert.True(minVy >= -300f,
            $"The pogo must stay inside PogoMaxSpeed; observed min Vy = {minVy}");
    }

    // Control: the same dive into OPEN AIR — nothing to push off — never goes upward.
    // Confirms the bounce above came from the connect, not from anything incidental in
    // the fall. The ground is parked far below so the chop can't reach it either: the
    // first run of this test used the short terrain and "whiffed" straight into stone,
    // which pogoed at -345 px/s. That's the tile path, not a whiff.
    [Fact]
    public void DownAirWhiff_NoPogo()
    {
        // Open rows down to a floor at ~256 px (as at TileSize=16) — the attacker
        // starts well above it and is still ~40 px clear after all 25 frames of fall.
        var tall = SimTerrain.FromAscii(Ground(FloorRows(256f), width: 16), originTileX: 0, originTileY: 0);

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

    // ── The fall-speed bound ─────────────────────────────────────────────────────
    //
    // A deep open shaft. The victim has to be AIRBORNE for this measurement: a hit
    // aimed straight down at someone standing on the floor is absorbed by the ground
    // normal inside the same step, and the test would read ~140 px/s no matter how hard
    // the strike actually was.
    private static ChunkMap OpenShaft() => SimTerrain.FromAscii(Ground(FloorRows(112f)), originTileX: 0, originTileY: 0);

    // Δv actually applied to the victim on the frame the hit lands, for an attacker
    // entering the swing at `diveSpeed`. Measured as a velocity DELTA rather than a
    // peak so it isn't confounded by the victim's own fall; one gravity step (20 px/s
    // at this dt) rides along, which is noise against the hundreds being compared.
    //
    // Both attacker and victim free-fall from rest here (ClickAfter(0, …) clicks on
    // frame 0, so HurtboxOpenLag is the whole delay), so their shared gravity term
    // cancels: attackerY(HurtboxOpenLag) - victimY(HurtboxOpenLag) works out to
    // (attackerY0 - victimY0) + diveSpeed * HurtboxOpenLag * Dt. Solve attackerY0 so
    // that gap is -ArcReach right as the hitbox opens (the swing apex meets the
    // victim), same geometry as AttackerStart above but with the extra dive term.
    private static float AttackerY0(float victimY0, float diveSpeed)
        => victimY0 - ArcReach - diveSpeed * HurtboxOpenLag * Dt;

    private float AppliedKnockback(float diveSpeed)
    {
        const float victimY0 = 52f;
        var cfg = new SimConfigMulti
        {
            Terrain = OpenShaft(), Frames = 30, Dt = Dt, Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = new Vector2(95f, AttackerY0(victimY0, diveSpeed)),
                                StartVelocity = new Vector2(0f, diveSpeed),
                                Script = ClickAfter(0, MouseBelow) },
                new SimPlayer { StartPosition = new Vector2(95f, victimY0), Faction = Faction.Neutral,
                                Script = InputScript.Always(default) },
            },
        };

        bool hit = false, first = true;
        float dv = 0f;
        var prev = Vector2.Zero;
        SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            var v = ps[1].Body.Velocity;
            if (!hit && !first && ps[1].Combat.HitstunActive) { hit = true; dv = (v - prev).Length(); }
            prev = v; first = false;
        });

        Assert.True(hit, $"The down-air must connect for the dive-speed probe (diveSpeed = {diveSpeed}).");
        return dv;
    }

    // DownAirSlash is the only slash that swings ALONG gravity, so it is the only one
    // whose AttackDir is collinear with the attacker's own fall. Because the published
    // StrikeVelocity is `body velocity + AttackDir * StrikeSpeed` and HitResolver's
    // closing speed is a dot product against that same direction, an undamped down-air
    // let fall height leak into knockback at nearly 1:1 and without any ceiling — a
    // terminal-velocity connect measured ~1100 px/s against ~410 for a hovering one.
    //
    // StrikeBodyVelocityShare (0.3) is what bounds it. This pins BOTH halves: the dive
    // still has to matter, and it still has to be a garnish on StrikeSpeed rather than
    // the dominant term. The ratio is the assertion rather than the absolute numbers so
    // this survives retuning of StrikeSpeed, mass or restitution.
    [Fact]
    public void DiveSpeed_ScalesKnockback_ButDoesNotDominateIt()
    {
        float hover = AppliedKnockback(0f);     // ~60 px/s at the connect, barely falling
        float dive  = AppliedKnockback(400f);   // ~480 px/s at the connect, a committed drop

        output.WriteLine($"applied knockback: hover = {hover:F0}, dive = {dive:F0}, ratio = {dive / hover:F2}");

        Assert.True(dive > hover * 1.05f,
            $"A committed dive should hit harder than a hover; hover = {hover:F0}, dive = {dive:F0}.");
        // Undamped this ratio was 1.89 at only 480 px/s of fall and kept climbing with
        // height; 1.55 is comfortably above the ~1.28 the 0.3 share produces and well
        // below anything the unbounded form reaches.
        Assert.True(dive < hover * 1.55f,
            $"Fall speed must not dominate down-air knockback; hover = {hover:F0}, dive = {dive:F0}, "
            + $"ratio = {dive / hover:F2}. Check DownAirSlash.StrikeBodyVelocityShare.");
    }
}
