using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The two halves of "Zeus keeps fighting a player it cannot see":
//
//   1. ZeusThunderColumnAction — the attack that neither needs line of sight to
//      OPEN nor respects terrain when it LANDS. Cover is not an answer to it.
//   2. The target memory (EnemyEntity.TracksTarget + ZeusController's search
//      point) — the beams, which all still gate on line of sight, keep firing at
//      where the player last was rather than falling silent the moment the player
//      steps behind something.
//
// Both are about a NEGATIVE — an attack that fails to happen leaves no trace and
// looks exactly like a boss that is working fine but idle — so these run the real
// Simulation with a real Zeus behind a real wall and read the action names off it.
//
// Purpose-built terrain rather than Levels/hill.json: the spire is now shaped
// precisely so its own tip can see its whole face (that was the point of the
// taper), which makes it the worst possible place to test what happens when sight
// is broken. A wall is unambiguous.
public class ZeusBlindFireTests(ITestOutputHelper output)
{
    private const int TS = Chunk.TileSize;

    // Zeus at the left, the player at the right, and a two-column wall between them
    // running from the ceiling down to the floor so nothing gets a sight line past
    // it — not to the player, and not to anywhere within the controller's ±190px
    // search spread of the player either. That completeness is what makes "the
    // player took damage" attributable: the beams are published WITH an origin, so
    // terrain occlusion stops them; only the column can reach through.
    //
    //        0 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15
    //   0    . . . . . . X X . . .  .  .  .  .  .
    //   ...
    //   6    . Z . . . . X X . . .  P  .  .  .  .
    //   7    X X X X X X X X X X X  X  X  X  X  X
    private const int WallColA = 6, WallColB = 7;
    private const int FloorRow = 7;

    // Columns spanned by the roof in the roofed variant: wide enough to cover the
    // three-tile column plus the +/-BlindJitter scatter either side of it.
    private const int RoofRow = 4, RoofColA = 8, RoofColB = 14;

    private static ChunkMap Course(bool roofed = false)
    {
        var rows = new List<string>();
        for (int r = 0; r < FloorRow; r++)
        {
            var line = new char[16];
            for (int c = 0; c < 16; c++) line[c] = (c == WallColA || c == WallColB) ? 'X' : '.';
            if (roofed && r == RoofRow)
                for (int c = RoofColA; c <= RoofColB; c++) line[c] = 'X';
            rows.Add(new string(line));
        }
        rows.Add(new string('X', 16));
        rows.Add(new string('X', 16));
        return SimTerrain.FromAscii(string.Join('\n', rows), originTileX: 0, originTileY: 0);
    }

    // Zeus hangs west of the wall (rooted + weightless, so it stays put); the player
    // stands on the floor east of it. 10 tiles apart — well inside AlertRange (620px)
    // and the column's MaxRange, so "nearby but hidden" is exactly the state.
    private static readonly Vector2 ZeusPos   = new(1 * TS + 8f, 5 * TS + 8f);
    private static readonly Vector2 Hidden    = new(11 * TS + 8f, 6 * TS + 4f);
    // Same row as Zeus and on ITS side of the wall, so the sight line is clear.
    private static readonly Vector2 InTheOpen = new(3 * TS + 8f, 6 * TS + 4f);

