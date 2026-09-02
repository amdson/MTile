using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behavioural gates for the three gauntlet enemies (Bastion / Pouncer / Latcher)
// and the framework changes they required. Each test pins the ONE property that
// makes its enemy the thing it is, so a future retune can move every number in
// the blueprint without breaking these, but cannot quietly break the mechanic:
//
//   Bastion — charges, then actually emits a bolt; the bolt eats terrain and
//             stops eating after its penetration budget.
//   Pouncer — leaves the ground under its own power, closes horizontally, and
//             hits harder the further it falls.
//   Latcher — stays attached to a ceiling, INCLUDING while it attacks (the
//             failure mode the EnemyClingMoveState change fixes), and connects
//             from that inverted position.
//
// Everything runs through a real Simulation so the phase ordering, combat pass,
// and physics step are the ones the game uses.
public class GauntletEnemyTests(ITestOutputHelper output)
{
    // Flat floor: solid at tile y = 3 (world y 48), open above, 40 tiles wide
    // starting at tile x -4.
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float FloorTopY = 3 * Chunk.TileSize;   // 48

    // Roofed corridor: ceiling occupies tile rows 0-1, floor row 6, interior
    // rows 2-5 (four tiles of headroom, comfortably above the standing-fold
    // envelope so the player doesn't auto-crouch and cramp the Latcher's swing).
    private static ChunkMap Tunnel() => SimTerrain.FromAscii(@"
        XXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXX
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float CeilingBottomY = 2 * Chunk.TileSize;
    private const float TunnelFloorTopY = 6 * Chunk.TileSize;
    // Anything past the corridor's vertical midpoint means the Latcher let go.
    private const float TunnelMidpointY = (CeilingBottomY + TunnelFloorTopY) / 2f;

    private static readonly PlayerInput Idle = default;

    // ── Bastion ──────────────────────────────────────────────────────────────

    [Fact]
    public void Bastion_ChargesThenFiresARailBolt()
    {
        // 300px apart — inside the rail shot's [70, 520] band and outside its
        // MinRange, so the charge is the only thing that can happen.
        var sim = new Simulation(Floor(), new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Bastion, new Vector2(340f, FloorTopY - 14f))));

        int firstBoltFrame = -1;
        // Windup is 1.35s ≈ 81 frames; 150 leaves room for the shot without
        // reaching into a second charge cycle's fire.
        for (int f = 0; f < 150; f++)
        {
            sim.Step(Idle);
            if (firstBoltFrame < 0 && HasKind(sim, EntityKind.RailBolt)) firstBoltFrame = f;
        }

