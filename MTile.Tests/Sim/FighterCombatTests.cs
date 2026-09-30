using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase 4 gate for Plans/FIGHTER_DESIGN_PLAN.md — targets and teams (§5.3). An enemy
// fights the nearest live candidate not on its team; the combat pass lets Enemy-faction
// hitboxes land on Enemy-faction hurtboxes across a team line. The stock roster all
// shares Teams.Enemies, so these tests put fighters on teams 3 and 4 and park the
// player far away (the arena's "rooted dummy" stand-in: idle input, out of reach).
public class FighterCombatTests(ITestOutputHelper output)
{
    // 130 tiles of flat floor (world x -44 … 1386 at 11 px tiles).
    private static ChunkMap Floor()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 8; r++) sb.Append(new string('O', 130)).Append('\n');
        sb.Append(new string('X', 130));
        return SimTerrain.FromAscii(sb.ToString(), originTileX: -4, originTileY: 0);
    }

    private const float FloorTopY = 8 * Chunk.TileSize;
    private const float StandY    = FloorTopY - 11f;
    private static readonly PlayerInput Idle = default;

    // ── (a) opposing teams close and trade ──────────────────────────────────

    [Fact]
    public void OpposingTeams_CloseAndDamageEachOther()
    {
        // Player 800+ px from both; the fighters start 120 apart.
        var sim = Build(new Vector2(-30f, FloorTopY - 12f),
                        Brawler(team: 3), new Vector2(830f, StandY),
                        Brawler(team: 4), new Vector2(950f, StandY));
        var a = (EnemyEntity)sim.Entities[0];
        var b = (EnemyEntity)sim.Entities[1];

        sim.Step(Idle);
        Assert.Equal(b.Id, a.CurrentTargetId);
        Assert.Equal(a.Id, b.CurrentTargetId);

        int frame = -1;
        for (int f = 0; f < 300; f++)
        {
            sim.Step(Idle);
            if (a.Health < 4f && b.Health < 4f) { frame = f; break; }
        }
        Assert.True(frame >= 0,
            $"after 5 s: A hp {a.Health:F2}, B hp {b.Health:F2} — opposing fighters did not trade hits.");
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
        output.WriteLine($"both damaged by frame {frame}: A {a.Health:F2}, B {b.Health:F2}");
    }

    // ── (b) same team ignore each other ─────────────────────────────────────

    [Fact]
    public void SameTeam_IgnoreEachOther()
    {
        var sim = Build(new Vector2(-30f, FloorTopY - 12f),
                        Brawler(team: 3), new Vector2(830f, StandY),
                        Brawler(team: 3), new Vector2(860f, StandY));
        var a = (EnemyEntity)sim.Entities[0];
        var b = (EnemyEntity)sim.Entities[1];

        for (int f = 0; f < 300; f++)
        {
            sim.Step(Idle);
            Assert.Equal(sim.Player.Id, a.CurrentTargetId);
            Assert.Equal(sim.Player.Id, b.CurrentTargetId);
            Assert.Equal("", a.CurrentActionName);
            Assert.Equal("", b.CurrentActionName);
        }
        Assert.Equal(4f, a.Health);
        Assert.Equal(4f, b.Health);
    }

    // Same-team immunity is a combat-pass rule too, not just a targeting one: a
    // teammate standing in a swing aimed at the player takes nothing.
    [Fact]
    public void SameTeam_SwingThroughTeammateDoesNotHurtIt()
    {
        var sim = Build(new Vector2(400f, FloorTopY - 12f),
                        Brawler(team: 3), new Vector2(430f, StandY),
                        Post(team: 3),    new Vector2(415f, StandY));
        var mate = sim.Entities[1];
        for (int f = 0; f < 120; f++) sim.Step(Idle);
        Assert.True(sim.Player.Combat.DamageTaken > 0f, "the brawler never hit the player, so this proves nothing");
        Assert.Equal(4f, mate.Health);
    }

    // ── (c) nearest wins; the stickiness rule ───────────────────────────────

    [Fact]
    public void TargetsNearestOpposing_PlayerOrFighter()
    {
        // Player 30 px from A, team-4 fighter 200 px: A takes the player.
        var near = Build(new Vector2(370f, FloorTopY - 12f),
                         Brawler(team: 3), new Vector2(400f, StandY),
                         Post(team: 4),    new Vector2(600f, StandY));
        var a = (EnemyEntity)near.Entities[0];
        near.Step(Idle);
        Assert.Equal(near.Player.Id, a.CurrentTargetId);

        // Player far, the fighter near: A takes the fighter.
        var far = Build(new Vector2(1150f, FloorTopY - 12f),
                        Brawler(team: 3), new Vector2(400f, StandY),
                        Post(team: 4),    new Vector2(600f, StandY));
        var a2 = (EnemyEntity)far.Entities[0];
        far.Step(Idle);
        Assert.Equal(far.Entities[1].Id, a2.CurrentTargetId);
    }

    [Fact]
    public void Stickiness_KeepsTargetUntilChallengerIsQuarterCloser()
    {
        // A at 400; B (team 4) at 600, d = 200. Player at 0.8·d, then 0.7·d.
        foreach (var (px, expectKeep) in new[] { (400f - 160f, true), (400f - 140f, false) })
        {
            var sim = Build(new Vector2(px, StandY),
                            Post(team: 3), new Vector2(400f, StandY),
                            Post(team: 4), new Vector2(600f, StandY));
            var a = (EnemyEntity)sim.Entities[0];
            var b = sim.Entities[1];

            Assert.True(sim.TryFindTarget(a, EntityId.None, out var fresh, out var freshPlayer));
            Assert.Equal(sim.Player.Id, fresh.Id);                 // no history: nearest wins
            Assert.True(fresh.IsPlayer);
            Assert.Same(sim.Player, freshPlayer);

            Assert.True(sim.TryFindTarget(a, b.Id, out var kept, out var keptPlayer));
            Assert.Equal(expectKeep ? b.Id : sim.Player.Id, kept.Id);
            Assert.Equal(!expectKeep, kept.IsPlayer);
            if (expectKeep) Assert.Null(keptPlayer);
        }
    }

    // ── (d) stickiness survives a snapshot round trip ───────────────────────

    [Fact]
    public void Stickiness_RoundTripBitIdentical()
    {
        // A (team 3) is rooted at 500 and never attacks. B (team 4) is rooted at 700,
        // so A starts on B. C (team 4) walks in from 160 toward A: once C is nearer
        // than B, A keeps B (sticky) until C is 25% nearer. The snapshot is taken
        // INSIDE that band — lose TargetId on restore and A would switch at once.
        var sim = Build(new Vector2(1150f, FloorTopY - 12f),
                        Post(team: 3),    new Vector2(500f, StandY),
                        Post(team: 4),    new Vector2(700f, StandY),
                        Brawler(team: 4), new Vector2(160f, StandY));
        var a = (EnemyEntity)sim.Entities[0];
        var b = sim.Entities[1];
        var c = sim.Entities[2];

        int guard = 0;
        while (System.MathF.Abs(c.Body.Position.X - a.Body.Position.X) > 185f)
        {
            sim.Step(Idle);
            Assert.True(++guard < 600, "C never walked into the sticky band");
        }
        Assert.Equal(b.Id, a.CurrentTargetId);                       // sticky, though C is nearer
        Assert.True(System.MathF.Abs(c.Body.Position.X - a.Body.Position.X) < 200f);

        var snap = sim.Snapshot();
        const int N = 180;
        var liveTrace = new List<string>();
        var liveSums  = new List<ulong>();
        for (int f = 0; f < N; f++) { sim.Step(Idle); liveTrace.Add(Probe(sim)); liveSums.Add(sim.Checksum()); }
        Assert.Equal(c.Id, a.CurrentTargetId);                       // the switch happened inside the window

        sim.Restore(snap);
        Assert.Equal(b.Id, a.CurrentTargetId);                       // restored, not recomputed
        var replayTrace = new List<string>();
        var replaySums  = new List<ulong>();
        for (int f = 0; f < N; f++) { sim.Step(Idle); replayTrace.Add(Probe(sim)); replaySums.Add(sim.Checksum()); }

        for (int i = 0; i < N; i++)
        {
            if (liveTrace[i] != replayTrace[i])
            {
                output.WriteLine($"Divergence at replay step {i}:");
                output.WriteLine("LIVE:\n"   + liveTrace[i]);
                output.WriteLine("REPLAY:\n" + replayTrace[i]);
            }
            Assert.Equal(liveTrace[i], replayTrace[i]);
            Assert.Equal(liveSums[i],  replaySums[i]);
        }
        int switchAt = liveTrace.FindIndex(t => t.Contains($"t{c.Id}"));
        output.WriteLine($"Identical across {N} frames; A switched to C at replay step {switchAt}.");
    }

    // ── (e) retarget when the target dies ───────────────────────────────────

    [Fact]
    public void TargetDies_RetargetsNextOpposingDeterministically()
    {
        // A (team 3) at 500 on B (team 4, 100 px). C and D (team 4) are both exactly
        // 150 px away on either side; the tie goes to the earlier-spawned, C.
        EntityId Run(out EntityId cId, out EntityId dId, out ulong sum)
        {
            var sim = Build(new Vector2(1150f, FloorTopY - 12f),
                            Post(team: 3), new Vector2(500f, StandY),
                            Post(team: 4), new Vector2(600f, StandY),
                            Post(team: 4), new Vector2(350f, StandY),
                            Post(team: 4), new Vector2(650f, StandY));
            var a = (EnemyEntity)sim.Entities[0];
            var b = sim.Entities[1];
            cId = sim.Entities[2].Id;
            dId = sim.Entities[3].Id;

            for (int f = 0; f < 10; f++) sim.Step(Idle);
            Assert.Equal(b.Id, a.CurrentTargetId);

            b.Health = 0f;                 // dies; swept at the end of the next Step
            sim.Step(Idle);
            var chosen = a.CurrentTargetId;
            for (int f = 0; f < 10; f++) sim.Step(Idle);
            Assert.Equal(chosen, a.CurrentTargetId);
            sum = sim.Checksum();
            return chosen;
        }

        var first  = Run(out var c1, out var d1, out var s1);
        var second = Run(out _,      out _,      out var s2);
        Assert.Equal(c1, first);
        Assert.NotEqual(d1, first);
        Assert.Equal(first, second);
        Assert.Equal(s1, s2);

        // And with nobody left on the other team but the player, the player.
        var sim = Build(new Vector2(1150f, FloorTopY - 12f),
                        Post(team: 3), new Vector2(500f, StandY),
                        Post(team: 4), new Vector2(600f, StandY));
        var a = (EnemyEntity)sim.Entities[0];
        sim.Step(Idle);
        Assert.Equal(sim.Entities[1].Id, a.CurrentTargetId);
        sim.Entities[1].Health = 0f;
        sim.Step(Idle);
        Assert.Equal(sim.Player.Id, a.CurrentTargetId);
    }

    // ── blueprints ───────────────────────────────────────────────────────────

    // Walks to its target and swings. The pool melee (32 px range) with a chase
    // that stops inside it.
    private static EnemyBlueprint Brawler(int team) => new()
    {
        Kind          = EntityKind.Sparring,
        Radius        = 10f,
        Health        = 4f,
        Mass          = 1f,
        FrictionScale = 0.10f,
        Team          = team,
        Movement = () => new()
        {
            new EnemyIdleState(),
            new EnemyChaseState(),
            new EnemyAttackHoldState(),
            new EnemyStaggerState(),
        },
        Actions    = () => new() { new EnemyMeleeAction() },
        Controller = new ChasePlayerController { EngageRange = 20f },
    };

    // Rooted, harmless: never moves, never attacks. A fixed candidate.
    private static EnemyBlueprint Post(int team) => new()
    {
        Kind       = EntityKind.Sparring,
        Radius     = 10f,
        Health     = 4f,
        Mass       = 1f,
        Rooted     = true,
        Team       = team,
        Movement   = () => new() { new EnemyIdleState() },
        Actions    = () => new(),
        Controller = new StationaryAimController { AlertRange = 0f },
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Simulation Build(Vector2 playerPos, params object[] spawns) =>
        new(Floor(), playerPos, g =>
        {
            for (int i = 0; i < spawns.Length; i += 2)
                g.SpawnEntity(new BlueprintEnemy((EnemyBlueprint)spawns[i], (Vector2)spawns[i + 1]));
        });

    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)}|dmg{Bits(p.Combat.DamageTaken)}\n");
        foreach (var e in sim.Entities)
        {
            sb.Append($"E{e.Id}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}");
            if (e is EnemyEntity en) sb.Append($"|{en.CurrentActionName}|t{en.CurrentTargetId}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");
}
