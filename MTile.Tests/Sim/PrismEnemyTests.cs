using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behaviour + determinism gates for the Prism boss (Entities/Enemies/Types/PrismEnemy.cs)
// and its ray emitters (Entities/RayEmitter.cs): a ray is stopped by a wall and then eats
// through it; the boss dashes toward the player; an emitter that lives out its life
// explodes; and the whole thing survives a snapshot round-trip.
//
// Run just these:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~PrismEnemyTests"
public class PrismEnemyTests(ITestOutputHelper output)
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
    private static Vector2 BossAt(float x) => new(x, FloorTopY - PrismEnemy.FootDrop - 1f);

    private static readonly PlayerInput Idle = default;

    // A floor with a full-height stone wall at tile column 13 (world x 143..154), the
    // cone's whole height. Rows run -6..2 above the floor at row 3.
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
    public void Ray_IsStoppedByAWallAndThenEatsThroughIt()
    {
        // Emitter at the player's height aimed straight right through the wall; the
        // player 150 px behind the wall, inside the cone.
        var at = new Vector2(60f, FloorTopY - 12f);
        var playerAt = new Vector2(300f, FloorTopY - 12f);
        var sim = new Simulation(WalledFloor(), playerAt, g => g.SpawnEntity(
            new RayEmitter(at, EntityId.None, Teams.Enemies, angle: 0f,
                           halfAngle: MathHelper.ToRadians(6f), range: 900f, telegraph: 1.0f, life: 30f)));

        int firing = SimFrames.FromSeconds(1.0f, Simulation.FixedDt);
        int breakFrame = -1, damageFrame = -1;
        for (int f = 0; f < 60 * 9; f++)
        {
            sim.Step(Idle);
            bool open = sim.Chunks.GetCellState(13, 1) == TileState.Empty || sim.Chunks.GetCellState(13, 2) == TileState.Empty;
            if (open && breakFrame < 0) breakFrame = f;
            if (sim.Player.Combat.DamageTaken > 0f && damageFrame < 0) damageFrame = f;
            if (breakFrame >= 0 && damageFrame >= 0) break;
        }
        output.WriteLine($"firing from {firing}, wall opened at {breakFrame}, first damage at {damageFrame}");

        Assert.True(breakFrame > firing + 10, $"The wall should take time to eat (opened at frame {breakFrame}, firing from {firing}).");
        Assert.True(damageFrame >= 0, "The player behind the wall was never reached once it opened.");
        Assert.True(damageFrame >= breakFrame, $"The ray reached the player (frame {damageFrame}) before the wall opened (frame {breakFrame}).");
    }

    [Fact]
    public void Prism_DashesTowardThePlayer()
    {
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new PrismEnemy(BossAt(300f))));
        var boss = First(sim);
        float startX = boss.Body.Position.X;
        bool dashed = false;
        for (int f = 0; f < 60 * 8; f++)
        {
            sim.Step(Idle);
            if (boss.CurrentActionName == nameof(PrismDashAction)) dashed = true;
        }
        output.WriteLine($"boss x {startX:F0} → {boss.Body.Position.X:F0}, player damage {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(dashed, "Prism never chose its dash.");
        Assert.True(startX - boss.Body.Position.X > 60f,
            $"Prism didn't close the distance (Δx {startX - boss.Body.Position.X:F1}px).");
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "Prism never landed a hit.");
    }

    // The floor with a 3-tile stone platform (cols 8..12, rows 0..2); its top is y = 0.
    private static ChunkMap Ledge() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOXXXXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXXXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOXXXXXOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    [Fact]
    public void Prism_DashesUpToAPlayerOnALedge()
    {
        var playerAt = new Vector2(10 * Chunk.TileSize + 5f, -12f);        // standing on the platform
        var sim  = new Simulation(Ledge(), playerAt, g => g.SpawnEntity(new PrismEnemy(BossAt(300f))));
        var boss = First(sim);
        float groundY = FloorTopY - PrismEnemy.FootDrop;
        float minY = groundY, lastDamage = 0f;
        bool dashHit = false;
        for (int f = 0; f < 60 * 10; f++)
        {
            sim.Step(Idle);
            minY = System.MathF.Min(minY, boss.Body.Position.Y);
            float dmg = sim.Player.Combat.DamageTaken;
            if (dmg > lastDamage && boss.CurrentActionName == nameof(PrismDashAction)
                && Vector2.Distance(boss.Body.Position, sim.Player.Body.Position) < 48f)
                dashHit = true;
            lastDamage = dmg;
            if (dashHit) break;
        }
        output.WriteLine($"boss min y {minY:F0} (ground {groundY:F0}), dash hit {dashHit}");
        Assert.True(minY < groundY - 12f, $"Prism never left the ground for the ledge (min y {minY:F0}).");
        Assert.True(dashHit, "Prism never hit the player on the ledge with a dash.");
    }

    [Fact]
    public void Prism_BoxesThePlayerWithARayOnEachSide()
    {
        var sim  = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new PrismEnemy(BossAt(260f))));
        bool boxed = false;
        for (int f = 0; f < 60 * 8 && !boxed; f++)
        {
            sim.Step(Idle);
            bool left = false, right = false;
            float px = sim.Player.Body.Position.X;
            foreach (var e in sim.Entities)
            {
                if (e is not RayEmitter r || r.IsDead) continue;
                if (r.Body.Position.X < px) left = true; else right = true;
            }
            boxed = left && right;
        }
        Assert.True(boxed, "Prism never had a live emitter on each side of the player.");
    }

    [Fact]
    public void Prism_PlantsARayThatExplodesAtEndOfLife()
    {
        var sim = new Simulation(Floor(), PlayerAt, g => g.SpawnEntity(new PrismEnemy(BossAt(260f))));
        int blasts = 0;
        sim.OnChargedBlast += (_, _, _) => blasts++;
        int spawnFrame = -1, goneFrame = -1;
        float life = 0f;
        var first = EntityId.None;
        for (int f = 0; f < 60 * 14; f++)
        {
            sim.Step(Idle);
            if (first.IsNone)
            {
                foreach (var e in sim.Entities)
                    if (e is RayEmitter r && !r.IsDead) { first = r.Id; spawnFrame = f; life = r.LifeLeft; break; }
                continue;
            }
            bool alive = false;
            foreach (var e in sim.Entities) if (e.Id == first && !e.IsDead) alive = true;
            if (!alive) { goneFrame = f; break; }
        }
        output.WriteLine($"planted at {spawnFrame} (life {life:F1}s), blasts {blasts}, gone at {goneFrame}");
        Assert.True(spawnFrame >= 0, "Prism never planted a ray emitter.");
        Assert.True(blasts >= 1, "The emitter never exploded at the end of its life.");
        Assert.True(goneFrame > spawnFrame + SimFrames.FromSeconds(life * 0.9f, Simulation.FixedDt),
            $"The emitter went off early (planted {spawnFrame}, gone {goneFrame}, life {life:F1}s).");
    }

    [Fact]
    public void Prism_SurvivesASnapshotRoundTrip()
    {
        const int K = 90, N = 480;

        Simulation Build() => new(Floor(), PlayerAt, g => g.SpawnEntity(new PrismEnemy(BossAt(260f))));

        // Restless input so the pair stay in contact and every attack gets a chance.
        PlayerInput At(int f) => new() { Right = f % 50 < 20, Left = f % 50 >= 35, Space = f % 45 < 3 };

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

    private static PrismEnemy First(Simulation sim)
    {
        foreach (var e in sim.Entities) if (e is PrismEnemy p) return p;
        Assert.Fail("No Prism in the simulation.");
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
            if (e is PrismEnemy b)
                sb.Append($"|{b.CurrentActionName}|s{b.Scratch.I0},{Bits(b.Scratch.F0)},{Bits(b.Scratch.F1)},{Bits(b.Scratch.F3)}");
            if (e is RayEmitter r)
                sb.Append($"|fire{(r.Firing ? 1 : 0)}|life{Bits(r.LifeLeft)}");
            sb.Append('\n');
        }
        sb.Append($"T|{sim.Chunks.TerrainHash}\n");
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");
}
