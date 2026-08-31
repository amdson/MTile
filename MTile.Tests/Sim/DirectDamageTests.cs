using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The direct-damage model, which replaced the escalation model this file used to
// test (it was EscalationTests).
//
// Under escalation, a landed hit cost no HP at all: it raised a monotonic
// DamagePercent, the percent multiplied incoming knockback, and HP only came off
// when the resulting launch slammed the victim into terrain. Now a hit takes HP
// straight off the victim — the rule Entity.OnHit always followed — knockback is a
// flat property of the attack, and crush impact is one more damage source rather
// than the only one.
//
// What these tests pin is the SHAPE of that model, not its tuning numbers: damage
// equals the hitbox's Damage, knockback is independent of how hurt you already are,
// regen can't out-heal a fight, and the stab is the launcher.
public class DirectDamageTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private const float HitDamage = 0.5f;   // a stock slash

    private static Hitbox MakeHit(PhysicsBody victimBody, Vector2 knockback) =>
        new Hitbox(victimBody.Bounds, hitId: 1, damage: HitDamage, knockbackImpulse: knockback,
                   owner: Faction.Player2, source: new EntityId(99), debugColor: Color.White);

    // A hit costs exactly its Damage in HP, every time, and the running tally follows.
    [Fact]
    public void EveryHit_ComesStraightOffHp()
    {
        var p  = new PlayerCharacter(new Vector2(40f, 20f));
        var hb = new Hurtbox(p.Body.Bounds, Faction.Player1, p.Id);
        float full = p.Health;

        for (int i = 1; i <= 5; i++)
        {
            p.OnHit(MakeHit(p.Body, new Vector2(120f, 0f)), hb);
            Assert.Equal(full - i * HitDamage, p.Health, 3);
            Assert.Equal(i * HitDamage, p.Combat.DamageTaken, 3);
        }
        output.WriteLine($"HP {full} → {p.Health} over 5 hits; tally {p.Combat.DamageTaken}.");
    }

    // The property that replaced escalation: an identical attack lands identically on
    // a fresh victim and a nearly-dead one. Knockback is the ATTACK's stat now, not a
    // function of how much punishment the target has already absorbed.
    [Fact]
    public void Knockback_IsIndependentOfDamageAlreadyTaken()
    {
        var knockback = new Vector2(300f, 0f);

        var fresh = new PlayerCharacter(new Vector2(40f, 20f));
        fresh.OnHit(MakeHit(fresh.Body, knockback),
                    new Hurtbox(fresh.Body.Bounds, Faction.Player1, fresh.Id));

        var battered = new PlayerCharacter(new Vector2(40f, 20f));
        battered.Health = 0.5f;                       // one hit from a KO
        battered.Combat.DamageTaken = 4.5f;
        battered.OnHit(MakeHit(battered.Body, knockback),
                       new Hurtbox(battered.Body.Bounds, Faction.Player1, battered.Id));

        output.WriteLine($"fresh dv {fresh.Body.Velocity.X:F1}, battered dv {battered.Body.Velocity.X:F1}");
        Assert.Equal(fresh.Body.Velocity.X, battered.Body.Velocity.X, 3);
    }

    // Regen is a slow out-of-combat trickle behind a delay, not a pool that undoes the
    // fight. Both halves matter: at the old 0.8/s it erased a slash inside a second,
    // which would have handed the damage model straight back its irrelevance.
    [Fact]
    public void Regen_WaitsOutTheDelay_ThenTricklesBack()
    {
        var p  = new PlayerCharacter(new Vector2(40f, 20f));
        var hb = new Hurtbox(p.Body.Bounds, Faction.Player1, p.Id);
        var terrain = FlatGround();
        var bodies  = new List<PhysicsBody> { p.Body };
        var ctrl    = new Controller();
        var hbx = new HitboxWorld(); var hux = new HurtboxWorld();

        void Idle(int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                ctrl.InjectInput(default);
                terrain.TickSprouts(Dt);
                p.Update(ctrl, terrain, hbx, hux, Dt);
                PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            }
        }

        Idle(20);                                     // settle onto the floor first
        p.OnHit(MakeHit(p.Body, Vector2.Zero), hb);
        float hurt = p.Health;

        Idle(60);                                     // 2 s — inside the 3 s delay
        Assert.Equal(hurt, p.Health, 3);

        Idle(90);                                     // 3 s more ⇒ 2 s of actual regen
        output.WriteLine($"hurt at {hurt}, after 5 s idle {p.Health} (max {p.MaxHealth})");
        Assert.True(p.Health > hurt, "HP should trickle back once the delay has passed.");
        Assert.True(p.Health < hurt + HitDamage,
            "…but two seconds of regen must not buy back a whole slash.");
        Assert.True(p.Health <= p.MaxHealth + 1e-4f, "HP must not overshoot MaxHealth.");
    }

    // A KO ends the life: full HP, a cleared tally, and — easy to miss — a cleared
    // regen delay, or the fatal hit would lock the fresh spawn out of regen.
    [Fact]
    public void Respawn_ClearsTheTally_AndRefills()
    {
        var p = new PlayerCharacter(new Vector2(40f, 20f));
        p.OnHit(MakeHit(p.Body, Vector2.Zero), new Hurtbox(p.Body.Bounds, Faction.Player1, p.Id));
        p.Health = 0.1f;

        p.Respawn(new Vector2(40f, 20f));
        Assert.Equal(0f, p.Combat.DamageTaken);
        Assert.Equal(p.MaxHealth, p.Health);
    }

    // ── The stab is the launcher ─────────────────────────────────────────────────
    //
    // The headline of the knockback pass: every slash came down ~40% and the launch
    // floor halved, while StabAction's strike speed was deliberately left alone. The
    // stab is slow, committed and telegraphed by a whole wind-up, and it should be
    // the move that sends someone flying — so this asserts the GAP, not either number.

    private static readonly Vector2 AttackerStart = new(70f, 20f);
    private static readonly Vector2 VictimStart   = new(95f, 20f);

    // Press at f15 and release the same frame ⇒ Click ⇒ GroundSlash1.
    private static InputScript SlashScript()
    {
        var aim = new Vector2(180f, 28f);
        return new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = aim })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = aim })
            .Forever   (new PlayerInput { MouseWorldPosition = aim });
    }

    // Press at f15, hold 8 frames while the cursor swipes outward, release ⇒ Stab.
    private static InputScript StabScript()
    {
        var press   = new Vector2(120f, 28f);
        var release = new Vector2(180f, 28f);
        return new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = press })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = press })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
            .Forever   (new PlayerInput { MouseWorldPosition = release });
    }

    // Peak horizontal speed the victim reaches, and what it cost them in HP.
    private (float peakVx, float damage, string move) RunAttack(InputScript attacker)
    {
        float peak = 0f, damage = 0f;
        string move = "";
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = FlatGround(),
            Frames  = 60,
            Dt      = Dt,
            Gravity = Gravity,
            Players = new[]
            {
                new SimPlayer { StartPosition = AttackerStart, Script = attacker },
                new SimPlayer { StartPosition = VictimStart, Script = InputScript.Always(default),
                                Faction = Faction.Neutral },
            },
        },
        onFrame: (f, ps) =>
        {
            peak = MathF.Max(peak, MathF.Abs(ps[1].Body.Velocity.X));
            if (ps[0].CurrentActionName.Contains("Slash") || ps[0].CurrentActionName.Contains("Stab"))
                move = ps[0].CurrentActionName;
        },
        outPlayers: ps => damage = ps[1].Combat.DamageTaken);
        return (peak, damage, move);
    }

    [Fact]
    public void Stab_LaunchesFarHarderThanASlash()
    {
        var slash = RunAttack(SlashScript());
        var stab  = RunAttack(StabScript());

        output.WriteLine($"{slash.move}: peak vx {slash.peakVx:F0}, {slash.damage} HP");
        output.WriteLine($"{stab.move}: peak vx {stab.peakVx:F0}, {stab.damage} HP");

        Assert.True(slash.damage > 0f, "precondition: the slash should have connected");
        Assert.True(stab.damage  > 0f, "precondition: the stab should have connected");
        Assert.True(stab.peakVx > slash.peakVx * 2.5f,
            $"The stab should be in a different class from a light slash " +
            $"({stab.peakVx:F0} vs {slash.peakVx:F0} px/s).");
    }

    // …and a light slash is not itself a launch. 100 px/s is the MinLaunch floor; the
    // guard is that a poke stays around it rather than at the half-a-run-speed shove
    // (180) it used to hand out for merely connecting.
    [Fact]
    public void ALightSlash_ShovesRatherThanLaunches()
    {
        var slash = RunAttack(SlashScript());
        output.WriteLine($"{slash.move} peak vx {slash.peakVx:F0} px/s");
        Assert.True(slash.damage > 0f, "precondition: the slash should have connected");
        Assert.True(slash.peakVx < 160f,
            $"A light slash should not launch ({slash.peakVx:F0} px/s).");
    }
}
