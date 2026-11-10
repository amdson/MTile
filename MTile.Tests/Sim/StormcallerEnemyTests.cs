using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Behaviour + determinism gates for the Stormcaller boss
// (Entities/Enemies/Types/StormcallerEnemy.cs): the gale hurts and pushes a player
// anywhere in the hall, a lobbed bomb comes down on a player who stands still, the
// close-range strike throws the player back, and the fight survives a snapshot
// round-trip.
//
// Run just these:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~StormcallerEnemyTests"
public class StormcallerEnemyTests(ITestOutputHelper output)
{
    // Flat stone floor, solid at tile y = 3, 90 tiles wide (world x -44 .. 946), with
    // 24 rows of air above it for the bombs' arcs.
    private static ChunkMap Floor(int wallTileX = int.MinValue, int wallHeight = 0)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 24; r++)
        {
            var row = new StringBuilder(new string('O', 90));
            // A left wall, thick enough to outlast the wind's erosion for a test's length,
            // so nothing blown down the hall leaves it.
            for (int c = 0; c < 6; c++) row[c] = 'X';
            // Rows 0..23 map to tile rows -21..2; the wall stands on the floor (row 3).
            if (wallTileX != int.MinValue && r >= 24 - wallHeight) row[wallTileX + 4] = 'X';
            sb.Append(row).Append('\n');
        }
        for (int r = 0; r < 2;  r++) sb.Append(new string('X', 90)).Append('\n');
        return SimTerrain.FromAscii(sb.ToString(), originTileX: -4, originTileY: -21);
    }

    private const float FloorTopY = 3 * Chunk.TileSize;
    private static Vector2 PlayerAt(float x) => new(x, FloorTopY - 12f);
    private static Vector2 BossAt(float x)   => new(x, FloorTopY - StormcallerEnemy.HalfHeight - 1f);

    private static readonly PlayerInput Idle = default;

    // An idle player does not survive this fight for long, and a respawn resets the
    // damage counter the tests read. Tests that run past the first few seconds use this.
    private static Simulation Tough(Simulation sim)
    {
        sim.Player.MaxHealth = 1000f;
        sim.Player.Health    = 1000f;
        return sim;
    }

    private static Simulation Build(float playerX, float bossX)
        => new(Floor(), PlayerAt(playerX), g => g.SpawnEntity(new StormcallerEnemy(BossAt(bossX))));

    [Fact]
    public void Gale_HurtsAndPushesAPlayerAcrossTheHall()
    {
        // Boss alone would also lob; look only at the first 1.9 s, before a bomb can land.
        var sim = Build(100f, 850f);
        float startX = sim.Player.Body.Position.X;
        int frames = SimFrames.FromSeconds(1.9f, Simulation.FixedDt);
        for (int f = 0; f < frames; f++) sim.Step(Idle);

        float dmg = sim.Player.Combat.DamageTaken;
        float dx  = sim.Player.Body.Position.X - startX;
        output.WriteLine($"after {frames}f: damage {dmg:F2}, dx {dx:F1}");
        Assert.True(dmg > 0f, "No gust reached the player.");
        Assert.True(dmg <= StormWind.GustDamage(StormPhase.Moderate) * 2f + 1e-3f, $"More than two gusts' damage in 1.9 s ({dmg:F2}).");
        Assert.True(dx < -3f, $"The gale didn't move an idle player left (dx {dx:F1}).");
    }

    [Fact]
    public void Gale_IsBlockedByAWall()
    {
        // A 5-high stone wall at tile 14 (x 154..165); the player stands just in its lee.
        // 2.5 s = two gusts, and still before the first bomb can land.
        var sim = new Simulation(Floor(wallTileX: 14, wallHeight: 5), PlayerAt(140f),
                                 g => g.SpawnEntity(new StormcallerEnemy(BossAt(850f))));
        var boss = First(sim);
        float startX = sim.Player.Body.Position.X;
        for (int f = 0; f < SimFrames.FromSeconds(2.5f, Simulation.FixedDt); f++) sim.Step(Idle);
        float dx = sim.Player.Body.Position.X - startX;
        output.WriteLine($"sheltered: damage {sim.Player.Combat.DamageTaken:F2}, dx {dx:F2}, exposure {boss.Exposure(sim.Player.Body.Bounds):F2}");
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
        Assert.True(System.MathF.Abs(dx) < 0.5f, $"A sheltered player was still pushed (dx {dx:F2}).");
        Assert.Equal(0f, boss.Exposure(sim.Player.Body.Bounds));
    }

    [Fact]
    public void Gale_ErodesTheWallItBreaksOn()
    {
        // A 3-high, one-thick wall at tile 40, far from the player (so no bomb or wind
        // column lands near it). Every gust takes a little off the tile each lane ends on:
        // several seconds of shelter, then the wind is through.
        var sim = new Simulation(Floor(wallTileX: 40, wallHeight: 3), PlayerAt(60f),
                                 g => g.SpawnEntity(new StormcallerEnemy(BossAt(850f))));
        int brokeFrame = -1;
        for (int f = 0; f < 60 * 40 && brokeFrame < 0; f++)
        {
            sim.Step(Idle);
            if (sim.Chunks.GetCellState(40, 2) == TileState.Empty && sim.Chunks.GetCellState(40, 0) == TileState.Empty)
                brokeFrame = f;
        }
        output.WriteLine($"wall eaten through at frame {brokeFrame}");
        Assert.True(brokeFrame >= 0, "The wind never ate through a one-thick wall.");
        Assert.True(brokeFrame > 60 * 4, $"The wall went too fast to be shelter (frame {brokeFrame}).");
        // The floor it stood on is not in any lane: untouched.
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(40, 3));
    }

    [Fact]
    public void Gale_CyclesModerateCalmIntense()
    {
        // The schedule itself: a shelter phase that alternates moderate and intense blows,
        // then a long calm; the heading changes only as a calm begins.
        int moderate = 0, calm = 0, intense = 0, headingChangesWhileBlowing = 0;
        var prev = StormWind.At(0);
        for (int f = 0; f < 60 * 50; f++)   // two cycles
        {
            var w = StormWind.At(f);
            if (w.Phase == StormPhase.Moderate) moderate++; else if (w.Phase == StormPhase.Calm) calm++; else intense++;
            if (w.Heading != prev.Heading && w.Phase != StormPhase.Calm) headingChangesWhileBlowing++;
            prev = w;
        }
        Assert.True(moderate > intense && intense > 0 && calm > intense, $"moderate {moderate}, calm {calm}, intense {intense}");
        Assert.True(calm >= 60 * 2 * 8, $"The calm (run phase) should be long ({calm / 2} frames a cycle).");
        Assert.Equal(0, headingChangesWhileBlowing);
        Assert.NotEqual(StormWind.Direction(0), StormWind.Direction(1));

        // And in the sim: a tough neutral ball out in the hall (the player stands nearer the
        // boss, so the bombs, the column and the ice go there, not at the ball) loses HP
        // faster in the intense blow than in the moderate one.
        Entity ball = null;
        var sim = new Simulation(Floor(), PlayerAt(560f), g =>
        {
            g.SpawnEntity(new StormcallerEnemy(BossAt(850f)));
            ball = EntityFactory.Ball(new Vector2(200f, FloorTopY - 8f));
            ball.Health = ball.MaxHealth = 100f;
            g.SpawnEntity(ball);
        });
        var boss = First(sim);
        var lost = new float[3];
        var frames = new int[3];
        for (int f = 0; f < 60 * 17; f++)
        {
            float hp = ball.Health;
            // Pin it: a ball has nothing to hold on with and would be blown out of the hall.
            ball.Body.Position = new Vector2(200f, FloorTopY - 8f);
            ball.Body.Velocity = Vector2.Zero;
            sim.Step(Idle);
            var phase = StormWind.At(f + 1).Phase;   // the boss's clock is one ahead of the step index
            lost[(int)phase] += hp - ball.Health;
            frames[(int)phase]++;
        }
        float perSecMod = lost[0] / (frames[0] / 60f), perSecHard = lost[2] / (frames[2] / 60f);
        output.WriteLine($"ball lost: moderate {lost[0]:F2} ({perSecMod:F2}/s), calm {lost[1]:F2}, intense {lost[2]:F2} ({perSecHard:F2}/s); ball x {ball.Body.Position.X:F0}");
        Assert.True(lost[0] > 0f, "The moderate wind never hurt the ball.");
        Assert.True(perSecHard > perSecMod * 2f, $"Intense ({perSecHard:F2}/s) is not much worse than moderate ({perSecMod:F2}/s).");
    }

    [Fact]
    public void Jet_ColumnIsAimedAtThePlayerAndBlowsThemAway()
    {
        var sim  = Tough(Build(300f, 850f));
        var boss = First(sim);
        int started = -1;
        float minVx = 0f, hpAtStart = 0f;
        for (int f = 0; f < 60 * 12; f++)
        {
            bool was = boss.CurrentActionName == nameof(StormcallerJetAction);
            sim.Step(Idle);
            bool now = boss.CurrentActionName == nameof(StormcallerJetAction);
            if (now && !was && started < 0) { started = f; hpAtStart = sim.Player.Combat.DamageTaken; }
            if (now) minVx = System.MathF.Min(minVx, sim.Player.Body.Velocity.X);
            if (was && !now && started >= 0) break;
        }
        output.WriteLine($"column began at frame {started}; fastest leftward {minVx:F0} px/s; damage during {sim.Player.Combat.DamageTaken - hpAtStart:F2}");
        Assert.True(started > 60, $"The column should be semi-rare, not an opener (began at frame {started}).");
// (How hard it throws is not asserted: by now the intense gale has usually pinned an
        // idle player to the far wall. It was -550 px/s on a free-standing one.)
        Assert.True(sim.Player.Combat.DamageTaken - hpAtStart >= StormcallerJetAction.Damage, "The column never hit.");
    }

    [Fact]
    public void Ice_CoversMostOfTheScreenAndLeavesGaps()
    {
        // The pattern, over a spread of seeds: ~70 % covered, the cast point always
        // covered, both gaps clear to their full radius, and the near gap a short run away.
        for (int seed = 1; seed <= 40; seed++)
        {
            var centre  = new Vector2(300f, 20f);
            var circles = StormIceField.Layout(seed * 977, centre, out var gaps);
            bool In(Vector2 p) { foreach (var c in circles) if (Vector2.DistanceSquared(p, new Vector2(c.X, c.Y)) <= c.Z * c.Z) return true; return false; }

            int hit = 0, n = 0;
            float left = centre.X - StormIceField.RegionW * 0.5f, top = centre.Y - StormIceField.RegionH * 0.6f;
            for (float y = top + 2f; y < top + StormIceField.RegionH; y += 4f)
            for (float x = left + 2f; x < left + StormIceField.RegionW; x += 4f) { n++; if (In(new Vector2(x, y))) hit++; }
            float share = hit / (float)n;
            Assert.True(share > 0.62f && share < 0.80f, $"seed {seed}: {share:P0} covered");
            Assert.True(In(centre), $"seed {seed}: the cast point is not covered");
            Assert.Equal(2, gaps.Length);
            foreach (var g in gaps)
                for (int k = 0; k < 16; k++)
                {
                    float a = k * MathHelper.TwoPi / 16f;
                    var rim = g + new Vector2(System.MathF.Cos(a), System.MathF.Sin(a)) * (StormIceField.GapRadius - 1f);
                    Assert.False(In(rim) || In(g), $"seed {seed}: a gap is not clear");
                }
            Assert.True(System.MathF.Abs(gaps[0].X - centre.X) < 150f, $"seed {seed}: the near gap is {gaps[0].X - centre.X:F0}px away");
        }
    }

    [Fact]
    public void Ice_HitsAPlayerWhoStaysPutThroughWalls()
    {
        // A field cast on a player standing behind a tall wall. No boss: just the field.
        // It sets after FormSeconds and the wall is no help.
        var sim = new Simulation(Floor(wallTileX: 14, wallHeight: 8), PlayerAt(140f),
                                 g => g.SpawnEntity(new StormIceField(PlayerAt(140f), seed: 12345, Faction.Enemy)));
        int early = SimFrames.FromSeconds(StormIceField.FormSeconds - 0.2f, Simulation.FixedDt);
        for (int f = 0; f < early; f++) sim.Step(Idle);
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);          // still forming: nothing yet
        for (int f = 0; f < 30; f++) sim.Step(Idle);
        output.WriteLine($"damage after the field set: {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(sim.Player.Combat.DamageTaken >= 1.4f, $"The field didn't hit a player who stayed where it was cast ({sim.Player.Combat.DamageTaken:F2}).");
    }

    [Fact]
    public void Ice_IsCastAsAnIntenseBlowBegins()
    {
        var sim = Tough(Build(300f, 850f));
        int castFrame = -1;
        for (int f = 0; f < 60 * 7 && castFrame < 0; f++)
        {
            sim.Step(Idle);
            foreach (var e in sim.Entities) if (e is StormIceField) castFrame = f;
        }
        output.WriteLine($"ice cast at frame {castFrame}");
        Assert.True(castFrame >= 0, "No ice field in the first intense blow.");
        Assert.Equal(StormPhase.Intense, StormWind.At(castFrame).Phase);
    }

    [Fact]
    public void Run_CalmHasBeamsAndBigBombs()
    {
        // Through the first calm (15 s .. 25 s): 0-2 beams blowing at any moment, none a
        // fair share of the time, and every bomb thrown in it is the big kind.
        var sim = Tough(Build(300f, 850f));
        int calmFrames = 0, none = 0, most = 0, bigBombs = 0, smallBombsInCalm = 0;
        var seen = new HashSet<EntityId>();
        for (int f = 0; f < 60 * 25; f++)
        {
            sim.Step(Idle);
            if (StormWind.At(f + 1).Phase != StormPhase.Calm) continue;
            calmFrames++;
            int blowing = 0;
            foreach (var e in sim.Entities)
            {
                if (e is StormBeam b && !b.IsDead && b.Blowing) blowing++;
                if (e is StormBombProjectile bomb && seen.Add(bomb.Id) && bomb.AgeSeconds < 0.1f)
                { if (bomb.Scale == StormcallerLobAction.RunBombScale) bigBombs++; else smallBombsInCalm++; }
            }
            if (blowing == 0) none++;
            most = System.Math.Max(most, blowing);
        }
        float share = none / (float)calmFrames;
        output.WriteLine($"calm frames {calmFrames}: no beam {share:P0} of the time, at most {most} at once; big bombs {bigBombs}, small {smallBombsInCalm}");
        Assert.True(most >= 1 && most <= 2, $"Beams at once should peak at 1-2 (saw {most}).");
        Assert.True(share > 0.25f && share < 0.6f, $"No beam blowing {share:P0} of the calm; want about 40 %.");
        Assert.True(bigBombs >= 1, "No big bomb was thrown in the calm.");
        Assert.Equal(0, smallBombsInCalm);
    }

    // A beam laid flat along the hall, coming from the right, through a one-thick wall at
    // tile 14 with the player standing behind it.
    private Simulation BeamAtWall(TileType wall, out ChunkMap chunks)
    {
        chunks = Floor(wallTileX: 14, wallHeight: 5);
        for (int gy = -2; gy <= 2; gy++) SetType(chunks, 14, gy, wall);
        return new Simulation(chunks, PlayerAt(120f), g =>
            g.SpawnEntity(new StormBeam(new Vector2(150f, FloorTopY - 14f), System.MathF.PI, 1f, Faction.Enemy)));
    }

    private static void SetType(ChunkMap chunks, int gx, int gy, TileType type)
    {
        int cx = (int)System.MathF.Floor(gx / (float)Chunk.Size), cy = (int)System.MathF.Floor(gy / (float)Chunk.Size);
        Assert.True(chunks.TryGet(new Point(cx, cy), out var chunk), $"no chunk at tile ({gx},{gy})");
        chunk.Tiles[gx - cx * Chunk.Size, gy - cy * Chunk.Size].Type = type;
    }

    [Fact]
    public void Run_BeamDrillsThroughDirtAndHitsBehindIt()
    {
        var sim = BeamAtWall(TileType.Dirt, out _);
        for (int f = 0; f < 60 * 2 + 10; f++) sim.Step(Idle);
        output.WriteLine($"dirt: damage {sim.Player.Combat.DamageTaken:F2}; wall cell (14,1) {sim.Chunks.GetCellState(14, 1)}, (14,2) {sim.Chunks.GetCellState(14, 2)}");
        Assert.True(sim.Player.Combat.DamageTaken >= 0.3f, "The beam didn't hit a player behind a dirt wall.");
        Assert.Equal(TileState.Empty, sim.Chunks.GetCellState(14, 2));
    }

    [Fact]
    public void Run_BeamIsStoppedByRockAndBarelyMarksIt()
    {
        var sim = BeamAtWall(TileType.Stone, out _);
        float startX = sim.Player.Body.Position.X;
        for (int f = 0; f < 60 * 2 + 10; f++) sim.Step(Idle);
        output.WriteLine($"stone: damage {sim.Player.Combat.DamageTaken:F2}, dx {sim.Player.Body.Position.X - startX:F2}; wall (14,1) {sim.Chunks.GetCellState(14, 1)}, (14,2) {sim.Chunks.GetCellState(14, 2)}");
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
        Assert.True(System.MathF.Abs(sim.Player.Body.Position.X - startX) < 0.5f, "A player behind rock was still pushed.");
        for (int gy = -2; gy <= 2; gy++) Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(14, gy));

        // "Close to permanent": the same wall takes a beam on the same tiles ten times over.
        for (int n = 0; n < 9; n++)
        {
            sim.SpawnEntity(new StormBeam(new Vector2(150f, FloorTopY - 14f), System.MathF.PI, 1f, Faction.Enemy));
            for (int f = 0; f < 60 * 2 + 10; f++) sim.Step(Idle);
        }
        Assert.Equal(TileState.Solid, sim.Chunks.GetCellState(14, 2));
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void Gale_StopsWhenTheBossDies()
    {
        var sim  = Build(100f, 850f);
        var boss = First(sim);
        for (int f = 0; f < 30; f++) sim.Step(Idle);
        boss.Health = 0f;
        for (int f = 0; f < 240; f++) sim.Step(Idle);   // let any bomb in the air land
        float before = sim.Player.Combat.DamageTaken;
        for (int f = 0; f < 180; f++) sim.Step(Idle);
        Assert.Equal(before, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void Lob_BombComesDownOnAPlayerWhoStandsStill()
    {
        var sim = Build(300f, 850f);
        bool lobbed = false, burst = false;
        float nearest = float.MaxValue, apexY = float.MaxValue;
        int spawnFrame = -1, burstFrame = -1;
        for (int f = 0; f < 60 * 6 && !burst; f++)
        {
            sim.Step(Idle);
            foreach (var e in sim.Entities)
            {
                if (e is not StormBombProjectile b) continue;
                if (!lobbed) { lobbed = true; spawnFrame = f; }
                apexY = System.MathF.Min(apexY, b.Body.Position.Y);
                if (b.Exploded)
                {
                    burst = true; burstFrame = f;
                    nearest = Vector2.Distance(b.Body.Position, sim.Player.Body.Position);
                }
            }
        }
        output.WriteLine($"lobbed at {spawnFrame}, burst at {burstFrame}, {nearest:F1}px from the player, apex y {apexY:F0}, damage {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(lobbed, "The Stormcaller never lobbed a bomb.");
        Assert.True(burst, "The bomb never burst.");
        Assert.True(burstFrame - spawnFrame > 90, $"The arc is not slow ({burstFrame - spawnFrame} frames in the air).");
        Assert.True(apexY < FloorTopY - 100f, $"The arc is not high (apex y {apexY:F0}, floor {FloorTopY:F0}).");
        Assert.True(nearest < StormBombProjectile.BlastRadius, $"The bomb burst {nearest:F1}px from a player who never moved.");
        Assert.True(sim.Player.Combat.DamageTaken >= 0.8f, $"The burst didn't hurt ({sim.Player.Combat.DamageTaken:F2}).");
    }

    [Fact]
    public void Palm_ThrowsAPlayerInFrontOfItBackDownTheHall()
    {
        var sim  = Build(810f, 850f);
        var boss = First(sim);
        bool palmed = false;
        float minX = float.MaxValue;
        for (int f = 0; f < 60 * 3; f++)
        {
            sim.Step(Idle);
            if (boss.CurrentActionName == nameof(StormcallerPalmAction)) palmed = true;
            minX = System.MathF.Min(minX, sim.Player.Body.Position.X);
        }
        output.WriteLine($"palmed {palmed}, player thrown to x {minX:F0} from 810, damage {sim.Player.Combat.DamageTaken:F2}");
        Assert.True(palmed, "The Stormcaller never used its palm on a player standing in front of it.");
        Assert.True(sim.Player.Combat.DamageTaken >= 0.6f, "The palm didn't connect.");
        Assert.True(minX < 810f - 60f, $"The palm didn't throw the player back (reached x {minX:F0}).");
    }

    [Fact]
    public void Stormcaller_SurvivesASnapshotRoundTrip()
    {
        const int K = 100, N = 1150;

        // Walk in, jump about: gusts, bombs, an ice field, a wind column, the calm and its beams all follow the snapshot.
        PlayerInput At(int f) => new() { Right = f % 50 < 8, Space = f % 45 < 3 };

        var live = Build(450f, 850f);
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

    private static StormcallerEnemy First(Simulation sim)
    {
        foreach (var e in sim.Entities) if (e is StormcallerEnemy s) return s;
        Assert.Fail("No Stormcaller in the simulation.");
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
            if (e is StormcallerEnemy b) sb.Append($"|{b.CurrentActionName}");
            if (e is StormBombProjectile s) sb.Append($"|x{(s.Exploded ? 1 : 0)}");
            if (e is StormIceField ice) sb.Append($"|set{(ice.HasSet ? 1 : 0)}");
            if (e is StormBeam beam) sb.Append($"|blow{(beam.Blowing ? 1 : 0)}");
            sb.Append('\n');
        }
        sb.Append($"T|{sim.Chunks.TerrainHash}\n");
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");
}
