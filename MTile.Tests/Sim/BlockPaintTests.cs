using System;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// BlockPaintAction — plain RMB mass-ball painter. A ball seeded at the cursor trails it
// with critical damping, leaking mass into the cell beneath; TileMassField's spill
// cascade turns that into sprouts.
//
// These pin the three properties the design depends on: plain RMB paints (and paints
// more than the old one-tile-per-cell drag did), Shift+RMB does NOT paint (it charges
// an eruption instead), and the ball never overshoots the cursor.
public class BlockPaintTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 Start = new(40f, 36f);

    private static SimConfigMulti Build(InputScript script, ChunkMap terrain, int frames) => new SimConfigMulti
    {
        Terrain = terrain,
        Frames  = frames,
        Dt      = Dt,
        Gravity = new Vector2(0f, 600f),
        Players = new[] { new SimPlayer { StartPosition = Start, Script = script } },
    };

    // Cursor parked just above the floor, within build reach, so the deposit has support
    // to grow from. Held for ~0.75s of paint.
    private static readonly Vector2 OnFloor = new(56f, 44f);

    private static int SolidCount(ChunkMap c, int gtx0, int gtx1, int gty0, int gty1)
    {
        int n = 0;
        for (int gtx = gtx0; gtx <= gtx1; gtx++)
        for (int gty = gty0; gty <= gty1; gty++)
            if (c.GetCellState(gtx, gty) == TileState.Solid) n++;
        return n;
    }

    [Fact]
    public void PlainRmbHeld_PaintsMultipleTiles()
    {
        var terrain = FlatGround();
        int before = SolidCount(terrain, 0, 8, 0, 3);

        string action = "";
        SimRunner.RunMulti(Build(new InputScript()
                .For(6, new PlayerInput { MouseWorldPosition = OnFloor })
                .Forever(new PlayerInput { RightClick = true, MouseWorldPosition = OnFloor }),
            terrain, frames: 60),
            onFrame: (f, ps) => { if (ps[0].CurrentActionName == "BlockPaintAction") action = "BlockPaintAction"; });

        int after = SolidCount(terrain, 0, 8, 0, 3);
        output.WriteLine($"action={action}, solid {before} → {after}");
        Assert.Equal("BlockPaintAction", action);
        // The old drag paint on a parked cursor placed exactly one tile. Mass flow plus
        // the spill cascade should build a small mound instead.
        Assert.True(after - before > 1, $"Expected a mound, got {after - before} tile(s).");
    }

    // Plain RMB pressed with the cursor buried is the CHARGE half of the gesture: it must
    // bank eruption charge and place nothing.
    [Fact]
    public void RmbPressedInSolid_ChargesInsteadOfPainting()
    {
        var terrain = FlatGround();
        int before = SolidCount(terrain, 0, 8, 0, 3);

        float peakCharge = 0f;
        var inSolid = new Vector2(56f, 56f);
        SimRunner.RunMulti(Build(new InputScript()
                .For(6, new PlayerInput { MouseWorldPosition = inSolid })
                .Forever(new PlayerInput { RightClick = true, MouseWorldPosition = inSolid }),
            terrain, frames: 60),
            onFrame: (f, ps) => peakCharge = MathF.Max(peakCharge, ps[0].Abilities.Meters.EruptMove));

        output.WriteLine($"peak charge = {peakCharge:F1}");
        Assert.True(peakCharge > 0f, "A press inside solid should bank eruption charge.");
        Assert.Equal(before, SolidCount(terrain, 0, 8, 0, 3));
    }

    // Shift+RMB is precise single-block placement — one tile, paid in full, no cascade.
    [Fact]
    public void ShiftRmb_PlacesASingleBlock()
    {
        var terrain = FlatGround();
        int before = SolidCount(terrain, 0, 8, 0, 3);

        bool sawPlace = false, sawGuard = false;
        SimRunner.RunMulti(Build(new InputScript()
                .For(6, new PlayerInput { MouseWorldPosition = OnFloor })
                .Forever(new PlayerInput { Shift = true, RightClick = true, MouseWorldPosition = OnFloor }),
            terrain, frames: 60),
            onFrame: (f, ps) =>
            {
                if (ps[0].CurrentActionName == "BlockPlaceAction") sawPlace = true;
                if (ps[0].CurrentActionName == "GuardAction")      sawGuard = true;
            });

        int after = SolidCount(terrain, 0, 8, 0, 3);
        output.WriteLine($"place={sawPlace}, guard={sawGuard}, solid {before} → {after}");
        Assert.True(sawPlace, "Shift+RMB should enter BlockPlaceAction.");
        // Regression: GuardAction's Passive 40 used to bury the build actions' Passive 10.
        Assert.False(sawGuard, "Guard must yield to the Shift+RMB build gesture.");
        // Exactly one — a parked cursor places its cell once, not once per frame.
        Assert.Equal(before + 1, after);
    }

    // Critical damping: the ball closes on the target monotonically and never crosses it.
    [Fact]
    public void Ball_TrailsTargetWithoutOvershooting()
    {
        var pos = new Vector2(0f, 0f);
        var vel = Vector2.Zero;
        var target = new Vector2(100f, 0f);

        float prev = pos.X;
        for (int i = 0; i < 200; i++)
        {
            SmoothPen.CriticallyDampedStep(ref pos, ref vel, target, smoothTime: 0.12f, dt: Dt);
            Assert.True(pos.X <= target.X + 1e-3f, $"overshot at step {i}: x={pos.X}");
            Assert.True(pos.X >= prev - 1e-3f, $"moved backwards at step {i}");
            prev = pos.X;
        }
        output.WriteLine($"settled at x={pos.X:F3} (target 100)");
        Assert.True(MathF.Abs(pos.X - target.X) < 0.5f, "Ball should converge on the target.");
    }
}
