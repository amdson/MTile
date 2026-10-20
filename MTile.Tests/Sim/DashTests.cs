using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// DashState — the Shift+Space chord while airborne (or in a jump state) throws the body
// toward the cursor, once per airtime. Shift alone is Guard, which is ground-only.
// Guards:
//
//   1. An air chord dashes, along body → cursor, and covers real distance.
//   2. Once per airtime: a second chord before touching down does nothing.
//   3. Mid-air the chord replaces the double jump instead of spending it; Space alone
//      still double-jumps.
//   3b. Shift alone guards on the ground and does nothing in the air.
//   4. The chord on the ground jumps and dashes off the take-off frame.
//   5. Landing hands the dash back.
//   6. Space under a held ground guard jumps and dashes.
//   7. A click mid-dash cuts into DashSlash, which keeps the momentum and pays a long
//      tail; a click outside a dash is still an ordinary air slash. With Shift still
//      held from the chord, the press is still DashSlash's, not Grab's / BlockGrab's.
//   8. The slice runs on top of the dash (a movement state) and never shortens it.
public class DashTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private static ChunkMap Ground(float height)
    {
        string open = new string('O', 60), solid = new string('X', 60);
        int rows = (int)MathF.Ceiling(height / Chunk.TileSize);
        return SimTerrain.FromAscii(string.Join("\n", Enumerable.Repeat(open, rows).Append(solid)),
                                    originTileX: 0, originTileY: 0);
    }

    private static readonly Vector2 AirStart = new(200f, 40f);

    private record Trace(List<string> Actions, List<string> States, List<Vector2> Pos, List<Vector2> Vel)
    {
        public int Count(string action) => Runs(Actions, action);
        public int First(string action) => Actions.IndexOf(action);
        public int Dashes => Runs(States, nameof(DashState));
        public int FirstDash => States.IndexOf(nameof(DashState));
        public int DashFrames => States.Count(s => s == nameof(DashState));
        private static int Runs(List<string> xs, string x)
            => xs.Where((a, i) => a == x && (i == 0 || xs[i - 1] != x)).Count();
    }

    private static Trace Run(ChunkMap terrain, Vector2 start, InputScript script, int frames)
    {
        var t = new Trace(new(), new(), new(), new());
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain, Frames = frames, Dt = Dt, Gravity = new Vector2(0f, 600f),
            Players = new[] { new SimPlayer { StartPosition = start, Script = script } },
        }, onFrame: (f, ps) =>
        {
            t.Actions.Add(ps[0].CurrentActionName);
            t.States.Add(ps[0].CurrentStateName);
            t.Pos.Add(ps[0].Body.Position);
            t.Vel.Add(ps[0].Body.Velocity);
        });
        return t;
    }

    private static PlayerInput Chord(Vector2 mouse) => new() { Shift = true, Space = true, MouseWorldPosition = mouse };

    [Fact]
    public void AirChord_DashesTowardTheCursor()
    {
        var mouse = AirStart + new Vector2(300f, -100f);   // right and a little up
        var script = new InputScript()
            .For(3, new PlayerInput { MouseWorldPosition = mouse })
            .Forever(Chord(mouse));
        var t = Run(Ground(600f), AirStart, script, 40);

        int f = t.FirstDash;
        output.WriteLine($"dash@{f} vel={t.Vel[f]}");
        Assert.True(f >= 0, "An airborne Shift+Space should enter DashState.");
        Assert.DoesNotContain(nameof(DoubleJumpingState), t.States);

        var dir = Vector2.Normalize(t.Vel[f]);
        var aim = Vector2.Normalize(mouse - t.Pos[Math.Max(0, f - 1)]);
        Assert.True(Vector2.Dot(dir, aim) > 0.98f, $"dash velocity {t.Vel[f]} should point at the cursor");

        float travel = t.Pos[f + 9].X - t.Pos[f].X;
        Assert.True(travel > 40f, $"the burst should carry the body well forward (moved {travel:0.0} px)");
        Assert.True(t.Pos[f + 9].Y < t.Pos[f].Y, "an up-right dash should rise, not fall, during the burst");
    }

    [Fact]
    public void SecondChordInTheSameAirtime_DoesNothing()
    {
        var mouse = AirStart + new Vector2(300f, 0f);
        var script = new InputScript()
            .For(3,  new PlayerInput { MouseWorldPosition = mouse })
            .For(3,  Chord(mouse))
            .For(25, new PlayerInput { MouseWorldPosition = mouse })
            .For(3,  Chord(mouse))
            .Forever(new PlayerInput { MouseWorldPosition = mouse });
        var t = Run(Ground(600f), AirStart, script, 60);

        Assert.Equal(1, t.Dashes);
    }

    [Fact]
    public void ShiftAlone_GuardsOnTheGround_AndNeverInTheAir()
    {
        var shift = new PlayerInput { Shift = true, MouseWorldPosition = new Vector2(400f, 40f) };
        var ground = Run(Ground(64f), new Vector2(200f, 40f), new InputScript()
            .For(40, default(PlayerInput)).Forever(shift), 70);
        Assert.Contains(nameof(GuardAction), ground.Actions);
        Assert.DoesNotContain(nameof(DashState), ground.States);

        var air = Run(Ground(600f), AirStart, new InputScript()
            .For(3, default(PlayerInput)).Forever(shift), 50);
        Assert.DoesNotContain(nameof(GuardAction), air.Actions);
        Assert.DoesNotContain(nameof(DashState), air.States);
    }

    [Fact]
    public void AirSpaceAlone_StillDoubleJumps_AndTheChordDoesNotSpendIt()
    {
        var mouse = AirStart + new Vector2(300f, 0f);
        var idle  = new PlayerInput { MouseWorldPosition = mouse };
        var space = new PlayerInput { Space = true, MouseWorldPosition = mouse };

        var plain = Run(Ground(600f), AirStart, new InputScript()
            .For(3, idle).For(6, space).Forever(idle), 30);
        Assert.Contains(nameof(DoubleJumpingState), plain.States);
        Assert.DoesNotContain(nameof(DashState), plain.States);

        // Chord, let go of both, then Space alone: dash first, double jump still banked.
        var both = Run(Ground(600f), AirStart, new InputScript()
            .For(3, idle).For(4, Chord(mouse)).For(20, idle).For(6, space).Forever(idle), 50);
        int dash = both.FirstDash, dj = both.States.IndexOf(nameof(DoubleJumpingState));
        output.WriteLine($"dash@{dash} doublejump@{dj}");
        Assert.True(dash >= 0 && dj > dash + 20, "the double jump should wait for its own Space press");
    }

    [Fact]
    public void SpaceUnderAHeldGroundGuard_JumpsAndDashes()
    {
        var mouse = new Vector2(500f, 40f);
        var t = Run(Ground(64f), new Vector2(200f, 40f), new InputScript()
            .For(40, new PlayerInput { MouseWorldPosition = mouse })
            .For(10, new PlayerInput { Shift = true, MouseWorldPosition = mouse })
            .Forever(Chord(mouse)), 70);
        int guard = t.First(nameof(GuardAction)), dash = t.FirstDash;
        output.WriteLine($"guard@{guard} dash@{dash}");
        Assert.True(guard >= 0 && dash >= 50 && dash <= 52, "Space under a held ground guard should jump and dash at once");
        Assert.DoesNotContain(nameof(GuardAction), t.Actions.Skip(dash));
    }

    [Fact]
    public void ShiftWithTheJumpPress_DashesOffTheTakeoff()
    {
        var mouse = new Vector2(500f, 40f);   // level with the body: the take-off lifts it
        var script = new InputScript()
            .For(40, new PlayerInput { MouseWorldPosition = mouse })
            .For(1,  new PlayerInput { Space = true, Shift = true, MouseWorldPosition = mouse })
            .Forever(new PlayerInput { Space = true, Shift = true, MouseWorldPosition = mouse });
        var t = Run(Ground(64f), new Vector2(200f, 40f), script, 80);

        int f = t.FirstDash;
        output.WriteLine($"dash@{f} state={(f >= 0 ? t.States[f] : "-")}");
        Assert.True(f >= 40 && f <= 44, $"Shift with the jump should dash at take-off (@{f}).");
        Assert.True(t.Vel[f].X > 200f, "the dash should throw the body toward the cursor");
        Assert.True(t.Vel[f].Y < 0f, "a take-off dash must not aim into the floor");
    }

    [Fact]
    public void Landing_HandsTheDashBack()
    {
        var mouse = new Vector2(500f, 0f);
        var idle = new PlayerInput { MouseWorldPosition = mouse };
        var script = new InputScript()
            .For(40, idle)
            .For(4, Chord(mouse)).For(60, idle)    // dash #1 off the take-off, then land
            .For(4, Chord(mouse))                  // dash #2
            .Forever(idle);
        var t = Run(Ground(64f), new Vector2(150f, 40f), script, 160);

        Assert.Equal(2, t.Dashes);
    }

    [Fact]
    public void ClickDuringDash_Slices_ThenPaysTheTail()
    {
        var mouse = AirStart + new Vector2(300f, 0f);
        var idle  = new PlayerInput { MouseWorldPosition = mouse };
        var click = new PlayerInput { LeftClick = true, MouseWorldPosition = mouse };
        var t = Run(Ground(600f), AirStart, new InputScript()
            .For(3, idle).For(1, Chord(mouse)).For(2, idle)
            .For(2, click).For(2, idle)            // released on f8: Click intent mid-dash
            .For(30, idle)
            .For(2, click).Forever(idle), 70);     // a click inside the tail

        int dash = t.FirstDash, cut = t.First(nameof(DashSlash));
        output.WriteLine($"dash@{dash} cut@{cut} vel@cut={(cut >= 0 ? t.Vel[cut] : default)}");
        Assert.True(cut > dash && cut <= dash + 8, "a click mid-dash should cut straight into DashSlash");
        Assert.True(t.Vel[cut].X > 150f, "the slice should keep the dash's momentum");

        int end = t.Actions.FindIndex(cut, a => a != nameof(DashSlash));
        Assert.Equal(nameof(RecoveryAction), t.Actions[end]);
        // The second click (f40) lands inside the ~33f tail: nothing fires before it ends.
        Assert.DoesNotContain(t.Actions.Skip(end).Take(25), a => a.Contains("Slash"));
    }

    [Fact]
    public void ClickWithoutADash_IsAnOrdinaryAirSlash()
    {
        var mouse = AirStart + new Vector2(300f, 0f);
        var t = Run(Ground(600f), AirStart, new InputScript()
            .For(3, new PlayerInput { MouseWorldPosition = mouse })
            .For(2, new PlayerInput { LeftClick = true, MouseWorldPosition = mouse })
            .Forever(new PlayerInput { MouseWorldPosition = mouse }), 30);
        Assert.Contains(nameof(AirSlash1), t.Actions);
        Assert.DoesNotContain(nameof(DashSlash), t.Actions);
    }

    [Fact]
    public void ZzzProbe_ClickWithShiftStillHeld()
    {
        var mouse = AirStart + new Vector2(300f, 0f);
        var held  = new PlayerInput { Shift = true, Space = true, MouseWorldPosition = mouse };
        var click = new PlayerInput { Shift = true, Space = true, LeftClick = true, MouseWorldPosition = mouse };
        var t = Run(Ground(600f), AirStart, new InputScript()
            .For(3, new PlayerInput { MouseWorldPosition = mouse }).For(3, held)
            .For(2, click).For(20, held).Forever(new PlayerInput { MouseWorldPosition = mouse }), 30);
        for (int f = 0; f < 30; f++) output.WriteLine($"{f}: {t.Actions[f]}");
    }

    // Shift is still down from the chord when the player clicks — that Shift+LMB press
    // must cut the dash into DashSlash, not get the dash evicted for Grab / BlockGrab.
    // The mouse sits on stone so BlockGrab's terrain chord is live too.
    [Fact]
    public void ClickWithShiftStillHeld_IsTheDashSlash_NotAGrab()
    {
        var floor = new Vector2(AirStart.X + 40f, 610f);   // inside the floor row (y 605-616)
        var held  = new PlayerInput { Shift = true, Space = true, MouseWorldPosition = floor };
        var click = held with { LeftClick = true };
        var t = Run(Ground(600f), AirStart, new InputScript()
            .For(3, new PlayerInput { MouseWorldPosition = floor }).For(3, held)
            .For(15, click).Forever(held), 40);

        int dash = t.FirstDash, cut = t.First(nameof(DashSlash));
        output.WriteLine($"dash@{dash} cut@{cut} :: {string.Join(" ", t.Actions.Skip(dash).Take(12))}");
        Assert.True(cut == dash + 3, $"the press (f6) should cut straight into DashSlash (@{cut})");
        Assert.DoesNotContain(nameof(GrabAction), t.Actions);
        Assert.DoesNotContain(nameof(BlockGrabAction), t.Actions);
    }

    // The slice is an action on top of the dash: the burst must cover the same ground and
    // the dash last just as long whether or not the player cuts during it.
    [Fact]
    public void SliceDoesNotShortenTheDash()
    {
        var mouse = AirStart + new Vector2(300f, 0f);
        var idle  = new PlayerInput { MouseWorldPosition = mouse };
        InputScript Script(bool slice) => new InputScript()
            .For(3, idle).For(1, Chord(mouse)).For(2, idle)
            .For(2, slice ? idle with { LeftClick = true } : idle)
            .Forever(idle);
        var plain = Run(Ground(600f), AirStart, Script(false), 40);
        var cut   = Run(Ground(600f), AirStart, Script(true),  40);

        Assert.Contains(nameof(DashSlash), cut.Actions);
        int f = plain.FirstDash, end = f + 22;   // past the 0.24 s burst + exit carry
        output.WriteLine($"dash@{f} frames {plain.DashFrames}/{cut.DashFrames} " +
                         $"travel {plain.Pos[end].X - plain.Pos[f].X:0.0}/{cut.Pos[end].X - cut.Pos[f].X:0.0}");
        Assert.Equal(f, cut.FirstDash);
        Assert.Equal(plain.DashFrames, cut.DashFrames);
        Assert.True(MathF.Abs(plain.Pos[end].X - cut.Pos[end].X) < 1f, "the slice must not cost dash distance");
        Assert.True(plain.Pos[f + 14].X - plain.Pos[f].X > 75f, "the longer burst should carry ~86 px");
    }
}
