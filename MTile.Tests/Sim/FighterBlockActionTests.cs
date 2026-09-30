using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase 3 slice of Plans/FIGHTER_DESIGN_PLAN.md: the Builder's two terrain-placing pool
// actions (EnemyPlaceBlockAction, EnemySpawnBlockInAirAction). Pins:
//
//   * PlaceBlock lays one tile of Spec.Material at the cell Reach px along the locked
//     aim, on the windup→active transition frame and not before.
//   * Energy drops by exactly Spec.EnergyCost at Enter, and a meter that can no longer
//     afford the cost never fires again (regen 0).
//   * SpawnBlockInAir conjures the tile above where the target WAS at Enter — a target
//     that walks during the windup leaves the block hanging over empty floor.
//   * A snapshot mid-windup replays bit-identically, including the terrain: the tile
//     appears on the same replay frame (the destination rides in EntityData.Aim).
public class FighterBlockActionTests(ITestOutputHelper output)
{
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const int   FloorRow  = 8;
    private const float FloorTopY = FloorRow * Chunk.TileSize;
    private static readonly PlayerInput Idle     = default;
    private static readonly PlayerInput WalkLeft = new() { Left = true };

    // A planted turret with one action. Regen 0 so every unit spent stays spent.
    private static EnemyBlueprint Builder(EnemyActionState action, float energyMax, int holdFrames = 0) => new()
    {
        Kind          = EntityKind.Sparring,
        Radius        = 10f,
        Health        = 4f,
        Mass          = 1f,
        FrictionScale = 0.10f,
        EnergyMax     = energyMax,
        EnergyRegen   = 0f,
        Movement   = () => new() { new EnemyIdleState() },
        Actions    = () => new() { action },
        Controller = new HoldThenAim { HoldFrames = holdFrames },
    };

    private static Simulation Build(EnemyBlueprint bp, float enemyX, float playerX) =>
        new(Floor(), new Vector2(playerX, FloorTopY - 12f),
            g => g.SpawnEntity(new BlueprintEnemy(bp, new Vector2(enemyX, FloorTopY - 11f))));

    private static (int, int) Cell(Vector2 w)
        => ((int)MathF.Floor(w.X / Chunk.TileSize), (int)MathF.Floor(w.Y / Chunk.TileSize));

    // Every non-empty cell above the floor, in scan order.
    private static List<(int gtx, int gty)> AirTiles(ChunkMap c)
    {
        var list = new List<(int, int)>();
        for (int gty = 0; gty < FloorRow; gty++)
        for (int gtx = -4; gtx < 46; gtx++)
            if (c.GetCellState(gtx, gty) != TileState.Empty) list.Add((gtx, gty));
        return list;
    }

    // ── (a) PlaceBlock lays the tile at the aimed cell, on the transition ───