    private static Simulation Build(Vector2 playerAt)
        => new(Course(), playerAt,
               g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Zeus, ZeusPos)));

    private static EnemyEntity FindZeus(Simulation sim)
    {
        foreach (var e in sim.Entities)
            if (e is EnemyEntity en && en.Kind == EntityKind.Zeus) return en;
        return null;
    }

    // Steps `frames`, pinning the player where the caller wants them (Zeus's column
    // knocks for 260 and its beams for more; letting the body get thrown around would
    // make "was the player visible" a different question every frame). Returns every
    // action name Zeus opened.
    private static HashSet<string> Run(Simulation sim, Vector2 pin, int frames,
                                       ITestOutputHelper output = null)
    {
        var zeus = FindZeus(sim);
        var seen = new HashSet<string>();
        for (int f = 0; f < frames; f++)
        {
            sim.Player.Body.Position = pin;
            sim.Player.Body.Velocity = Vector2.Zero;
            sim.Step(default);
            string a = zeus.CurrentActionName;
            if (a.Length > 0) seen.Add(a);
        }
        output?.WriteLine($"actions: {string.Join(",", seen)}");
        return seen;
    }

    private const int CycleFrames = 600;

    // The wall has to actually work, or every other assertion in this file is vacuous.
    [Fact]
    public void TheWallBreaksLineOfSight()
    {
        var chunks = Course();
        Assert.False(EnemyAim.HasLineOfSight(ZeusPos, Hidden, chunks, 16f),
                     "the wall does not occlude the hidden spot");
        Assert.True(EnemyAim.HasLineOfSight(ZeusPos, InTheOpen, chunks, 16f),
                    "the open spot is occluded — the course is wrong");
    }

    // 1. The column opens with no sight line at all. Every other Zeus attack calls
    //    EnemyAim.HasLineOfSight in its precondition and would stay shut here.
    [Fact]
    public void ThunderColumnOpensWithNoLineOfSight()
    {
        var sim  = Build(Hidden);
        var seen = Run(sim, Hidden, 2 * CycleFrames, output);

        Assert.Contains("ZeusThunderColumnAction", seen);
    }

    // 2. ...and lands through the wall. The column IS occluded — it publishes an origin
    //    — but that origin sits at the top of the column, so the reachability trace runs
    //    straight DOWN and a wall standing beside the player is not on it. The sky above
    //    the hidden spot (column 11) is open all the way up, which is why this still
    //    connects. Damage here cannot have come from anything else: the beams trace from
    //    Zeus and are occluded by the same wall. See RoofOverThePlayerBlocksTheColumn for
    //    the other half — the cover that DOES work.
    [Fact]
    public void ThunderColumnDamagesAPlayerBehindCover()
    {
        var sim = Build(Hidden);
        Assert.Equal(0f, sim.Player.Combat.DamagePercent);

        // Several cycles: the column's blind aim is scattered by ±BlindJitter, so it is
        // not meant to connect every single time — only reliably enough that standing
        // still behind a wall is a losing plan.
        Run(sim, Hidden, 4 * CycleFrames, output);

        output.WriteLine($"damage taken behind cover: {sim.Player.Combat.DamagePercent}");
        Assert.True(sim.Player.Combat.DamagePercent > 0f,
                    "the player was never hit through the wall — cover is still free.");
    }

    // 2b. The other half of the same rule, and the reason the origin is where it is: a
    //     roof stops the column even though a wall does not. Same course, same hidden
    //     spot, one row of tiles added directly overhead — so the only thing that changed
    //     is what is between the sky and the player.
    //
    //     This is the counterplay the attack is built around. It is not passive cover:
    //     the player has to spend build mass and put the ceiling up inside the 2.2s tell,
    //     which is what keeps the column from degenerating into a strictly worse bolt now
    //     that it can be answered at all.
    [Fact]
    public void RoofOverThePlayerBlocksTheColumn()
    {
        var chunks = Course(roofed: true);

        // Sanity: the roof is overhead and the player is not buried in it, or "no damage"
        // would prove nothing about occlusion.
        Assert.Equal(TileState.Solid, chunks.GetCellState(11, RoofRow));
        Assert.NotEqual(TileState.Solid, chunks.GetCellState(11, 6));

        var sim = new Simulation(chunks, Hidden,
                                 g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Zeus, ZeusPos)));

        // The same four cycles that reliably draw blood in the uncovered case above.
        var seen = Run(sim, Hidden, 4 * CycleFrames, output);

        output.WriteLine($"damage taken under a roof: {sim.Player.Combat.DamagePercent}");
        // The column must still OPEN — this tests occlusion, not a precondition that
        // quietly stopped firing, which would pass for entirely the wrong reason.
        Assert.Contains("ZeusThunderColumnAction", seen);
        Assert.Equal(0f, sim.Player.Combat.DamagePercent);
    }

    // 2c. Range. The column reaches anywhere on the stage — walking away is not an
    //     answer to it. This is a two-part gate and the test would be worthless against
    //     only one: ZeusThunderColumnAction.MaxRange had to become unbounded, AND
    //     ZeusController.AlertRange had to stop clamping it, because EnemyEntity refuses
    //     to select any new action while WantAttack is false. At the old AlertRange of
    //     620 the column's own 900 was already dead code.
    //
    //     The beams are the control: they carry their own bands (bolt 80-640, strike
    //     <=640, sweep 60-620) and must still be silent out here, which is what shows the
    //     coarse gate was widened rather than the whole kit unleashed.
    [Fact]
    public void TheColumnReachesFarBeyondTheOldAlertRange()
    {
        // Flat open floor 140 tiles wide — no cover, so this is purely about distance.
        const int Wide = 140, Floor = 7;
        var rows = new List<string>();
        for (int r = 0; r < Floor; r++) rows.Add(new string('.', Wide));
        rows.Add(new string('X', Wide));
        rows.Add(new string('X', Wide));
        var chunks = SimTerrain.FromAscii(string.Join('\n', rows), originTileX: 0, originTileY: 0);

        // ~1900px apart: triple the old 620 alert range, and past the old 900 MaxRange.
        var zeusAt   = new Vector2(2 * TS + 8f, 5 * TS + 8f);
        var playerAt = new Vector2(122 * TS + 8f, 6 * TS + 4f);
        float dist   = (playerAt - zeusAt).Length();
        Assert.True(dist > 1800f, $"the course is not long enough to prove anything ({dist:F0}px)");

        var sim = new Simulation(chunks, playerAt,
                                 g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Zeus, zeusAt)));
        var seen = Run(sim, playerAt, 4 * CycleFrames, output);

        output.WriteLine($"at {dist:F0}px — actions: {string.Join(",", seen)}, " +
                         $"damage: {sim.Player.Combat.DamagePercent}");
        Assert.Contains("ZeusThunderColumnAction", seen);
        Assert.True(sim.Player.Combat.DamagePercent > 0f,
                    "the column opened but never connected at range.");

        // The beams stay home. If these fired, the change widened far more than intended.
        foreach (var beam in new[] { "ZeusBoltAction", "ZeusStrikeAction", "ZeusSweepAction" })
            Assert.DoesNotContain(beam, seen);
    }

    // 3. The memory. With the player visible, Zeus opens its ordinary repertoire; once
    //    the player ducks behind the wall it should KEEP opening it, aimed at where
    //    they last were — not fall silent. The beams are the tell, since those are the
    //    ones gated on line of sight.
    [Fact]
    public void BeamsKeepFiringAtTheLastSeenPositionAfterSightIsBroken()
    {
        var sim = Build(InTheOpen);

        // Long enough in the open for a sighting to be recorded and the schedule to
        // come round: this is what puts something IN the memory.
        var whileVisible = Run(sim, InTheOpen, CycleFrames, output);
        Assert.True(whileVisible.Count > 0, "Zeus never attacked a player standing in plain view.");

        // Now duck behind the wall and stay there. The remembered spot is out in the
        // open on Zeus's side, so Zeus can still see the spot it is shooting at — which
        // is the entire mechanism.
        var whileHidden = Run(sim, Hidden, 2 * CycleFrames, output);

        var beams = new[] { "ZeusBoltAction", "ZeusStrikeAction", "ZeusSweepAction" };
        Assert.Contains(beams, b => whileHidden.Contains(b));
    }

    // 4. And the memory is memory, not clairvoyance: what Zeus shoots at after sight is
    //    broken is the OLD position, so walking away from where you were last seen is
    //    the counterplay the search pattern is supposed to reward.
    [Fact]
    public void TheRememberedPositionIsWhereThePlayerWasNotWhereTheyAre()
    {
        var sim  = Build(InTheOpen);
        var zeus = FindZeus(sim);

        Run(sim, InTheOpen, 30);                       // record a sighting

        // Move somewhere the wall hides, and far from the sighting.
        var far = new Vector2(14 * TS + 8f, 6 * TS + 4f);
        Run(sim, far, 60);

        // The statue is now aiming near the OLD spot. Read it off the controller the
        // same way the action FSM does, through a context the entity fills in.
        Assert.False(EnemyAim.HasLineOfSight(ZeusPos, far, Course(), 16f),
                     "the far spot is not actually hidden");

        float toOld = MathF.Abs(zeus.LastSeenPos.X - InTheOpen.X);
        float toNew = MathF.Abs(zeus.LastSeenPos.X - far.X);
        output.WriteLine($"memory at x={zeus.LastSeenPos.X}, old={InTheOpen.X}, new={far.X}");
        Assert.True(toOld < toNew, "Zeus is remembering where the player went, not where it saw them.");
        Assert.True(zeus.LastSeenAge > 0.5f, "the sighting never went stale while the player was hidden.");
    }
}
