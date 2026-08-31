using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// The hit airlock (Plans/HIT_AIRLOCK_PLAN.md): getting hit is an INVOLUNTARY
// eviction through RecoveryAction, hitstun/stun fold into the recovery index,
// and ArmorProfile is the per-action exception. Three scenarios pin it:
//
//   1. Armor: a light slash landing inside a stab's armored window (windup +
//      strike, threshold 300 vs Slash1 strength ~200) does NOT interrupt —
//      damage still lands, no hitstun registers, the stab completes.
//   2. Flinch: a heavy hit (enemy stab, strength ~650) breaks the armor,
//      registers, and evicts the victim's own stab mid-swing into Recovery.
//   3. Guard break: an out-of-cone hit evicts a live guard AND starts the break
//      recovery (CombatState.GuardBroken), which holding Shift cannot wait out —
//      the stance comes back only after the countdown AND a release of the
//      button, so the victim can't just keep the shield pinned down.
public class HitEvictionTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly Vector2 AttackerStart = new(70f, 20f);
    private static readonly Vector2 VictimStart   = new(95f, 20f);

    // Attacker stab gesture (as in CombatHitstunTests.Stun_StabHit…): press at
    // f15, 8-frame swipe, release ⇒ StabAction enters f24; hitbox opens at
    // 0.18s ≈ f29–30 and lands on the victim standing 25px to the right.
    private static InputScript AttackerStabScript()
    {
        var press   = new Vector2(120f, 28f);
        var release = new Vector2(180f, 28f);
        return new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = press })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = press })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
            .Forever   (new PlayerInput { MouseWorldPosition = release });
    }

    // Victim stab gesture aimed RIGHT (away from the attacker, so the victim's
    // own hitbox never touches the attacker). `idleFrames` positions the entry.
    private static InputScript VictimStabScript(int idleFrames)
    {
        var press   = new Vector2(145f, 28f);
        var release = new Vector2(205f, 28f);
        return new InputScript()
            .For   (idleFrames, new PlayerInput { MouseWorldPosition = press })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = press })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
            .Forever   (new PlayerInput { MouseWorldPosition = release });
    }

    private (List<string> actions, List<bool> hitstun, float damage)
        Run(InputScript attacker, InputScript victim, int frames)
    {
        var actions = new List<string>(frames);
        var hitstun = new List<bool>(frames);
        float damage = 0f;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = FlatGround(),
            Frames  = frames,
            Dt      = Dt,
            Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = AttackerStart, Script = attacker },
                new SimPlayer { StartPosition = VictimStart,   Script = victim,
                                Faction = Faction.Neutral },
            },
        },
        onFrame: (f, ps) =>
        {
            actions.Add(ps[1].CurrentActionName);
            hitstun.Add(ps[1].Combat.HitstunActive);
        },
        outPlayers: ps => damage = ps[1].Combat.DamageTaken);
        return (actions, hitstun, damage);
    }

    private static int First(List<string> a, string name, int from = 0)
    {
        for (int f = from; f < a.Count; f++) if (a[f] == name) return f;
        return -1;
    }

    private static int Count(List<string> a, string name)
    {
        int n = 0;
        foreach (var s in a) if (s == name) n++;
        return n;
    }

    // ── 1. Armor: a light slash cannot stuff a committed stab ────────────────
    [Fact]
    public void ArmoredStab_TanksLightSlash_NoFlinch()
    {
        // Attacker: quick click ⇒ GroundSlash1 enters f16, hitbox live f16–17
        // (Impulse mode, strength = KnockbackMagnitude 200 < armor 300).
        var mouseAhead = new Vector2(200f, 28f);
        var attacker = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = mouseAhead })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = mouseAhead })
            .Forever   (new PlayerInput { MouseWorldPosition = mouseAhead });

        // Victim: stab enters f15 — the attacker's slash lands 1–2 frames into
        // the victim's armored wind-up.
        var (actions, hitstun, damage) = Run(attacker, VictimStabScript(6), frames: 60);

        int stabFirst  = First(actions, "StabAction");
        int stabFrames = Count(actions, "StabAction");
        output.WriteLine($"victim stab@{stabFirst} for {stabFrames}f, damage={damage}");

        Assert.True(stabFirst >= 0, "Victim's stab gesture should fire StabAction.");
        Assert.True(damage > 0f,
            "The attacker's slash should still CONNECT (armor eats the knockback, not the damage).");
        Assert.DoesNotContain(true, hitstun);   // armored ⇒ never registers ⇒ no hitstun
        // Not interrupted: the stab runs its full 18-frame activation.
        Assert.True(stabFrames >= 16,
            $"Armored stab should complete, but only stayed current {stabFrames} frames.");
    }

    // ── 2. Flinch: a heavy hit breaks the armor and evicts mid-swing ─────────
    [Fact]
    public void HeavyHit_EvictsVictimsStab_MidSwing()
    {
        // Attacker stab hits ≈ f30 (strength ≈ StrikeSpeed 650 > armor 300).
        // Victim's own stab enters f26 — evicted ~4 frames in, well short of the
        // 18-frame activation, and lands in Recovery for the hitstun window.
        var (actions, hitstun, _) = Run(AttackerStabScript(), VictimStabScript(17), frames: 90);

        int stabFirst  = First(actions, "StabAction");
        int stabFrames = Count(actions, "StabAction");
        int hitFrame   = hitstun.IndexOf(true);
        output.WriteLine($"victim stab@{stabFirst} for {stabFrames}f, hitstun@{hitFrame}");

        Assert.True(stabFirst >= 0, "Victim's stab gesture should fire StabAction.");
        Assert.True(hitFrame > 0, "The attacker's stab should land and register hitstun.");
        Assert.True(stabFrames >= 2 && stabFrames <= 12,
            $"Victim's stab should be flinch-evicted mid-swing (current {stabFrames} frames).");
        int recoveryFirst = First(actions, "RecoveryAction", stabFirst);
        Assert.True(recoveryFirst >= 0 && recoveryFirst <= hitFrame + 2,
            $"Eviction must land the victim in RecoveryAction right after the hit (recovery@{recoveryFirst}, hit@{hitFrame}).");
    }

    // ── 3. Guard break + the release-gated recovery ──────────────────────────

    // Victim holds Shift the whole run: guard is live well before the hit. The
    // attacker strikes from the LEFT while the victim faces RIGHT, so the hit is out
    // of the parry cone, registers fully, and breaks the guard.
    private static InputScript HeldShiftVictim()
        => new InputScript()
            .For   (5, default(PlayerInput))
            .Forever  (new PlayerInput { Shift = true });

    [Fact]
    public void GuardBrokenByHit_StaysDownWhileShiftIsHeld()
    {
        var (actions, hitstun, _) = Run(AttackerStabScript(), HeldShiftVictim(), frames: 90);

        int guardFirst = First(actions, "GuardAction");
        int hitFrame   = hitstun.IndexOf(true);
        output.WriteLine($"guard@{guardFirst}, hit@{hitFrame}");

        Assert.True(guardFirst >= 0 && guardFirst < hitFrame,
            "Guard should be live before the hit lands.");
        Assert.True(hitFrame > 0, "The stab should land (out-of-cone, no parry).");

        // Broken: the frames right after the hit are the airlock, not guard.
        int recoveryFirst = First(actions, "RecoveryAction", hitFrame);
        Assert.True(recoveryFirst >= 0 && recoveryFirst <= hitFrame + 2,
            $"Victim should sit in RecoveryAction after the guard break (recovery@{recoveryFirst}).");

        // …and it stays broken for the rest of the run. Holding Shift is exactly what
        // must NOT recover it: the recovery gate wants the button released, or a player
        // could pin Shift down and be handed a fresh perfect-block window every time
        // the countdown lapsed.
        int guardBack = First(actions, "GuardAction", hitFrame + 1);
        output.WriteLine($"guard re-entry@{guardBack} (want none)");
        Assert.True(guardBack < 0,
            $"Held Shift must not recover a broken guard (came back @{guardBack}).");
    }

    // ── The timing model, end to end through the action FSM ──────────────────
    //
    // The victim taps Left first so it FACES the attacker (facing follows horizontal
    // intent while grounded, and guard refuses to activate with L/R held) — so unlike
    // the break tests above, these hits arrive inside the parry cone. `guardAt` is when
    // Shift goes down; the stab lands ≈ f29.
    private static InputScript FacingVictimGuardingAt(int guardAt)
        => new InputScript()
            .For   (3, new PlayerInput { Left = true })
            .For   (guardAt - 3, default(PlayerInput))
            .Forever  (new PlayerInput { Shift = true });

    [Fact]
    public void GuardRaisedJustInTime_AbsorbsCompletely()
    {
        var (actions, hitstun, damage) = Run(AttackerStabScript(),
                                              FacingVictimGuardingAt(27), frames: 60);

        int guardFirst = First(actions, "GuardAction");
        output.WriteLine($"guard@{guardFirst}, damage={damage}, hitstun={hitstun.IndexOf(true)}");

        Assert.True(guardFirst >= 0, "Shift should raise the guard before the stab lands.");
        Assert.Equal(0f, damage);                     // nothing got through
        Assert.DoesNotContain(true, hitstun);          // and it never registered
    }

    // The clean block spends the stance but refunds the cooldown, so with Shift still
    // held guard is back almost immediately — ready for the next hit of a flurry. This
    // is the FSM half of the contract: a pure CombatState test can't catch GuardAction
    // failing to drop out on the block, or failing to come back after it.
    [Fact]
    public void CleanBlock_DropsGuardThenRearmsImmediately()
    {
        var (actions, _, damage) = Run(AttackerStabScript(),
                                        FacingVictimGuardingAt(27), frames: 60);

        int guardFirst = First(actions, "GuardAction");
        // The block ends that run of guard frames.
        int lastOfRun = guardFirst;
        while (lastOfRun + 1 < actions.Count && actions[lastOfRun + 1] == "GuardAction") lastOfRun++;
        int back = First(actions, "GuardAction", lastOfRun + 1);
        output.WriteLine($"guard {guardFirst}..{lastOfRun}, back@{back}, damage={damage}");

        Assert.Equal(0f, damage);                     // it really was a clean block
        Assert.True(back > 0, "Guard should come back up after a clean block.");
        // The 0.15s cooldown is 4 frames at this dt; a refunded block skips it.
        Assert.True(back - lastOfRun <= 2,
            $"A clean block must refund the cooldown (gap {back - lastOfRun} frames).");
    }

    // The point of the rework: parking on Shift is no longer invulnerability. The same
    // hit that a fresh guard eats completely leaks most of its damage through a guard
    // that has been held since the start of the run.
    [Fact]
    public void GuardHeldSinceLongBefore_LeaksTheHitThrough()
    {
        var (actions, hitstun, damage) = Run(AttackerStabScript(),
                                              FacingVictimGuardingAt(4), frames: 60);

        int guardFirst = First(actions, "GuardAction");
        int hitFrame   = hitstun.IndexOf(true);
        output.WriteLine($"guard@{guardFirst}, damage={damage}, hitstun@{hitFrame}");

        Assert.True(guardFirst >= 0 && guardFirst < 20, "Guard should be live long before the hit.");
        Assert.True(damage > 0f, "A stale guard must let damage through.");
        Assert.True(hitFrame > 0, "…and the leaked hit still registers.");
        // Broken by the hit it failed to stop, and held Shift can't bring it back.
        int guardBack = First(actions, "GuardAction", hitFrame + 1);
        Assert.True(guardBack < 0, $"Leaked hit should break the guard (came back @{guardBack}).");
    }

    // Release the button after the break and guard comes back — the countdown is a real
    // recovery window, not a permanent disable.
    [Fact]
    public void GuardReturnsAfterReleasingTheButton()
    {
        // Hit lands ≈ f30 (see AttackerStabScript). Hold through it, release across the
        // 0.5s (15-frame at this dt) recovery, then press again.
        var victim = new InputScript()
            .For   ( 5, default(PlayerInput))
            .For   (35, new PlayerInput { Shift = true })
            .For   (15, default(PlayerInput))
            .Forever  (new PlayerInput { Shift = true });
        var (actions, hitstun, _) = Run(AttackerStabScript(), victim, frames: 110);

        int hitFrame  = hitstun.IndexOf(true);
        int guardBack = First(actions, "GuardAction", hitFrame + 1);
        output.WriteLine($"hit@{hitFrame}, guard re-entry@{guardBack}");

        Assert.True(hitFrame > 0, "The stab should land (out-of-cone, no parry).");
        // Nothing while Shift is still held after the break (f30-39)...
        for (int f = hitFrame + 1; f < 40 && f < actions.Count; f++)
            Assert.True(actions[f] != "GuardAction",
                $"frame {f}: a broken guard must stay down while Shift is held.");
        // ...and back on the first frame of the re-press, the release having cleared it.
        Assert.True(guardBack >= 55,
            $"Guard should come back only after the release + re-press (@{guardBack}).");
    }
}