    [Fact]
    public void PlaceBlock_LaysMaterialAtTheAimedCellOnTheTransition()
    {
        var spec = ActionSpec.Default(ActionKind.PlaceBlock);
        spec.Material = TileType.Stone;
        spec.EnergyCost = MaterialStrengths.BuildCostFor(TileType.Stone);

        var sim = Build(Builder(new EnemyPlaceBlockAction(spec), energyMax: 100f), enemyX: 160f, playerX: 60f);
        var bot = (EnemyEntity)sim.Entities[0];

        int enter = StepUntilAction(sim, nameof(EnemyPlaceBlockAction));
        // Planted and aimed at the player on the same floor: Reach px toward -x, settled
        // onto the floor row (a body centre sits right at a row boundary, so the raw
        // aimed point can land one row up).
        var dir = Vector2.Normalize(sim.Player.Body.Position - bot.Body.Position);
        var (gtx, _) = Cell(bot.Body.Position + dir * spec.Reach);
        int gty = FloorRow - 1;

        int windupFrames = (int)MathF.Ceiling(spec.Windup / Simulation.FixedDt);
        int placedAt = -1;
        for (int f = 1; f <= windupFrames + (int)MathF.Ceiling(spec.Active / Simulation.FixedDt) + 1; f++)
        {
            sim.Step(Idle);
            if (sim.Chunks.GetCellState(gtx, gty) != TileState.Empty) { placedAt = f; break; }
        }
        Assert.True(placedAt > 0, $"no tile at ({gtx},{gty}); air tiles: {string.Join(" ", AirTiles(sim.Chunks))}");
        Assert.InRange(placedAt, windupFrames - 1, windupFrames + 1);
        Assert.Equal(TileType.Stone, sim.Chunks.GetCellType(gtx, gty));
        Assert.Single(AirTiles(sim.Chunks));

        // Sprout path: Sprouting for SproutLifetime, then Solid.
        for (int f = 0; f < 30; f++) sim.Step(Idle);
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(gtx, gty));
        output.WriteLine($"entered @{enter}, tile at ({gtx},{gty}) {placedAt} frames later");
    }

    // ── (b) Energy: exact spend at Enter, no action once unaffordable ───────

    [Fact]
    public void PlaceBlock_SpendsExactCostAtEnter_AndStopsWhenUnaffordable()
    {
        var spec = ActionSpec.Default(ActionKind.PlaceBlock);
        float cost = spec.EnergyCost;
        Assert.Equal(MaterialStrengths.BuildCostFor(spec.Material), cost);   // honest default row
        Assert.True(cost > 0f);

        float max = cost * 2.5f;   // two uses, then 0.5 × cost left over forever
        var sim = Build(Builder(new EnemyPlaceBlockAction(spec), energyMax: max), enemyX: 160f, playerX: 60f);
        var bot = (EnemyEntity)sim.Entities[0];

        // Detect Enter by the meter moving, not by the action name: the brain always
        // wants to attack, so the second use re-enters on the very frame the first exits.
        var enters = new List<int>();
        int lastActive = -1;
        for (int f = 0; f < 400; f++)
        {
            float before = bot.Energy;
            sim.Step(Idle);
            if (bot.Energy != before)
            {
                Assert.Equal(before - cost, bot.Energy);   // exactly the spec's cost, once
                Assert.Equal(nameof(EnemyPlaceBlockAction), bot.CurrentActionName);
                enters.Add(f);
            }
            if (bot.CurrentActionName != "") lastActive = f;
        }
        Assert.Equal(2, enters.Count);
        Assert.Equal(max - cost - cost, bot.Energy);
        Assert.True(bot.Energy < cost);
        // Two uses last ~84 frames; the remaining ~300 must be idle — unaffordable never fires.
        Assert.True(lastActive < 120, $"an action was still running at frame {lastActive} on an empty meter");
        // The second use found its aimed cell filled and stacked on top: a two-high wall.
        var tiles = AirTiles(sim.Chunks);
        Assert.Equal(2, tiles.Count);
        Assert.Equal(tiles[0].gtx, tiles[1].gtx);
        Assert.Equal(new[] { FloorRow - 2, FloorRow - 1 }, new[] { tiles[0].gty, tiles[1].gty });
        output.WriteLine($"enters at {string.Join(", ", enters)}; energy left {bot.Energy:F3} (cost {cost:F3})");
    }

    // ── (c) SpawnBlockInAir aims where the target WAS ───────────────────────

    [Fact]
    public void SpawnBlockInAir_UsesTheTargetPositionAtEnter()
    {
        var spec = ActionSpec.Default(ActionKind.SpawnBlockInAir);
        var sim = Build(Builder(new EnemySpawnBlockInAirAction(spec), energyMax: 100f, holdFrames: 30),
                        enemyX: 380f, playerX: 200f);
        var bot = (EnemyEntity)sim.Entities[0];

        // Walk left the whole time; the brain holds fire until the player is at speed.
        // Entities update after the player's step logic but before integration, so
        // Enter on step f reads the position the player had at the end of step f − 1.
        var after = new List<Vector2>();
        int enter = -1;
        for (int f = 0; f < 120 && enter < 0; f++)
        {
            sim.Step(WalkLeft);
            after.Add(sim.Player.Body.Position);
            if (bot.CurrentActionName == nameof(EnemySpawnBlockInAirAction)) enter = f;
        }
        Assert.True(enter > 0, "the air block never started");
        var atEnter = after[enter - 1];
        var (gtx, gty) = Cell(atEnter - new Vector2(0f, spec.HalfHeight));

        int windupFrames = (int)MathF.Ceiling(spec.Windup / Simulation.FixedDt);
        for (int f = 0; f < windupFrames + 2; f++) sim.Step(WalkLeft);

        var tiles = AirTiles(sim.Chunks);
        Assert.True(tiles.Count == 1 && tiles[0] == (gtx, gty),
            $"expected one tile at ({gtx},{gty}) over the Enter position; air tiles: {string.Join(" ", tiles)}");
        Assert.Equal(spec.Material, sim.Chunks.GetCellType(gtx, gty));

        // ...and the player really did leave: the tile is not over them now.
        var (nowX, _) = Cell(sim.Player.Body.Position);
        Assert.True(gtx - nowX >= 3,
            $"player only moved from cell {Cell(atEnter).Item1} to {nowX}; the test proves nothing");

        // No gravity on terrain: the unsupported tile stays put once it solidifies.
        for (int f = 0; f < 60; f++) sim.Step(Idle);
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(gtx, gty));
        output.WriteLine($"enter @{enter}: player x {atEnter.X:F1} → {sim.Player.Body.Position.X:F1}; tile ({gtx},{gty})");
    }

    [Fact]
    public void SpawnBlockInAir_RefusesAnOccupiedDestination()
    {
        var spec = ActionSpec.Default(ActionKind.SpawnBlockInAir);
        var sim = Build(Builder(new EnemySpawnBlockInAirAction(spec), energyMax: 100f, holdFrames: 30),
                        enemyX: 380f, playerX: 200f);
        var bot = (EnemyEntity)sim.Entities[0];
        for (int f = 0; f < 20; f++) sim.Step(Idle);   // brain holding fire while the player settles
        // Fill the cell over the parked player; the precondition must now fail.
        var (gtx, gty) = Cell(sim.Player.Body.Position - new Vector2(0f, spec.HalfHeight));
        Assert.NotNull(sim.Chunks.ForceSprout(gtx, gty, TileType.Stone));
        float energy = bot.Energy;
        for (int f = 0; f < 60; f++) sim.Step(Idle);
        Assert.Equal("", bot.CurrentActionName);
        Assert.Equal(energy, bot.Energy);
    }

    // ── (d) Snapshot mid-windup round-trips, terrain included ───────────────

    [Theory]
    [InlineData(ActionKind.PlaceBlock)]
    [InlineData(ActionKind.SpawnBlockInAir)]
    public void SnapshotMidWindup_ReplaysTheTileOnTheSameFrame(ActionKind kind)
    {
        var spec = ActionSpec.Default(kind);
        spec.Windup = 0.60f;
        EnemyActionState action = kind == ActionKind.PlaceBlock
            ? new EnemyPlaceBlockAction(spec) : new EnemySpawnBlockInAirAction(spec);
        string name = action.GetType().Name;

        // The walking target is what makes the air block's frozen destination matter:
        // re-deriving it from the live player after the restore would move the tile.
        var input = kind == ActionKind.PlaceBlock ? Idle : WalkLeft;
        var live = kind == ActionKind.PlaceBlock
            ? Build(Builder(action, 100f), enemyX: 160f, playerX: 60f)
            : Build(Builder(action, 100f, holdFrames: 20), enemyX: 380f, playerX: 200f);
        var bot = (EnemyEntity)live.Entities[0];

        int f = 0;
        for (; f < 300 && bot.CurrentActionName != name; f++) live.Step(input);
        Assert.True(f < 300, $"{name} never started");
        for (int i = 0; i < 8; i++, f++) live.Step(input);
        Assert.Equal(name, bot.CurrentActionName);
        Assert.Empty(AirTiles(live.Chunks));   // still winding up

        const int N = 90;
        var snap = live.Snapshot();
        var liveTrace = new List<string>();
        var liveSums  = new List<ulong>();
        for (int i = 0; i < N; i++) { live.Step(input); liveTrace.Add(Probe(live)); liveSums.Add(live.Checksum()); }
        int liveTileFrame = liveTrace.FindIndex(s => s.Contains("T("));
        Assert.True(liveTileFrame >= 0, "no tile appeared after the snapshot, so the round trip proves nothing");

        live.Restore(snap);
        var replayTrace = new List<string>();
        var replaySums  = new List<ulong>();
        for (int i = 0; i < N; i++) { live.Step(input); replayTrace.Add(Probe(live)); replaySums.Add(live.Checksum()); }

        for (int i = 0; i < N; i++)
        {
            if (liveTrace[i] != replayTrace[i])
            {
                output.WriteLine($"Divergence at replay step {i} (sim frame {f + i}):");
                output.WriteLine("LIVE:\n"   + liveTrace[i]);
                output.WriteLine("REPLAY:\n" + replayTrace[i]);
            }
            Assert.Equal(liveTrace[i], replayTrace[i]);
            Assert.Equal(liveSums[i],  replaySums[i]);
        }
        output.WriteLine($"{name}: identical across {N} frames; tile appeared {liveTileFrame} frames after the snapshot.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Holds fire for the first `HoldFrames` frames (read off ctx.Frame — no state of
    // its own), then aims at the player like StationaryAimController.
    private sealed class HoldThenAim : EnemyController
    {
        public int HoldFrames { get; init; }
        public override EnemyInput Decide(in EnemyContext ctx) => new()
        {
            AimWorld   = ctx.Player.Body.Position,
            WantAttack = ctx.Frame > HoldFrames,
        };
    }

    private static int StepUntilAction(Simulation sim, string name)
    {
        var bot = (EnemyEntity)sim.Entities[0];
        for (int f = 0; f < 240; f++)
        {
            sim.Step(Idle);
            if (bot.CurrentActionName == name) return f;
        }
        Assert.Fail($"{name} never started");
        return -1;
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)};")
          .Append($"{Bits(p.Body.Velocity.X)},{Bits(p.Body.Velocity.Y)}|")
          .Append($"{p.CurrentStateName}/{p.CurrentActionName}\n");
        foreach (var e in sim.Entities)
        {
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}");
            if (e is EnemyEntity en) sb.Append($"|{en.CurrentActionName}|e{Bits(en.Energy)}");
            sb.Append('\n');
        }
        // Terrain probe: every non-empty cell above the floor, with state and type.
        foreach (var (gtx, gty) in AirTiles(sim.Chunks))
            sb.Append($"T({gtx},{gty}):{sim.Chunks.GetCellState(gtx, gty)}/{sim.Chunks.GetCellType(gtx, gty)}\n");
        return sb.ToString();
    }

    private static string Bits(float v) => BitConverter.SingleToInt32Bits(v).ToString("X8");
}
