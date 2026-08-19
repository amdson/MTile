using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The two tests you almost always want for a new enemy, kept minimal so they're
// worth copying alongside Entities/Enemies/Types/TemplateEnemy.cs.
//
//   1. A BEHAVIOUR gate — it does the one thing that makes it what it is.
//   2. A DETERMINISM gate — it survives snapshot/restore, so it works in netplay
//      and doesn't corrupt a rollback.
//
// Both run a real Simulation, so the phase ordering, combat pass, and physics
// step are the ones the game uses. This is a much faster loop than launching the
// game, and it catches the class of bug that is nearly invisible in play: every
// real defect in the gauntlet trio was found here, not by playing.
//
// Run just these:
//   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~TemplateEnemyTests"
public class TemplateEnemyTests(ITestOutputHelper output)
{
    // Flat floor, solid at tile y = 3 (world y 48), open above.
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float FloorTopY = 3 * Chunk.TileSize;   // 48

    // A stationary, non-attacking player. Every change in the trace is therefore
    // the enemy's doing, which is what makes the assertions mean anything.
    private static readonly PlayerInput Idle = default;

    [Fact]
    public void Template_ClosesTheDistanceAndLandsAHit()
    {
        // 260px apart — well outside the controller's PreferredRange, so it has
        // to walk before it can swing.
        var sim = new Simulation(Floor(), new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Template,
                                                   new Vector2(300f, FloorTopY - 11f))));

        var bot     = First(sim, EntityKind.Template);
        float startX = bot.Body.Position.X;

        // ~200 frames just to walk 218px at 65px/s, then a 0.5s windup on top.
        for (int f = 0; f < 420; f++) sim.Step(Idle);

        // Legs work: it moved toward the player rather than standing still or
        // wandering off. (A brain/state mix-up usually shows up right here.)
        Assert.True(startX - bot.Body.Position.X > 100f,
            $"Template didn't close the distance (Δx {startX - bot.Body.Position.X:F1}px).");

        // Arms work: the attack actually published a hitbox that connected.
        // DamagePercent is the monotonic percent meter — a hit is the only thing
        // that can raise it while the player never moves or attacks.
        Assert.True(sim.Player.Combat.DamagePercent > 0f,
            "Template never landed a hit.");

        output.WriteLine($"Closed {startX - bot.Body.Position.X:F0}px; " +
                         $"player at {sim.Player.Combat.DamagePercent:F2}%.");
    }

    [Fact]
    public void Template_SurvivesASnapshotRoundTrip()
    {
        // Run → snapshot → keep running (recording) → restore → replay the same
        // inputs. If anything the enemy relies on isn't captured, the two traces
        // diverge. Snapshot mid-encounter so the capture lands inside a windup
        // or a recovery rather than at rest, which is where the gaps hide.
        const int K = 60, N = 220;

        Simulation Build() => new(Floor(), new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(EnemyFactory.Create(EntityKind.Template,
                                                   new Vector2(200f, FloorTopY - 11f))));

        // Deterministic, slightly restless input so the pair stay in contact.
        PlayerInput At(int f) => new() { Right = f % 40 < 15, Space = f % 30 < 3 };

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

    private static Entity First(Simulation sim, EntityKind kind)
    {
        foreach (var e in sim.Entities) if (e.Kind == kind) return e;
        Assert.Fail($"No {kind} in the simulation.");
        return null;
    }

    // Exact-float fingerprint of everything observable. Compared as raw bit
    // patterns, not formatted text, so a divergence of one ULP still fails —
    // which is the standard rollback has to meet.
    private static string Probe(Simulation sim)
    {
        var sb = new StringBuilder();
        var p  = sim.Player;
        sb.Append($"P|{Bits(p.Body.Position.X)},{Bits(p.Body.Position.Y)};")
          .Append($"{Bits(p.Body.Velocity.X)},{Bits(p.Body.Velocity.Y)}|")
          .Append($"{p.CurrentStateName}/{p.CurrentActionName}|pct{Bits(p.Combat.DamagePercent)}\n");
        foreach (var e in sim.Entities)
            sb.Append($"E{e.Id}:{e.Kind}|{Bits(e.Body.Position.X)},{Bits(e.Body.Position.Y)};")
              .Append($"{Bits(e.Body.Velocity.X)},{Bits(e.Body.Velocity.Y)}|hp{Bits(e.Health)}\n");
        return sb.ToString();
    }

    private static int Bits(float f) => System.BitConverter.SingleToInt32Bits(f);
}
