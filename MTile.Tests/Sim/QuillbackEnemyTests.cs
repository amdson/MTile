using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behaviour + determinism gates for the Quillback boss (Entities/Enemies/Types/QuillbackEnemy.cs):
// the shell soaks hits and refills; the quill storm fires only at range and a hit
// during it is a massive punish; it mauls a player in reach; it builds up to a player
// on a ledge and digs through a wall; and the whole thing survives a snapshot round-trip.
//
// Run just these:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~QuillbackEnemyTests"
public class QuillbackEnemyTests(ITestOutputHelper output)
{
    // Flat stone floor, solid at tile y = 3, 70 tiles wide (world x -44 .. 726).
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float FloorTopY = 3 * Chunk.TileSize;
    private static readonly Vector2 PlayerAt = new(60f, FloorTopY - 12f);
    private static Vector2 BossAt(float x) => new(x, FloorTopY - QuillbackEnemy.HalfHeight - 1f);

    private static readonly PlayerInput Idle = default;

    private static Hitbox Slash(float damage) => new(
        new BoundingBox(-1000f, -1000f, 1000f, 1000f), hitId: 999_999, damage,
        new Vector2(200f, -100f), Faction.Player1, EntityId.None);

    [Fact]
    public void Shell_SoaksHitsThenSpillsIntoHpAndRefills()
    {
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new QuillbackEnemy(BossAt(600f))));
        var boss = First(sim);

        boss.OnHit(Slash(1f), default);
        Assert.Equal(QuillbackEnemy.MaxHp, boss.Health);
        Assert.Equal(QuillbackEnemy.ShieldMax - 1f, boss.Shield, 3);

        boss.OnHit(Slash(QuillbackEnemy.ShieldMax), default);           // 5 left in the shell: 1 spills
        Assert.Equal(0f, boss.Shield);
        Assert.Equal(QuillbackEnemy.MaxHp - 1f, boss.Health, 3);

        // Broken: stays down through BrokenDelay, then refills fast.
        int down = SimFrames.FromSeconds(QuillbackEnemy.BrokenDelay * 0.8f, Simulation.FixedDt);
        for (int f = 0; f < down; f++) sim.Step(Idle);
        Assert.Equal(0f, boss.Shield);
        int up = SimFrames.FromSeconds(QuillbackEnemy.BrokenDelay * 0.2f + QuillbackEnemy.ShieldMax / QuillbackEnemy.ShieldRegen + 0.3f,
                                       Simulation.FixedDt);
        for (int f = 0; f < up; f++) sim.Step(Idle);
        output.WriteLine($"shell {boss.Shield:F2}, hp {boss.Health:F2}");
        Assert.Equal(QuillbackEnemy.ShieldMax, boss.Shield, 3);
    }

    [Fact]
    public void Storm_FiresOnlyAtRange_AndHitsThePlayer()
    {
        // Far start: the first decision is the storm.
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new QuillbackEnemy(BossAt(60f + 250f))));
        var boss = First(sim);
        string prev = "";
        var starts = new List<float>();
        float stormDamage = -1f;
        for (int f = 0; f < 60 * 12; f++)
        {
            sim.Step(Idle);
            string now = boss.CurrentActionName;
            if (now == nameof(QuillbackStormAction) && prev != now)
                starts.Add(Vector2.Distance(boss.Body.Position, sim.Player.Body.Position));
            if (now == nameof(QuillbackStormAction) && stormDamage < 0f && sim.Player.Combat.DamageTaken > 0f)
                stormDamage = sim.Player.Combat.DamageTaken;
            prev = now;
        }
        output.WriteLine($"storms at distances: {string.Join(", ", starts.ConvertAll(d => d.ToString("F0")))}; first storm damage {stormDamage:F2}");
        Assert.NotEmpty(starts);
        Assert.True(stormDamage > 0f, "The quill ring never hit the player.");
        var brain = new QuillbackController();
        foreach (var d in starts)
            Assert.True(d >= brain.FarRange - 2f, $"Storm started at {d:F0}px, inside FarRange {brain.FarRange}.");
    }

    [Fact]
    public void Storm_HitWhileOpen_TakesMassiveDamageAndIsDazed()
    {
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new QuillbackEnemy(BossAt(60f + 250f))));
        var boss = First(sim);
        for (int f = 0; f < 60 && !boss.IsOpen; f++) sim.Step(Idle);
        Assert.True(boss.IsOpen, "Never saw the storm open.");
        sim.Step(Idle);

        float hp = boss.Health;
        boss.OnHit(Slash(0.5f), default);
        output.WriteLine($"hp {hp:F2} → {boss.Health:F2}, shell {boss.Shield:F2}");
        Assert.Equal(hp - 0.5f * QuillbackEnemy.VulnerableMultiplier, boss.Health, 3);
        Assert.Equal(0f, boss.Shield);

        sim.Step(Idle);
        Assert.True(boss.IsDazed, "The broken storm didn't daze it.");
        for (int f = 0; f < 30; f++) sim.Step(Idle);
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);                 // a broken storm throws nothing
        Assert.Equal(nameof(QuillbackStormAction), boss.CurrentActionName);
    }

    [Fact]
    public void Quillback_MaulsAPlayerInReach()
    {
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new QuillbackEnemy(BossAt(60f + 30f))));
        var boss = First(sim);
        var seen = new HashSet<string>();
        for (int f = 0; f < 60 * 4; f++)
        {
            sim.Step(Idle);
            if (boss.CurrentActionName != "") seen.Add(boss.CurrentActionName);
        }
        output.WriteLine($"damage {sim.Player.Combat.DamageTaken:F2}; actions {string.Join(",", seen)}");
        Assert.True(sim.Player.Combat.DamageTaken >= 1f, "It barely touched a player standing in reach.");
        Assert.DoesNotContain(nameof(QuillbackStormAction), seen);
    }

    // The floor with a 3-tile stone platform (cols 8..12, rows 0..2); its top is y = 0.
    private static ChunkMap Ledge() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOXXXXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXXXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXXXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    [Fact]
    public void Quillback_BuildsUpToAPlayerOnALedge()
    {
        var playerAt = new Vector2(10 * Chunk.TileSize + 5f, -12f);       // standing on the platform
        var sim  = new Simulation(Ledge(), playerAt, g => g.SpawnEntity(new QuillbackEnemy(BossAt(300f))));
        var boss = First(sim);
        float groundY = FloorTopY - QuillbackEnemy.HalfHeight;
        float minY = groundY;
        bool pillared = false;
        int reachedAt = -1;
        for (int f = 0; f < 60 * 12; f++)
        {
            sim.Step(Idle);
            if (boss.CurrentActionName == nameof(QuillbackPillarAction)) pillared = true;
            minY = System.MathF.Min(minY, boss.Body.Position.Y);
            if (reachedAt < 0 && boss.Body.Position.Y < -QuillbackEnemy.HalfHeight + 3f && boss.Body.Velocity.Y >= 0f) reachedAt = f;
        }
        output.WriteLine($"pillared {pillared}, boss min y {minY:F0} (ground {groundY:F0}), on top at frame {reachedAt}, player dmg {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(pillared, "It never built a pillar.");
        Assert.True(reachedAt >= 0, $"It never got up to the ledge top (min y {minY:F0}).");
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "It got up but never hit the player.");
    }

    // A floor with a full-height stone wall at tile column 13 (world x 143..154).
    private static ChunkMap WalledFloor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOXOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: -6);

    [Fact]
    public void Quillback_DigsThroughAWallToReachThePlayer()
    {
        var sim  = new Simulation(WalledFloor(), PlayerAt, g => g.SpawnEntity(new QuillbackEnemy(BossAt(300f))));
        var boss = First(sim);
        int openAt = -1, throughAt = -1;
        for (int f = 0; f < 60 * 10 && throughAt < 0; f++)
        {
            sim.Step(Idle);
            if (openAt < 0 && (sim.Chunks.GetCellState(13, 1) == TileState.Empty || sim.Chunks.GetCellState(13, 2) == TileState.Empty))
                openAt = f;
            if (boss.Body.Position.X < 13 * Chunk.TileSize) throughAt = f;
        }
        output.WriteLine($"wall opened at {openAt}, boss through at {throughAt}, x {boss.Body.Position.X:F0}");
        Assert.True(openAt >= 0, "It never dug into the wall.");
        Assert.True(throughAt >= 0, $"It never got through the wall (x {boss.Body.Position.X:F0}).");
    }

    // ── Hidden player: it must never lose the player behind terrain ──────────
    // A walled room (cols 0..59, rows -16..20; floor top at row 3) with the player
    // stashed somewhere the plain chase can't see or reach. The boss starts at the left
    // end on the floor; within the time limit it has to dig, build or climb its way to
    // the player and land a hit.
    private const int Cols = 60, Top = -16, Bottom = 20;

    private static ChunkMap Room(System.Func<int, int, bool> solid)
    {
        var sb = new StringBuilder();
        for (int y = Top; y <= Bottom; y++)
        {
            for (int x = 0; x < Cols; x++)
                sb.Append(x == 0 || x == Cols - 1 || y == Bottom || solid(x, y) ? 'X' : 'O');
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString(), originTileX: 0, originTileY: Top);
    }

    private static bool Floor(int x, int y) => y >= 3;
    private static Vector2 StandOn(int col, int row) => new((col + 0.5f) * Chunk.TileSize, row * Chunk.TileSize - 12f);

    public static TheoryData<string> Hideouts => new() { "box", "pit", "deep-pit", "platform", "tower", "overhang" };

    private static (ChunkMap, Vector2) Hideout(string name) => name switch
    {
        // Sealed stone box on the floor, far off.
        "box" => (Room((x, y) => Floor(x, y) || ((x == 40 || x == 45) && y >= -2 && y <= 2) || (y == -2 && x >= 40 && x <= 45)),
                  StandOn(42, 3)),
        // Sealed cavity under the floor (rows 7..10), standing on row 11.
        "pit" => (Room((x, y) => Floor(x, y) && !(x >= 38 && x <= 42 && y >= 7 && y <= 10)), StandOn(40, 11)),
        // The same, much deeper (rows 14..17): well past the old dig range.
        "deep-pit" => (Room((x, y) => Floor(x, y) && !(x >= 38 && x <= 42 && y >= 14 && y <= 17)), StandOn(40, 18)),
        // A floating one-tile stone slab eight tiles up.
        "platform" => (Room((x, y) => Floor(x, y) || (y == -5 && x >= 34 && x <= 42)), StandOn(38, -5)),
        // A sheer stone tower, nine tiles tall.
        "tower" => (Room((x, y) => Floor(x, y) || (x >= 40 && x <= 43 && y >= -6)), StandOn(41, -6)),
        // A high shelf (row -9) with a roof slab hanging two tiles above it.
        "overhang" => (Room((x, y) => Floor(x, y) || (y == -9 && x >= 36 && x <= 44) || (y == -13 && x >= 30 && x <= 44)),
                       StandOn(40, -9)),
        _ => throw new System.ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Hideouts))]
    public void Quillback_DigsOrBuildsToAHiddenPlayer(string hideout)
    {
        var (terrain, playerAt) = Hideout(hideout);
        var sim  = new Simulation(terrain, playerAt, g => g.SpawnEntity(new QuillbackEnemy(new Vector2(6 * Chunk.TileSize, FloorTopY - QuillbackEnemy.HalfHeight - 1f))));
        var boss = First(sim);
        int hitAt = -1;
        float closest = float.MaxValue;
        var used = new HashSet<string>();
        for (int f = 0; f < 60 * 25 && hitAt < 0; f++)
        {
            sim.Step(Idle);
            if (boss.CurrentActionName != "") used.Add(boss.CurrentActionName.Replace("Quillback", "").Replace("Action", ""));
            float d = Vector2.Distance(boss.Body.Position, sim.Player.Body.Position);
            closest = System.MathF.Min(closest, d);
            // Reached: in melee range with nothing in between. (A ranged storm hit
            // doesn't count, and nor does pressing up against the far side of a slab.)
            if (d <= 40f && EnemyAim.HasLineOfSight(boss.Body.Position, sim.Player.Body.Position, sim.Chunks, 14f))
                hitAt = f;
        }
        output.WriteLine($"{hideout}: reached at {(hitAt < 0 ? "never" : $"{hitAt / 60f:F1}s")}, closest {closest:F0}px, " +
                         $"boss at {boss.Body.Position.X:F0},{boss.Body.Position.Y:F0} player {sim.Player.Body.Position.X:F0},{sim.Player.Body.Position.Y:F0}; used {string.Join(",", used)}");
        Assert.True(hitAt >= 0, $"It never reached the player in the {hideout} (closest {closest:F0}px).");
    }

    [Fact]
    public void Quillback_SurvivesASnapshotRoundTrip()
    {
        const int K = 90, N = 600;

        Simulation Build() => new(Ledge(), PlayerAt, g => g.SpawnEntity(new QuillbackEnemy(BossAt(320f))));

        // Restless input so the pair close, separate, and every move gets a chance.
        PlayerInput At(int f) => new() { Right = f % 160 < 50, Left = f % 160 >= 90, Space = f % 45 < 3 };

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

    private static QuillbackEnemy First(Simulation sim)
    {
        foreach (var e in sim.Entities) if (e is QuillbackEnemy q) return q;
        Assert.Fail("No Quillback in the simulation.");
        return null;
    }

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)};")
          .Append($"{Bits(p.Body.Velocity.X)},{Bits(p.Body.Velocity.Y)}|")
          .Append($"{p.CurrentStateName}/{p.CurrentActionName}|dmg{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
        {
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}");
            if (e is QuillbackEnemy q)
                sb.Append($"|{q.CurrentActionName}|sh{Bits(q.Shield)}|s{q.Scratch.I0},{q.Scratch.I1},{Bits(q.Scratch.F0)},{Bits(q.Scratch.F1)},{Bits(q.Scratch.F2)}");
            sb.Append('\n');
        }
        sb.Append($"T|{sim.Chunks.TerrainHash}\n");
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");
}
