using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// LaserAction: scan an elongated rectangle, then burn down it destroying blocks until
// the power budget is spent or the far end is reached, publishing one hitbox over the
// swept region. These pin the four rules that make it that move rather than a beam:
//   1. It DESTROYS cells along the box (not chips them).
//   2. It stops where the budget runs out, and everything past that survives.
//   3. Material cost is the reach knob — stone stops it far sooner than sand.
//   4. A body inside the swept box takes the hit; a body past the stopping point doesn't.
public class LaserTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const int   TS = Chunk.TileSize;

    // Long enough to cover scan (0.28s) + burn (0.34s) with room to settle: 60 frames = 1s.
    private const int Frames = 60;

    // Floor along row 6; the shooter stands on it at cell (2,5) and fires to the RIGHT
    // along row 5, straight into whatever `wall` fills cells 6..21 of that row. Rows 4
    // and 5 are both walled so the 20px-wide box has solid material above and below its
    // axis (which is what makes the ~2-cells-per-column cost real).
    private static ChunkMap Course(char wall)
    {
        var rowsAbove = new string('O', 24);
        string band = new string('O', 6) + new string(wall, 16) + new string('O', 2);
        var ascii = string.Join('\n', new[]
        {
            rowsAbove,        // row 0
            rowsAbove,        // 1
            rowsAbove,        // 2
            rowsAbove,        // 3
            band,             // 4  — upper half of the beam's width
            band,             // 5  — the beam axis
            new string('X', 24),  // 6  — floor
            new string('X', 24),  // 7
        });
        return SimTerrain.FromAscii(ascii, originTileX: 0, originTileY: 0);
    }

    // Standing on the floor (row 6 top = y 96), body centered a bit above it so the
    // player settles onto row 5.
    private static readonly Vector2 Shooter = new(2 * TS + 8f, 5 * TS + 4f);

    // Cursor far to the right along the beam axis (row 5's vertical center).
    private static readonly Vector2 AimRight = new(600f, 5 * TS + 8f);

    // Idle a few frames so the player settles on the floor, then tap R and hold aim.
    // R is a press edge, so it is released after one frame — the shot is committed.
    private static InputScript FireRight(Vector2 aim) => new InputScript()
        .For(6,  new PlayerInput { MouseWorldPosition = aim })
        .For(1,  new PlayerInput { MouseWorldPosition = aim, R = true })
        .Forever(new PlayerInput { MouseWorldPosition = aim });

    private sealed class Trace
    {
        public bool  Fired;
        public float MaxReach;
        public float MinPower = float.MaxValue;
        public float VictimDamage;
    }

    private static Trace Run(ChunkMap terrain, InputScript script,
                             IList<SimPlayer>? extraPlayers = null)
    {
        var trace = new Trace();
        var players = new List<SimPlayer> { new() { StartPosition = Shooter, Script = script } };
        if (extraPlayers != null) foreach (var p in extraPlayers) players.Add(p);

        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = Frames,
            Dt      = Dt,
            Gravity = new Vector2(0f, 600f),
            Players = players,
        },
        onFrame: (f, ps) =>
        {
            if (ps[0].CurrentAction is LaserAction)
            {
                var v = ps[0].CurrentActionVars;
                if (v.Firing)
                {
                    trace.Fired    = true;
                    trace.MaxReach = MathF.Max(trace.MaxReach, v.LaserReach);
                    trace.MinPower = MathF.Min(trace.MinPower, v.LaserPower);
                }
            }
            if (ps.Length > 1)
                trace.VictimDamage = MathF.Max(trace.VictimDamage, ps[1].Combat.DamageTaken);
        });
        return trace;
    }

    // How many consecutive cells of row 5 (starting at the wall's first cell, 6) are
    // Empty — i.e. the depth of the tunnel the laser bored, in tiles.
    private static int TunnelDepth(ChunkMap chunks)
    {
        int depth = 0;
        for (int gtx = 6; gtx < 22; gtx++)
        {
            if (chunks.GetCellState(gtx, 5) != TileState.Empty) break;
            depth++;
        }
        return depth;
    }

    [Fact]
    public void Laser_BoresATunnelThroughTheWall()
    {
        var terrain = Course('X');                 // ASCII solids default to Stone
        var trace = Run(terrain, FireRight(AimRight));
        int depth = TunnelDepth(terrain);
        output.WriteLine($"fired={trace.Fired} reach={trace.MaxReach:F0}px depth={depth} tiles minPower={trace.MinPower:F1}");

        Assert.True(trace.Fired, "the laser never reached its firing phase");
        Assert.True(depth >= 3, $"expected the laser to bore at least 3 tiles of stone, got {depth}");
    }

    // The budget, not the box length, is what ends the shot in solid rock: the front
    // stops well short of MaxLength and everything past it is untouched.
    [Fact]
    public void Laser_StopsWherePowerRunsOut_AndLeavesTheRestStanding()
    {
        var terrain = Course('X');
        var trace = Run(terrain, FireRight(AimRight));
        int depth = TunnelDepth(terrain);
        output.WriteLine($"reach={trace.MaxReach:F0}px depth={depth} tiles minPower={trace.MinPower:F2}");

        Assert.Equal(0f, trace.MinPower);                                   // budget spent
        Assert.True(trace.MaxReach < 26f * TS - TS,
            $"a stone wall should stop the front short of the box's far end; reach={trace.MaxReach:F0}");
        // The wall is 16 tiles deep — the shot must not have cleared all of it.
        Assert.True(depth < 16, $"the laser tunnelled the entire wall ({depth} tiles)");
        Assert.Equal(TileState.Solid, terrain.GetCellState(6 + depth, 5));  // the far face survives
    }

    // Material cost IS the reach knob: the same shot goes much further through empty air
    // than through rock, because air is free.
    [Fact]
    public void Laser_ReachesFurtherThroughAirThanThroughStone()
    {
        var open  = Run(Course('O'), FireRight(AimRight));
        var stone = Run(Course('X'), FireRight(AimRight));
        output.WriteLine($"air reach={open.MaxReach:F0}px  stone reach={stone.MaxReach:F0}px");

        Assert.True(open.MaxReach > 26f * TS - TS,
            $"through open air the laser should run the full box; reach={open.MaxReach:F0}");
        Assert.True(open.MaxReach > stone.MaxReach * 2f,
            $"air {open.MaxReach:F0} vs stone {stone.MaxReach:F0}");
    }

    // A body standing inside the swept box, well within reach, takes the hit through the
    // ordinary hitbox path (escalation damage).
    [Fact]
    public void Laser_DamagesABodyInsideTheBox()
    {
        var victim = new SimPlayer
        {
            StartPosition = new Vector2(6 * TS + 8f, 5 * TS + 4f),   // 4 tiles downrange
            Script        = InputScript.Always(default),
            Faction       = Faction.Player2,
        };
        var trace = Run(Course('O'), FireRight(AimRight), new[] { victim });
        output.WriteLine($"reach={trace.MaxReach:F0}px victimDamage={trace.VictimDamage:F1}");

        Assert.True(trace.VictimDamage > 0f, "a body inside the laser box took no damage");
    }

    // …and one standing past the point where the budget ran out does not: the box only
    // ever covers what the beam actually burned through.
    [Fact]
    public void Laser_DoesNotDamageABodyPastItsStoppingPoint()
    {
        // Wall from cell 6; the victim sits at cell 20, far beyond what 24 power buys.
        var victim = new SimPlayer
        {
            StartPosition = new Vector2(20 * TS + 8f, 5 * TS + 4f),
            Script        = InputScript.Always(default),
            Faction       = Faction.Player2,
        };
        var terrain = Course('X');
        var trace = Run(terrain, FireRight(AimRight), new[] { victim });
        output.WriteLine($"reach={trace.MaxReach:F0}px depth={TunnelDepth(terrain)} victimDamage={trace.VictimDamage:F1}");

        Assert.Equal(0f, trace.VictimDamage);
    }
}
