using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Phase 2 gate for Plans/FIGHTER_DESIGN_PLAN.md — the rollback gate. A fighter
// with a STATEFUL brain (all its memory in EnemyEntity.Scratch), an explicit
// action choice, and a DRAINING meter must snapshot → run → restore → re-run to
// bit-identical traces and checksums. If any of the new per-entity state
// (Scratch, Energy, EnergyMax) were missing from EntityData, the replay would
// diverge the first time the brain or the meter consulted it.
//
// The other tests pin the mechanics the gate leans on: RequestedAction narrows
// the scan to one candidate, an unaffordable action never triggers, regen makes
// it affordable again, GroundPower ÷ Mass makes a heavier body slower, and a
// paid-for flight ends when the meter empties.
public class FighterDeterminismTests(ITestOutputHelper output)
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

    private const float FloorTopY = 8 * Chunk.TileSize;   // 128
    private static readonly PlayerInput Idle = default;

    // ── A bundled brain with memory ──────────────────────────────────────────
    // Strafes: flips its walk direction every HalfPeriod seconds using a timer
    // and a mode bit kept in Scratch, and asks for one specific action. The
    // controller instance itself has no mutable fields — that is the rule §5.2
    // states, and the round-trip test is what enforces it.
    private sealed class StrafeBrain : EnemyController
    {
        public float HalfPeriod     { get; init; } = 0.5f;
        public int   RequestedIndex { get; init; } = -1;   // -1 ⇒ leave RequestedAction null

        public override EnemyInput Decide(in EnemyContext ctx)
        {
            ref var s = ref ctx.Self.Scratch;
            s.F0 += ctx.Dt;
            if (s.F0 >= HalfPeriod) { s.F0 -= HalfPeriod; s.I0 ^= 1; s.I1++; }
            float dir = s.I0 == 0 ? 1f : -1f;
            return new EnemyInput
            {
                MoveDir         = new Vector2(dir, 0f),
                AimWorld        = ctx.Player.Body.Position,
                WantAttack      = true,
                RequestedAction = RequestedIndex >= 0 ? RequestedIndex : null,
            };
        }
    }

    private static EnemyBlueprint Gunner(float energyMax, float regen, float shotCost, int? request = 1,
                                         float groundPower = 0f, float mass = 1f) => new()
    {
        Kind          = EntityKind.Skirmisher,
        Radius        = 10f,
        Health        = 4f,
        Mass          = mass,
        FrictionScale = 0.10f,
        EnergyMax     = energyMax,
        EnergyRegen   = regen,
        GroundPower   = groundPower,
        Movement = () => new()
        {
            new EnemyIdleState(),
            new EnemyChaseState(),
            new EnemyAttackHoldState(),
        },
        Actions = () =>
        {
            var melee = ActionSpec.Default(ActionKind.Melee);  melee.EnergyCost = shotCost;
            var shot  = ActionSpec.Default(ActionKind.Ranged); shot.EnergyCost  = shotCost;
            return new() { new EnemyMeleeAction(melee), new EnemyRangedAction(shot) };
        },
        Controller = new StrafeBrain { RequestedIndex = request ?? -1 },
    };

    private static Simulation Build(EnemyBlueprint bp, Vector2 enemyPos, Vector2? playerPos = null) =>
        new(Floor(), playerPos ?? new Vector2(40f, FloorTopY - 12f),
            g => g.SpawnEntity(new BlueprintEnemy(bp, enemyPos)));

    // ── The gate ─────────────────────────────────────────────────────────────

    [Fact]
    public void StatefulBrainAndDrainingMeter_RoundTripBitIdentical()
    {
        // 3 units, 0.6/s regen, 1 per shot: the meter empties inside the first
        // second and then paces the shots, so the trace after the snapshot
        // depends on Energy AND on the brain's Scratch timer.
        var live = Build(Gunner(energyMax: 3f, regen: 0.6f, shotCost: 1f), new Vector2(240f, FloorTopY - 11f));
        var bot  = (EnemyEntity)live.Entities[0];

        const int K = 75, N = 300;
        for (int f = 0; f < K; f++) live.Step(Idle);
        Assert.True(bot.Energy < 3f, "the meter never drained, so the round-trip proves nothing about it");
        Assert.True(bot.Scratch.I1 > 0, "the brain never flipped, so the round-trip proves nothing about Scratch");

        var snap = live.Snapshot();
        var liveTrace = new List<string>();
        var liveSums  = new List<ulong>();
        for (int f = 0; f < N; f++) { live.Step(Idle); liveTrace.Add(Probe(live)); liveSums.Add(live.Checksum()); }

        live.Restore(snap);
        var replayTrace = new List<string>();
        var replaySums  = new List<ulong>();
        for (int f = 0; f < N; f++) { live.Step(Idle); replayTrace.Add(Probe(live)); replaySums.Add(live.Checksum()); }

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
        output.WriteLine($"Identical across {N} frames; energy {bot.Energy:F2}, flips {bot.Scratch.I1}.");
    }

    // ── Mechanics ────────────────────────────────────────────────────────────

    [Fact]
    public void RequestedAction_ConsidersOnlyThatCandidate()
    {
        // Player 26 px away: melee's precondition (dist < 32) passes, ranged's
        // (dist ≥ 90) fails. Asking for index 0 swings; asking for index 1 does
        // nothing at all, even though a swing is available — the request is a
        // restriction, not a preference.
        var here = new Vector2(66f, FloorTopY - 11f);
        var swing = Build(Gunner(0f, 0f, 0f, request: 0), here);
        var wait  = Build(Gunner(0f, 0f, 0f, request: 1), here);
        var any   = Build(Gunner(0f, 0f, 0f, request: null), here);

        Assert.Equal(nameof(EnemyMeleeAction), FirstAction(swing));
        Assert.Equal("",                       FirstAction(wait));
        Assert.Equal(nameof(EnemyMeleeAction), FirstAction(any));
    }

    [Fact]
    public void EnergyGate_UnaffordableActionWaitsForRegen()
    {
        // One unit, one per shot, 0.25/s regen: the second shot cannot come
        // sooner than 4 s (240 frames) after the first.
        var sim = Build(Gunner(energyMax: 1f, regen: 0.25f, shotCost: 1f), new Vector2(240f, FloorTopY - 11f));
        var bot = (EnemyEntity)sim.Entities[0];

        var shotFrames = new List<int>();
        bool wasShooting = false;
        for (int f = 0; f < 600; f++)
        {
            sim.Step(Idle);
            bool shooting = bot.CurrentActionName == nameof(EnemyRangedAction);
            if (shooting && !wasShooting)
            {
                shotFrames.Add(f);
                Assert.InRange(bot.Energy, -1e-4f, 0.02f);   // spent at Enter
            }
            wasShooting = shooting;
        }

        Assert.True(shotFrames.Count >= 2, $"expected at least two shots in 600 frames, got {shotFrames.Count}");
        int gap = shotFrames[1] - shotFrames[0];
        Assert.True(gap >= 235, $"second shot came {gap} frames after the first; regen at 0.25/s cannot afford one under 240.");
        output.WriteLine($"shots at frames {string.Join(", ", shotFrames)}");
    }

    [Fact]
    public void ZeroCostActions_IgnoreAMissingMeter()
    {
        // No meter at all (EnergyMax 0) and a free action: fires as before.
        var sim = Build(Gunner(energyMax: 0f, regen: 0f, shotCost: 0f), new Vector2(240f, FloorTopY - 11f));
        Assert.Equal(nameof(EnemyRangedAction), FirstAction(sim));
    }

    [Fact]
    public void GroundPower_HeavierBodyIsSlowerOffTheLine()
    {
        // Same power, four times the mass. Chase toward a far player for half a
        // second; the light body must be further along.
        var far = new Vector2(600f, FloorTopY - 12f);
        var start = new Vector2(40f, FloorTopY - 11f);
        var light = Build(Chaser(groundPower: 120f, mass: 1f), start, far);
        var heavy = Build(Chaser(groundPower: 120f, mass: 4f), start, far);
        for (int f = 0; f < 30; f++) { light.Step(Idle); heavy.Step(Idle); }

        float dl = light.Entities[0].Body.Position.X - start.X;
        float dh = heavy.Entities[0].Body.Position.X - start.X;
        Assert.True(dl > dh + 4f, $"light moved {dl:F1}, heavy {dh:F1} — mass is not slowing the walk.");

        // ...and the legacy path is untouched: a blueprint with no power walks at
        // the state's fixed speed from frame one.
        var legacy = Build(Chaser(groundPower: 0f, mass: 4f), start, far);
        legacy.Step(Idle);
        Assert.InRange(legacy.Entities[0].Body.Velocity.X, 69f, 71f);
        output.WriteLine($"light {dl:F1}px, heavy {dh:F1}px in 30 frames");
    }

    [Fact]
    public void FlightDrain_EmptiesTheMeterAndDropsTheFlyer()
    {
        // Thrust well above gravity, one unit of energy, two units a second of
        // drain, no regen: half a second of hover, then it falls.
        var bp = new EnemyBlueprint
        {
            Kind        = EntityKind.Bird,
            Radius      = 9f,
            Health      = 2f,
            Mass        = 1f,
            Thrust      = 1400f,
            EnergyMax   = 1f,
            FlightDrain = 2f,
            Movement = () => new() { new EnemyIdleState(), new EnemyFlyState() },
            Actions  = () => new(),
            Controller = new StationaryAimController { AlertRange = 0f },   // hover in place, never attack
        };
        var start = new Vector2(200f, FloorTopY - 80f);
        var sim = Build(bp, start);
        var bot = (EnemyEntity)sim.Entities[0];

        for (int f = 0; f < 20; f++) sim.Step(Idle);
        Assert.InRange(bot.Body.Position.Y, start.Y - 6f, start.Y + 6f);   // holding altitude
        Assert.True(bot.Energy > 0f);

        for (int f = 0; f < 60; f++) sim.Step(Idle);
        Assert.Equal(0f, bot.Energy);
        Assert.True(bot.Body.Position.Y > start.Y + 20f,
            $"meter is empty but the flyer is still at y {bot.Body.Position.Y:F1} (started {start.Y:F1}).");
        output.WriteLine($"fell to y {bot.Body.Position.Y:F1} after the meter emptied");
    }

    [Fact]
    public void Strength_ScalesPublishedDamage_AndArmorResistsShoves()
    {
        var here = new Vector2(66f, FloorTopY - 11f);
        var weak   = Build(Melee(strength: 0.5f), here);
        var strong = Build(Melee(strength: 2.0f), here);
        Assert.InRange(FirstDamage(weak),   0.49f, 0.51f);
        Assert.InRange(FirstDamage(strong), 1.99f, 2.01f);

        // Armor: same hit, wider divisor, smaller shove.
        var soft = new BlueprintEnemy(Melee(strength: 1f), Vector2.Zero);
        var hard = new BlueprintEnemy(Melee(strength: 1f, armor: 3f), Vector2.Zero);
        var hit  = new Hitbox(new BoundingBox(-5, -5, 5, 5), 1, 1f, new Vector2(300f, 0f), Faction.Player1, default, Color.White);
        soft.OnHit(in hit, default);
        hard.OnHit(in hit, default);
        Assert.True(soft.Body.Velocity.X > hard.Body.Velocity.X * 2f,
            $"soft {soft.Body.Velocity.X:F1} vs armored {hard.Body.Velocity.X:F1}");
    }

    // ── blueprints ───────────────────────────────────────────────────────────

    private static EnemyBlueprint Chaser(float groundPower, float mass) => new()
    {
        Kind          = EntityKind.Sparring,
        Radius        = 10f,
        Health        = 4f,
        Mass          = mass,
        FrictionScale = 0.10f,
        GroundPower   = groundPower,
        Movement = () => new() { new EnemyIdleState(), new EnemyChaseState() },
        Actions  = () => new(),
        Controller = new ChasePlayerController { EngageRange = 10f },
    };

    private static EnemyBlueprint Melee(float strength, float armor = 0f) => new()
    {
        Kind          = EntityKind.Sparring,
        Radius        = 10f,
        Health        = 4f,
        Mass          = 1f,
        FrictionScale = 0.10f,
        Strength      = strength,
        Armor         = armor,
        Movement = () => new() { new EnemyIdleState(), new EnemyAttackHoldState() },
        Actions  = () => new() { new EnemyMeleeAction() },
        Controller = new StationaryAimController { AlertRange = 100f },
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string FirstAction(Simulation sim)
    {
        var bot = (EnemyEntity)sim.Entities[0];
        for (int f = 0; f < 240; f++)
        {
            sim.Step(Idle);
            if (bot.CurrentActionName != "") return bot.CurrentActionName;
        }
        return "";
    }

    private static float FirstDamage(Simulation sim)
    {
        for (int f = 0; f < 240; f++)
        {
            sim.Step(Idle);
            if (sim.Player.Combat.DamageTaken > 0f) return sim.Player.Combat.DamageTaken;
        }
        return 0f;
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
            if (e is EnemyEntity en)
                sb.Append($"|{en.CurrentActionName}|e{Bits(en.Energy)}|s{Bits(en.Scratch.F0)},{en.Scratch.I0},{en.Scratch.I1}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Bits(float v) => System.BitConverter.SingleToInt32Bits(v).ToString("X8");
}
