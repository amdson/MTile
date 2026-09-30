using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase 1 gate for Plans/FIGHTER_DESIGN_PLAN.md: enemy pool actions read their
// tuning from an ActionSpec the blueprint hands them, rather than from
// per-class constants. Three claims, each pinned by one test:
//
//   * ActionSpec.Default(kind) IS the old constant table (spot-checked), so an
//     action built with the parameterless ctor cannot have drifted.
//   * A blueprint that hands an action a different spec gets different
//     behaviour — the knob is live, not decorative. Checked on a melee windup
//     and on a ranged projectile's launch speed and damage.
//   * A snapshot taken mid-windup restores the SPEC's durations, not the
//     defaults: run → snapshot → run → restore → replay must trace identically
//     with a non-default windup, which it cannot if PopulateDurations re-derived
//     stock numbers after the restore.
//
// The enemy behaviour suites (Gauntlet / Template / Zeus / Shrike / Bird / Aspid /
// Warden / Wizard) are the other half of the gate: "no existing enemy changes
// behaviour" is their job to prove.
public class FighterActionSpecTests(ITestOutputHelper output)
{
    private static ChunkMap Floor() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: -4, originTileY: 0);

    private const float FloorTopY = 3 * Chunk.TileSize;   // 48
    private static readonly PlayerInput Idle = default;

    // A ground melee blueprint whose only variable is the spec its swing gets.
    // Constructed directly (not through EnemyFactory) so the test never touches
    // the process-wide registry; Restore keeps live entities in place, so the
    // round-trip below does not need the kind registered either.
    private static EnemyBlueprint Melee(ActionSpec spec) => new()
    {
        Kind          = EntityKind.Sparring,
        Radius        = 10f,
        Health        = 4f,
        Mass          = 1f,
        FrictionScale = 0.10f,
        Movement = () => new()
        {
            new EnemyIdleState(),
            new EnemyChaseState(),
            new EnemyAttackHoldState(),
            new EnemyStaggerState(),
        },
        Actions = () => new() { new EnemyMeleeAction(spec) },
        Controller = new ProximityChaseController { AlertRange = 200f },
    };

    private static EnemyBlueprint Ranged(ActionSpec spec) => new()
    {
        Kind          = EntityKind.Skirmisher,
        Radius        = 10f,
        Health        = 4f,
        Mass          = 1f,
        Movement = () => new() { new EnemyIdleState() },
        Actions  = () => new() { new EnemyRangedAction(spec) },
        Controller = new StationaryAimController { AlertRange = 600f },
    };

    private static Simulation Build(EnemyBlueprint bp, float enemyX) =>
        new(Floor(), new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(new BlueprintEnemy(bp, new Vector2(enemyX, FloorTopY - 11f))));

    // ── 1. Defaults are the old constants ────────────────────────────────────

    [Theory]
    [InlineData(ActionKind.Melee,      0.45f, 0.12f, 0.40f, 1.0f, 30, 25)]
    [InlineData(ActionKind.Contact,    0.00f, 0.10f, 0.85f, 0.4f, 12, 10)]
    [InlineData(ActionKind.Lunge,      0.35f, 0.25f, 0.45f, 0.9f, 30, 24)]
    [InlineData(ActionKind.Slam,       0.20f, 0.16f, 0.40f, 1.6f, 34, 30)]
    [InlineData(ActionKind.Ranged,     0.60f, 0.08f, 0.50f, 1.0f, 28, 22)]
    [InlineData(ActionKind.RailShot,   1.35f, 0.06f, 1.15f, 1.5f, 34, 30)]
    [InlineData(ActionKind.PounceSlam, 0.00f, 0.95f, 0.40f, 0.8f, 34, 30)]
    [InlineData(ActionKind.Lash,       0.55f, 0.14f, 0.45f, 1.3f, 32, 27)]
    public void Default_ReproducesTheStockConstants(ActionKind kind, float windup, float active, float recovery,
                                                    float damage, int activePri, int passivePri)
    {
        var s = ActionSpec.Default(kind);
        Assert.Equal(kind,       s.Kind);
        Assert.Equal(windup,     s.Windup);
        Assert.Equal(active,     s.Active);
        Assert.Equal(recovery,   s.Recovery);
        Assert.Equal(damage,     s.Damage);
        Assert.Equal(activePri,  s.ActivePriority);
        Assert.Equal(passivePri, s.PassivePriority);
    }

    [Fact]
    public void ParameterlessCtor_IsTheDefaultSpec()
    {
        Assert.Equal(ActionKind.Melee,      new EnemyMeleeAction().Spec.Kind);
        Assert.Equal(ActionKind.Contact,    new EnemyContactAction().Spec.Kind);
        Assert.Equal(ActionKind.Lunge,      new EnemyLungeAction().Spec.Kind);
        Assert.Equal(ActionKind.Slam,       new EnemySlamAction().Spec.Kind);
        Assert.Equal(ActionKind.Ranged,     new EnemyRangedAction().Spec.Kind);
        Assert.Equal(ActionKind.RailShot,   new EnemyRailShotAction().Spec.Kind);
        Assert.Equal(ActionKind.PounceSlam, new EnemyPounceSlamAction().Spec.Kind);
        Assert.Equal(ActionKind.Lash,       new EnemyLashAction().Spec.Kind);
        // Bespoke actions carry no spec and say so.
        Assert.Equal(ActionKind.Special,    new TemplateAction().Spec.Kind);
    }

    // ── 2. A different spec is different behaviour ───────────────────────────

    [Fact]
    public void ShorterWindup_LandsTheFirstHitSooner()
    {
        var stock = ActionSpec.Default(ActionKind.Melee);
        var quick = stock; quick.Windup = 0.10f;

        int stockHit = FirstHitFrame(Build(Melee(stock), 62f));
        int quickHit = FirstHitFrame(Build(Melee(quick), 62f));

        Assert.True(stockHit > 0, "stock swing never connected");
        Assert.True(quickHit > 0, "quick swing never connected");
        // 0.35s less windup at 60 fps ≈ 21 frames; allow slack for the approach.
        Assert.True(quickHit < stockHit - 10,
            $"quick windup hit at frame {quickHit}, stock at {stockHit} — the spec's Windup is not being read.");
        output.WriteLine($"first hit: stock @{stockHit}, quick @{quickHit}");
    }

    [Fact]
    public void RangedSpec_SetsProjectileSpeedAndDamage()
    {
        var spec = ActionSpec.Default(ActionKind.Ranged);
        spec.Speed  = 220f;
        spec.Damage = 0.35f;

        var sim = Build(Ranged(spec), 200f);
        Entity ball = null;
        for (int f = 0; f < 120 && ball == null; f++)
        {
            sim.Step(Idle);
            foreach (var e in sim.Entities) if (e.Kind == EntityKind.EnergyBall) ball = e;
        }
        Assert.NotNull(ball);
        Assert.InRange(ball.Body.Velocity.Length(), 219f, 221f);

        // Let it fly into the parked player: the one hit it lands is the spec's damage.
        for (int f = 0; f < 120 && sim.Player.Combat.DamageTaken <= 0f; f++) sim.Step(Idle);
        Assert.InRange(sim.Player.Combat.DamageTaken, 0.34f, 0.36f);
        output.WriteLine($"ball speed {ball.Body.Velocity.Length():F1}, damage dealt {sim.Player.Combat.DamageTaken:F2}");
    }

    // ── 3. Snapshot mid-windup restores the spec's durations ─────────────────

    [Fact]
    public void SnapshotMidWindup_ReplaysWithTheSpecsDurations()
    {
        // A windup twice the stock one. If the restore re-derived durations from
        // anything but the spec, the replay would swing on a different frame and
        // the traces would diverge from there.
        var spec = ActionSpec.Default(ActionKind.Melee);
        spec.Windup = 0.90f;

        var live = Build(Melee(spec), 62f);
        var bot  = (EnemyEntity)live.Entities[0];

        // Step until the swing is in flight, then a few more so the snapshot lands
        // inside the windup rather than on its first frame.
        int f = 0;
        for (; f < 300 && bot.CurrentActionName != nameof(EnemyMeleeAction); f++) live.Step(Idle);
        Assert.True(f < 300, "the melee action never started");
        for (int i = 0; i < 6; i++, f++) live.Step(Idle);
        Assert.Equal(nameof(EnemyMeleeAction), bot.CurrentActionName);

        const int N = 150;
        var snap = live.Snapshot();
        var liveTrace = new List<string>();
        for (int i = 0; i < N; i++) { live.Step(Idle); liveTrace.Add(Probe(live)); }

        live.Restore(snap);
        var replayTrace = new List<string>();
        for (int i = 0; i < N; i++) { live.Step(Idle); replayTrace.Add(Probe(live)); }

        for (int i = 0; i < N; i++)
        {
            if (liveTrace[i] != replayTrace[i])
            {
                output.WriteLine($"Divergence at replay step {i} (sim frame {f + i}):");
                output.WriteLine("LIVE:\n"   + liveTrace[i]);
                output.WriteLine("REPLAY:\n" + replayTrace[i]);
            }
            Assert.Equal(liveTrace[i], replayTrace[i]);
        }
        Assert.True(live.Player.Combat.DamageTaken > 0f, "the swing never landed, so the trace proved nothing");
        output.WriteLine($"Round-trip identical across {N} frames; snapshot taken at frame {f}.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int FirstHitFrame(Simulation sim)
    {
        for (int f = 1; f <= 300; f++)
        {
            sim.Step(Idle);
            if (sim.Player.Combat.DamageTaken > 0f) return f;
        }
        return -1;
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
            if (e is EnemyEntity en) sb.Append($"|{en.CurrentActionName}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");
}
