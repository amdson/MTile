using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behaviour + determinism gates for the Frog Swarm (Entities/Enemies/Types/FrogEnemy.cs):
// frogs alternate resting and hopping, spread apart, divide up to the cap and no further,
// a mutant leaps onto a player and bursts, and the swarm survives a snapshot round-trip.
//
// Run just these:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~FrogEnemyTests"
public class FrogEnemyTests(ITestOutputHelper output)
{
    // Stone hall: floor at tile y = 3, 90 tiles wide (world x -44 .. 946), walled both ends.
    private static ChunkMap Hall()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 24; r++)
        {
            var row = new StringBuilder(new string('O', 90));
            for (int c = 0; c < 3; c++) { row[c] = 'X'; row[89 - c] = 'X'; }
            sb.Append(row).Append('\n');
        }
        for (int r = 0; r < 2; r++) sb.Append(new string('X', 90)).Append('\n');
        return SimTerrain.FromAscii(sb.ToString(), originTileX: -4, originTileY: -21);
    }

    private const float FloorTopY = 3 * Chunk.TileSize;
    private static Vector2 PlayerAt(float x) => new(x, FloorTopY - 12f);
    private static Vector2 FrogAt(float x)   => new(x, FloorTopY - FrogEnemy.Height * 0.5f - 1f);
    private static readonly PlayerInput Idle = default;

    private static Simulation Tough(Simulation sim)
    {
        sim.Player.MaxHealth = 1000f;
        sim.Player.Health    = 1000f;
        return sim;
    }

    private static List<FrogEnemy> Frogs(Simulation sim)
        => sim.Entities.OfType<FrogEnemy>().Where(f => !f.IsDead).ToList();

    private static void Run(Simulation sim, float seconds)
    {
        int n = SimFrames.FromSeconds(seconds, Simulation.FixedDt);
        for (int f = 0; f < n; f++) sim.Step(Idle);
    }

    [Fact]
    public void Frog_IsOneBlockTall_AndAlternatesRestingAndHopping()
    {
        // The player stands where the frog hops: a normal frog is harmless.
        var sim = Tough(new Simulation(Hall(), PlayerAt(500f), g => g.SpawnEntity(new FrogEnemy(FrogAt(500f)))));
        var frog = Frogs(sim)[0];
        Assert.Equal(Chunk.TileSize, frog.Body.Bounds.Height, 3);

        int hops = 0, restFrames = 0; var prev = frog.State; float highest = float.MaxValue;
        int n = SimFrames.FromSeconds(6f, Simulation.FixedDt);
        for (int f = 0; f < n; f++)
        {
            sim.Step(Idle);
            if (frog.State == FrogEnemy.FrogState.Hop && prev != FrogEnemy.FrogState.Hop) hops++;
            if (frog.State == FrogEnemy.FrogState.Rest) restFrames++;
            highest = System.Math.Min(highest, frog.Body.Position.Y);
            prev = frog.State;
        }
        output.WriteLine($"hops {hops}, rest frames {restFrames}/{n}, apex {FloorTopY - highest:F1} px above floor");
        Assert.True(hops >= 2, $"Only {hops} hops in 6 s.");
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
        Assert.True(restFrames > n / 4, "The frog barely rested.");
        Assert.True(FloorTopY - highest > 2f * Chunk.TileSize, "The hops never left the ground properly.");
    }

    [Fact]
    public void Frogs_SpreadApart()
    {
        // Six on one spot, far from the player.
        var sim = Tough(new Simulation(Hall(), PlayerAt(60f), g =>
        {
            for (int i = 0; i < 6; i++) g.SpawnEntity(new FrogEnemy(FrogAt(500f)));
        }));
        Run(sim, 6f);
        var xs = Frogs(sim).Where(f => !f.Mutant).Select(f => f.Body.Position.X).ToList();
        float span = xs.Max() - xs.Min();
        output.WriteLine($"{xs.Count} frogs span {span:F0} px after 6 s");
        Assert.True(span > 100f, $"Frogs stacked on one spot only spread to {span:F0} px.");
    }

    [Fact]
    public void Swarm_DividesPastTwentyFour_ThenOnlyMakesMutants_AndStaysUnderTheCap()
    {
        // The player is far away and tough: mutants that reach them burst, which is what
        // keeps the count moving.
        var sim = Tough(new Simulation(Hall(), PlayerAt(60f), g =>
        {
            for (int i = 0; i < 20; i++) g.SpawnEntity(new FrogEnemy(FrogAt(300f + 25f * i)));
        }));
        int max = 0, normalsBornAbove = 0, mutantsBorn = 0; var seen = new HashSet<EntityId>();
        foreach (var f0 in Frogs(sim)) seen.Add(f0.Id);
        int n = SimFrames.FromSeconds(90f, Simulation.FixedDt);
        for (int f = 0; f < n; f++)
        {
            int before = Frogs(sim).Count;
            sim.Step(Idle);
            var frogs = Frogs(sim);
            max = System.Math.Max(max, frogs.Count);
            foreach (var x in frogs)
            {
                if (!seen.Add(x.Id)) continue;
                if (x.Mutant) mutantsBorn++;
                else if (before > FrogEnemy.MutantsAbove) normalsBornAbove++;
            }
        }
        output.WriteLine($"peak {max} frogs, {mutantsBorn} mutants born, {normalsBornAbove} normal frogs born above {FrogEnemy.MutantsAbove}");
        Assert.True(max > FrogEnemy.MutantsAbove, $"The swarm never grew past {FrogEnemy.MutantsAbove} (peak {max}).");
        Assert.True(max <= FrogEnemy.Cap, $"Swarm reached {max}, over the cap of {FrogEnemy.Cap}.");
        Assert.True(mutantsBorn > 4, $"Only {mutantsBorn} mutants in 90 s.");
        Assert.Equal(0, normalsBornAbove);
    }

    [Fact]
    public void MutantBurst_BarelyMarksDirt()
    {
        var chunks = Hall();
        // The floor tile under the player is dirt.
        const int gx = 18, gy = 3;
        int cx = (int)System.MathF.Floor(gx / (float)Chunk.Size), cy = (int)System.MathF.Floor(gy / (float)Chunk.Size);
        Assert.True(chunks.TryGet(new Point(cx, cy), out var chunk));
        chunk.Tiles[gx - cx * Chunk.Size, gy - cy * Chunk.Size].Type = TileType.Dirt;
        var sim = Tough(new Simulation(chunks, PlayerAt(gx * Chunk.TileSize + 5f),
                        g => g.SpawnEntity(new FrogEnemy(FrogAt(gx * Chunk.TileSize - 120f), mutant: true, seed: 7))));
        Run(sim, 5f);
        output.WriteLine($"player took {sim.Player.Combat.DamageTaken:F2}; dirt cell now {sim.Chunks.GetCellState(gx, gy)}");
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "The mutant never burst.");
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(gx, gy));
    }

    [Fact]
    public void Mutant_LeapsOntoAStandingPlayerAndBursts()
    {
        var sim = Tough(new Simulation(Hall(), PlayerAt(200f),
                        g => g.SpawnEntity(new FrogEnemy(FrogAt(290f), mutant: true, seed: 7))));
        var m = Frogs(sim)[0];
        float dmg = 0f; int at = -1;
        int n = SimFrames.FromSeconds(5f, Simulation.FixedDt);
        for (int f = 0; f < n && at < 0; f++)
        {
            sim.Step(Idle);
            dmg = sim.Player.Combat.DamageTaken;
            if (dmg > 0f) at = f;
        }
        output.WriteLine($"player hit at frame {at} for {dmg:F2}; mutant dead {m.IsDead || m.Exploded}");
        Assert.True(at >= 0, "A mutant 90 px away never reached a standing player in 5 s.");
        Assert.Equal(FrogEnemy.BlastDamage, dmg, 3);
        sim.Step(Idle); sim.Step(Idle);
        Assert.Empty(Frogs(sim));
    }

    [Fact]
    public void Frogs_SurviveASnapshotRoundTrip()
    {
        const int K = 120, N = 1500;
        PlayerInput At(int f) => new() { Right = f % 50 < 20, Space = f % 45 < 3 };

        var live = Tough(new Simulation(Hall(), PlayerAt(200f), g =>
        {
            for (int i = 0; i < 6; i++) g.SpawnEntity(new FrogEnemy(FrogAt(300f + 40f * i)));
            g.SpawnEntity(new FrogEnemy(FrogAt(700f), mutant: true, seed: 3));
        }));
        for (int f = 0; f < K; f++) live.Step(At(f));
        var snap = live.Snapshot();

        var liveTrace = new List<string>();
        for (int f = K; f < N; f++) { live.Step(At(f)); liveTrace.Add(Probe(live)); }
        int grown = Frogs(live).Count;

        live.Restore(snap);
        for (int f = K; f < N; f++)
        {
            live.Step(At(f));
            Assert.Equal(liveTrace[f - K], Probe(live));
        }
        output.WriteLine($"Round-trip identical across {liveTrace.Count} frames; {grown} frogs at the end.");
        Assert.True(grown > 6, "No split happened inside the replayed window.");
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)}|dmg{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
        {
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}");
            if (e is FrogEnemy f) sb.Append($"|{f.State}|{f.Facing}");
            sb.Append('\n');
        }
        sb.Append($"T|{sim.Chunks.TerrainHash}\n");
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");

    // A player already running when the mutant plans its leap: over many seeds some
    // leaps are aimed at where the player is at that moment, and some ahead of them.
    [Fact]
    public void Mutant_AimsAtThePlayer_OrAheadOfThem()
    {
        int current = 0, ahead = 0, clamped = 0; const int Seeds = 24;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            int sd = seed;
            var sim = Tough(new Simulation(Hall(), PlayerAt(275f),
                            g => g.SpawnEntity(new FrogEnemy(FrogAt(300f), mutant: true, seed: (ulong)sd))));
            var m = Frogs(sim)[0];
            for (int f = 0; f < 120 && m.State != FrogEnemy.FrogState.Crouch && !m.Exploded; f++)
                sim.Step(new PlayerInput { Left = true });
            if (m.State != FrogEnemy.FrogState.Crouch) continue;
            // Undo LeapVelocity: |to.X| = 0.75 L / (1 - L / 600) for launch speed L.
            float L = System.Math.Abs(m.Launch.X);
            float aimDx = System.Math.Sign(m.Launch.X) * 0.75f * L / (1f - L / 600f);
            float toPlayer = sim.Player.Body.Position.X - m.Body.Position.X;
            // The player themselves out of reach: either aim is cut to the full range.
            if (System.Math.Abs(toPlayer) > FrogEnemy.MutantRange - 2f) { clamped++; continue; }
            if (System.Math.Abs(aimDx - toPlayer) < 1f) current++;
            else if (aimDx < toPlayer - 1f) ahead++;      // further left: the way they are running
        }
        output.WriteLine($"{Seeds} seeds: {current} aimed at the player, {ahead} ahead of them, {clamped} with the player out of range");
        Assert.True(current > 0, "No leap was aimed at the player's current position.");
        Assert.True(ahead > 0, "No leap was aimed ahead of a running player.");
    }

    // The hall with a block of stone standing on the floor: columns [x0, x0 + w), `h` high.
    private static ChunkMap HallWithBlock(int x0, int w, int h, int secondX0 = -1)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 24; r++)
        {
            var row = new StringBuilder(new string('O', 90));
            for (int c = 0; c < 3; c++) { row[c] = 'X'; row[89 - c] = 'X'; }
            if (r >= 24 - h) for (int c = 0; c < w; c++) { row[x0 + 4 + c] = 'X'; if (secondX0 >= 0) row[secondX0 + 4 + c] = 'X'; }
            sb.Append(row).Append('\n');
        }
        for (int r = 0; r < 2; r++) sb.Append(new string('X', 90)).Append('\n');
        return SimTerrain.FromAscii(sb.ToString(), originTileX: -4, originTileY: -21);
    }

    [Fact]
    public void Frog_ClimbsOntoAHigherLedge()
    {
        // A 3-high, 8-wide plateau at tiles 40..47; four frogs start within sight of it
        // (any one hop has a ClimbChance, so one frog alone can wander off first).
        var sim = Tough(new Simulation(HallWithBlock(40, 8, 3), PlayerAt(60f), g =>
        {
            for (int i = 0; i < 4; i++) g.SpawnEntity(new FrogEnemy(FrogAt((36 + i) * Chunk.TileSize + 5f)));
        }));
        float plateauTop = FloorTopY - 3 * Chunk.TileSize;
        int onTop = -1;
        int n = SimFrames.FromSeconds(12f, Simulation.FixedDt);
        for (int f = 0; f < n && onTop < 0; f++)
        {
            sim.Step(Idle);
            var frog = Frogs(sim).FirstOrDefault(x => !x.Mutant && x.State == FrogEnemy.FrogState.Rest
                                                      && x.Body.Bounds.Bottom < plateauTop + 2f);
            if (frog != null) onTop = f;
        }
        output.WriteLine($"resting on the plateau at frame {onTop}");
        Assert.True(onTop >= 0, "No frog got onto a 3-high ledge beside it in 12 s.");
        // Level stone is not a player block: the plateau is intact.
        for (int gx = 40; gx < 48; gx++) Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(gx, 0));
    }

    [Fact]
    public void Wall_StopsFrogs_UntilTonguesEatIt_AndFlushBlocksAreLeftAlone()
    {
        // A 2-wide, 6-high player-owned pillar at tiles 30..31 (too tall to hop onto), an
        // identical level pillar at 50..51, and a player-owned patch of floor between.
        var chunks = HallWithBlock(30, 2, 6, secondX0: 50);
        for (int gx = 30; gx < 32; gx++) for (int gy = -3; gy < 3; gy++) chunks.Ownership.Stamp(gx, gy, BlockOwnership.Player1, 0);
        for (int gx = 34; gx < 48; gx++) chunks.Ownership.Stamp(gx, 3, BlockOwnership.Player1, 0);   // floor: flush

        var sim = Tough(new Simulation(chunks, PlayerAt(60f), g =>
        {
            for (int i = 0; i < 6; i++) g.SpawnEntity(new FrogEnemy(FrogAt((33 + 3 * i) * Chunk.TileSize)));
        }));
        int Count(int x0)
        {
            int c = 0;
            for (int gy = -3; gy < 3; gy++) for (int gx = x0; gx < x0 + 2; gx++)
                if (sim.Chunks.GetCellState(gx, gy) == TileState.Solid) c++;
            return c;
        }
        float leftmost = float.MaxValue;
        int n2 = SimFrames.FromSeconds(2f, Simulation.FixedDt);
        for (int f = 0; f < n2; f++) { sim.Step(Idle); foreach (var x in Frogs(sim)) leftmost = System.Math.Min(leftmost, x.Body.Position.X); }
        int early = Count(30);
        Run(sim, 60f);
        int late = Count(30), level = Count(50), floor = 0;
        for (int gx = 34; gx < 48; gx++) if (sim.Chunks.GetCellState(gx, 3) == TileState.Solid) floor++;
        output.WriteLine($"player pillar: {early}/12 after 2 s, {late}/12 after 62 s; level pillar {level}/12; owned floor {floor}/14; leftmost frog in the first 2 s x={leftmost:F0}");
        Assert.True(early >= 10, "The wall was gone almost at once.");
        Assert.True(leftmost > 32 * Chunk.TileSize, "A frog passed through the wall.");
        Assert.True(late < early, "No tongue ever ate a player block.");
        Assert.Equal(12, level);
        Assert.Equal(14, floor);
    }

    [Fact]
    public void Mutant_HaltedByAWall_BurstsAgainstIt()
    {
        // A 2-wide, 10-high level pillar at tiles 30..31 between the mutant and the player.
        var sim = Tough(new Simulation(HallWithBlock(30, 2, 10), PlayerAt(200f),
                        g => g.SpawnEntity(new FrogEnemy(FrogAt(460f), mutant: true, seed: 7))));
        var m = Frogs(sim)[0];
        Vector2 at = default; int frame = -1;
        int n = SimFrames.FromSeconds(4f, Simulation.FixedDt);
        for (int f = 0; f < n && frame < 0; f++)
        {
            sim.Step(Idle);
            if (m.Exploded) { frame = f; at = m.Body.Position; }
        }
        output.WriteLine($"burst at frame {frame}, x={at.X:F0} (wall face x={32 * Chunk.TileSize}); player took {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(frame >= 0, "The mutant never burst against the wall.");
        Assert.InRange(at.X, 32 * Chunk.TileSize, 32 * Chunk.TileSize + FrogEnemy.Width);
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }
}