        Assert.True(firstBoltFrame > 0, "Bastion never fired a rail bolt in 150 frames.");
        // Fired AFTER a real charge, not on sight. The exact frame is a tuning
        // detail; that there was a windup at all is the design contract.
        Assert.InRange(firstBoltFrame, 60, 120);
        output.WriteLine($"Bolt spawned at frame {firstBoltFrame} (windup ≈ 81 frames).");
    }

    [Fact]
    public void Bastion_DoesNotChargeWhenThePlayerIsInsideItsGuard()
    {
        // Player 40px away — under MinRange (70). The emplacement's answer to a
        // player who closed the distance is "nothing", which is what makes
        // closing the distance the correct play.
        var sim = new Simulation(Floor(), new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Bastion, new Vector2(80f, FloorTopY - 14f))));

        for (int f = 0; f < 200; f++)
        {
            sim.Step(Idle);
            Assert.False(HasKind(sim, EntityKind.RailBolt),
                $"Bastion fired at point-blank range on frame {f}.");
        }
    }

    [Fact]
    public void Bastion_HoldsFireThroughAWallAndOpensUpWhenItIsGone()
    {
        // Same geometry as the charge test, plus a stone slab across the line of
        // sight. The emplacement must not wind up at a player it cannot see —
        // otherwise it spends the encounter excavating its own room. Then the
        // slab is removed and it must fire, proving the gate tracks live terrain
        // rather than a one-time visibility decision.
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

        var sim = new Simulation(chunks, new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Bastion, new Vector2(340f, FloorTopY - 14f))));

        for (int f = 0; f < 200; f++)
        {
            sim.Step(Idle);
            Assert.False(HasKind(sim, EntityKind.RailBolt),
                $"Bastion fired through a solid wall on frame {f}.");
        }

        // Ascii col 14 with originTileX -4 ⇒ tile x 10, rows 1-2.
        chunks.BreakCell(10, 1);
        chunks.BreakCell(10, 2);

        bool fired = false;
        for (int f = 0; f < 200 && !fired; f++)
        {
            sim.Step(Idle);
            fired = HasKind(sim, EntityKind.RailBolt);
        }
        Assert.True(fired, "Bastion stayed silent after the wall came down.");
    }

    [Fact]
    public void RailBolt_ChewsThroughTerrainButOnlyForItsBudget()
    {
        // A wall of stone standing on the floor, and a bolt fired straight into
        // it from the left. The bolt must open a hole (cover is consumable) and
        // must NOT keep going forever (the level is not). A blank buffer row
        // separates the wall from the floor: the bolt's fixed HitboxHalfSize (6,
        // 12px tall) is now taller than a single 11px tile, so a level shot
        // through the wall's row unavoidably grazes the row above/below it —
        // the buffer keeps that graze off the floor the last assert checks.
        var chunks = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOXOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOXOOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

        // Wall column: ascii col 23 with originTileX -4 ⇒ tile x 19, rows 1-2. Floor is row 4.
        const int WallTx = 19;
        const int FloorTy = 4;
        int solidBefore = CountSolid(chunks, WallTx - 1, WallTx + 1, 1, 2);
        Assert.Equal(2, solidBefore);

        var sim = new Simulation(chunks, new Vector2(0f, FloorTopY - 12f));
        // Fire from well left of the wall, dead level with its lower cell.
        float y = 2 * Chunk.TileSize + Chunk.TileSize * 0.5f;
        sim.SpawnEntity(new RailBoltProjectile(new Vector2(120f, y), Vector2.UnitX,
                                               sim.HitIds.Next(), Faction.Enemy));

        // Lifetime is 0.85s ≈ 51 frames; 120 frames guarantees it is gone one
        // way or another.
        for (int f = 0; f < 120; f++) sim.Step(Idle);

        int solidAfter = CountSolid(chunks, WallTx - 1, WallTx + 1, 1, 2);
        Assert.True(solidAfter < solidBefore,
            "Rail bolt left the wall intact — it is supposed to consume cover.");
        Assert.False(HasKind(sim, EntityKind.RailBolt), "Rail bolt outlived its lifetime.");

        // The floor under the flight path is untouched: the bolt travels level,
        // so a level shot must not trench the ground it flew over.
        Assert.True(CountSolid(chunks, 0, 15, FloorTy, FloorTy) == 16,
            "Rail bolt damaged floor cells it never intersected.");
        output.WriteLine($"Wall cells {solidBefore} → {solidAfter}.");
    }

    // ── Pouncer ──────────────────────────────────────────────────────────────

    [Fact]
    public void Pouncer_HopsOffTheGroundAndClosesOnThePlayer()
    {
        var sim = new Simulation(Floor(), new Vector2(320f, FloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Pouncer, new Vector2(60f, FloorTopY - 11f))));

        var pouncer = FirstOfKind(sim, EntityKind.Pouncer);
        float startX = pouncer.Body.Position.X;
        float restY  = pouncer.Body.Position.Y;
        float peakY  = restY;      // Y-down: the peak of an arc is the MINIMUM y

        for (int f = 0; f < 240; f++)
        {
            sim.Step(Idle);
            if (pouncer.Body.Position.Y < peakY) peakY = pouncer.Body.Position.Y;
        }

        float rise = restY - peakY;
        Assert.True(rise > 24f, $"Pouncer never left the ground (peak rise {rise:F1}px).");
        Assert.True(pouncer.Body.Position.X - startX > 60f,
            $"Pouncer did not close on the player (Δx {pouncer.Body.Position.X - startX:F1}px).");
        output.WriteLine($"Rise {rise:F1}px, Δx {pouncer.Body.Position.X - startX:F1}px.");
    }

    [Fact]
    public void PounceSlam_HitsHarderFromHigherUp()
    {
        // The defining property: damage is a function of impact speed, not a
        // constant. Same enemy, same player, two drop heights.
        float shallow = DropAndMeasurePercent(90f);
        float deep    = DropAndMeasurePercent(400f);

        Assert.True(shallow > 0f, "Short drop did not connect at all.");
        Assert.True(deep > shallow * 1.25f,
            $"Slam damage did not scale with fall speed (shallow {shallow:F2}, deep {deep:F2}).");
        output.WriteLine($"Percent from 90px drop: {shallow:F2}; from 400px: {deep:F2}.");
    }

    // Drop a Pouncer from `height` px directly above a stationary player and
    // return the HP it took off. The player is left on the floor with no input, so
    // the only thing that can hurt it is the slam.
    private static float DropAndMeasurePercent(float height)
    {
        var playerSpawn = new Vector2(160f, FloorTopY - 12f);
        var sim = new Simulation(Floor(), playerSpawn,
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Pouncer,
                                                   new Vector2(160f, FloorTopY - 12f - height))));

        // Sample the moment the slam first registers rather than at a fixed
        // frame: a 400px drop takes ~62 frames to land and a 90px drop ~29, so
        // any single deadline either cuts the deep case short or gives the
        // shallow case time to hop and slam a second time. The slam dedupes on
        // one HitId, so the first nonzero reading IS that slam's whole
        // contribution.
        for (int f = 0; f < 200; f++)
        {
            sim.Step(Idle);
            if (sim.Player.Combat.DamageTaken > 0f) break;
        }
        return sim.Player.Combat.DamageTaken;
    }

    // ── Latcher ──────────────────────────────────────────────────────────────

    [Fact]
    public void Latcher_HangsFromTheCeilingIndefinitely()
    {
        // Player parked far to the right and out of lash range, so this measures
        // pure locomotion: does the crawler hold an inverted surface at all?
        var sim = new Simulation(Tunnel(), new Vector2(300f, TunnelFloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Latcher,
                                                   new Vector2(60f, CeilingBottomY + 11f))));

        var latcher = FirstOfKind(sim, EntityKind.Latcher);
        float worstY = latcher.Body.Position.Y;

        for (int f = 0; f < 300; f++)
        {
            sim.Step(Idle);
            if (latcher.Body.Position.Y > worstY) worstY = latcher.Body.Position.Y;
        }

        Assert.True(worstY < TunnelMidpointY,
            $"Latcher peeled off the ceiling (lowest y {worstY:F1}, floor at {TunnelFloorTopY}).");
        output.WriteLine($"Held ceiling for 300 frames; lowest y {worstY:F1}.");
    }

    [Fact]
    public void Latcher_LashesFromTheCeilingWithoutLettingGo()
    {
        // Regression gate for the EnemyClingMoveState fix. Before it,
        // CheckConditions dropped the cling the moment an action committed;
        // Exit restores GravityScale, so the crawler fell off the ceiling on the
        // first frame of every swing — turning its signature move into a
        // pratfall. The player sits directly beneath, inside lash range.
        var sim = new Simulation(Tunnel(), new Vector2(100f, TunnelFloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Latcher,
                                                   new Vector2(100f, CeilingBottomY + 11f))));

        var latcher = FirstOfKind(sim, EntityKind.Latcher);
        float worstY = latcher.Body.Position.Y;

        for (int f = 0; f < 240; f++)
        {
            sim.Step(Idle);
            if (latcher.Body.Position.Y > worstY) worstY = latcher.Body.Position.Y;
        }

        Assert.True(sim.Player.Combat.DamageTaken > 0f,
            "Latcher never connected from an inverted position.");
        Assert.True(worstY < TunnelMidpointY,
            $"Latcher fell off the ceiling while attacking (lowest y {worstY:F1}).");
        output.WriteLine($"Lowest y {worstY:F1}; player at {sim.Player.Combat.DamageTaken:F2}%.");
    }

    // ── Framework ────────────────────────────────────────────────────────────

    [Fact]
    public void WantAttack_False_SuppressesActionSelection()
    {
        // StationaryAimController clears WantAttack outside AlertRange. Before
        // the EnemyEntity wiring, the field was declared and documented but
        // never read, so an out-of-alert Bastion charged anyway.
        var sim = new Simulation(Floor(), new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(new BlueprintEnemy(new EnemyBlueprint
            {
                Kind       = EntityKind.Bastion,
                Radius     = 14f,
                Mass       = 40f,
                Controller = new StationaryAimController { AlertRange = 10f },   // effectively never
                Movement   = () => new List<EnemyMovementState> { new EnemyIdleState() },
                Actions    = () => new List<EnemyActionState>   { new EnemyRailShotAction() },
            }, new Vector2(340f, FloorTopY - 14f))));

        for (int f = 0; f < 200; f++)
        {
            sim.Step(Idle);
            Assert.False(HasKind(sim, EntityKind.RailBolt),
                $"A vetoed brain still fired on frame {f}.");
        }
    }

    [Fact]
    public void GauntletTrio_SurvivesASnapshotRoundTrip()
    {
        // All three enemies plus a bolt in flight, snapshotted mid-encounter and
        // replayed. Covers the new EntityData usages: the action FSM's LockedAim
        // in the Aim slot and RailBolt's penetration counter in Budget.
        const int K = 40, N = 150;

        Simulation Build() => new(Floor(), new Vector2(40f, FloorTopY - 12f), g =>
        {
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Bastion, new Vector2(300f, FloorTopY - 14f)));
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Pouncer, new Vector2(140f, FloorTopY - 11f)));
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Latcher, new Vector2(200f, FloorTopY - 10f)));
        });

        PlayerInput At(int f) => new() { Right = f % 30 < 12, Space = f % 25 < 3 };

        var live = Build();
        for (int f = 0; f < K; f++) live.Step(At(f));
        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); liveTrace.Add(Probe(live)); }

        live.Restore(snap);
        var replayTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); replayTrace.Add(Probe(live)); }

        for (int i = 0; i < liveTrace.Count; i++)
        {
            if (liveTrace[i] != replayTrace[i])
            {
                output.WriteLine($"Divergence at replay frame {K + i}:");
                output.WriteLine("LIVE:\n"   + liveTrace[i]);
                output.WriteLine("REPLAY:\n" + replayTrace[i]);
            }
            Assert.Equal(liveTrace[i], replayTrace[i]);
        }
        output.WriteLine($"Round-trip identical across {liveTrace.Count} frames.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool HasKind(Simulation sim, EntityKind kind)
    {
        foreach (var e in sim.Entities) if (e.Kind == kind) return true;
        return false;
    }

    private static Entity FirstOfKind(Simulation sim, EntityKind kind)
    {
        foreach (var e in sim.Entities) if (e.Kind == kind) return e;
        Assert.Fail($"No {kind} in the simulation.");
        return null;
    }

    private static int CountSolid(ChunkMap chunks, int tx0, int tx1, int ty0, int ty1)
    {
        int n = 0;
        for (int tx = tx0; tx <= tx1; tx++)
        for (int ty = ty0; ty <= ty1; ty++)
            if (chunks.GetCellState(tx, ty) == TileState.Solid) n++;
        return n;
    }

    // Exact-float signature of the observable sim, same shape as
    // SnapshotRoundTripTests.Probe: any missed field shows up as a mismatch.
    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)};")
          .Append($"{Bits(p.Body.Velocity.X)},{Bits(p.Body.Velocity.Y)}|")
          .Append($"{p.CurrentStateName}/{p.CurrentActionName}|pct{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}\n");
        for (int ty = 0; ty <= 4; ty++)
        {
            sb.Append('T');
            for (int tx = -4; tx <= 30; tx++) sb.Append((int)sim.Chunks.GetCellState(tx, ty));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);
}
