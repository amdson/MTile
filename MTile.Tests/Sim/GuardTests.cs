using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Guard is a TIMED parry (CombatState.ResolveGuard), not a shield you hold: it absorbs
// a hit outright only in the brief window after the stance comes up, leaks progressively
// more the longer the button is held, and breaks on anything that leaks through.
//
// These tests pin both halves of that: the penetration curve itself, and the render-side
// cue stamps (LastParryFrame / LastParryDir / LastParryCharged) that GameAudio.GuardBlock
// and HitFeelSystem derive the block sound and sparks from. If the stamps stop being
// written, or stop surviving a snapshot restore, the cue silently stops firing (or
// double-fires after a rollback) with nothing else in the suite noticing.
public class GuardTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const int   GuardFrame = 42;

    // Mirrors the private constants in CombatState — see the comments there.
    private const int PerfectWindowFrames = 7;    // FromSeconds(0.12, 1/60)
    private const int FalloffFrames       = 36;   // FromSeconds(0.60, 1/60)
    private const int BreakRecoveryFrames = 30;   // FromSeconds(0.50, 1/60)
    private const int CooldownFrames      = 9;    // FromSeconds(0.15, 1/60)

    // Weak enough to arm GuardRetaliate (GuardChargeMaxDamage is 1.0).
    private const float WeakDamage   = 0.5f;
    private const float StrongDamage = 3.0f;

    private static CombatState Guarding()
    {
        var c = new CombatState();
        c.BeginGuard(GuardFrame);
        return c;
    }

    // Attacker on the right, player facing right: the impulse pushes the player LEFT,
    // so the direction back toward the attacker is +X.
    private static Vector2 ImpulseFromTheRight => new Vector2(-200f, 0f);

    private static GuardOutcome HitAt(CombatState c, int frame, float damage = WeakDamage,
                                      int facing = 1)
        => c.ResolveGuard(ImpulseFromTheRight, damage, facing, frame, Dt);

    // ── The perfect window ──────────────────────────────────────────────────────

    [Fact]
    public void HitInsideTheWindowIsAbsorbedCompletely()
    {
        var c = Guarding();

        var g = HitAt(c, GuardFrame + PerfectWindowFrames);

        Assert.True(g.Absorbed);
        Assert.Equal(0f, g.DamageScale);
        Assert.Equal(0f, g.KnockbackScale);
        Assert.True(c.GuardActive);      // a clean block keeps the stance up
        Assert.False(c.GuardBroken);
    }

    [Fact]
    public void AbsorbedHitStampsFrameAndIncomingDirection()
    {
        var c = Guarding();

        HitAt(c, GuardFrame + 1);

        Assert.Equal(GuardFrame + 1, c.LastParryFrame);
        Assert.True(c.LastParryDir.X > 0.99f);          // unit vector toward the attacker
        Assert.Equal(1f, c.LastParryDir.Length(), 3);
    }

    [Fact]
    public void ChargedFlagTracksWhetherRetaliateWasArmed()
    {
        var weak = Guarding();
        HitAt(weak, GuardFrame + 1, WeakDamage);
        Assert.True(weak.GuardCharged);
        Assert.True(weak.LastParryCharged);

        var strong = Guarding();
        HitAt(strong, GuardFrame + 1, StrongDamage);
        Assert.False(strong.GuardCharged);
        Assert.False(strong.LastParryCharged);   // still a clean block, just no counter
        Assert.Equal(GuardFrame + 1, strong.LastParryFrame);
    }

    // ── Penetration past the window ─────────────────────────────────────────────

    [Fact]
    public void PenetrationRampsWithHowLongGuardWasHeld()
    {
        // One frame past the window leaks essentially nothing...
        var early = Guarding();
        var gEarly = HitAt(early, GuardFrame + PerfectWindowFrames + 1);
        Assert.False(gEarly.Absorbed);
        Assert.InRange(gEarly.DamageScale, 0f, 0.05f);

        // ...halfway down the ramp, about half the saturated leak...
        var mid = Guarding();
        var gMid = HitAt(mid, GuardFrame + PerfectWindowFrames + FalloffFrames / 2);
        Assert.InRange(gMid.DamageScale,    0.30f, 0.45f);
        Assert.InRange(gMid.KnockbackScale, 0.20f, 0.30f);

        // ...and it is monotonic in between.
        Assert.True(gMid.DamageScale > gEarly.DamageScale);
    }

    [Fact]
    public void HoldingGuardIndefinitelySaturates()
    {
        var c = Guarding();

        // Far beyond the falloff — this is the "just holding Shift forever" case.
        var g = HitAt(c, GuardFrame + 600);

        Assert.False(g.Absorbed);
        Assert.Equal(0.75f, g.DamageScale,    3);
        Assert.Equal(0.50f, g.KnockbackScale, 3);
    }

    // A leaked hit is still a hit that landed: it must not stamp the block cue, or the
    // clean-parry clang would play over a hit that actually got through.
    [Fact]
    public void LeakedHitLeavesNoBlockCue()
    {
        var c = Guarding();

        HitAt(c, GuardFrame + 600);

        Assert.Equal(0, c.LastParryFrame);
    }

    // ── Break + recovery ────────────────────────────────────────────────────────

    [Fact]
    public void AnythingThatGetsThroughBreaksTheGuard()
    {
        var c = Guarding();

        HitAt(c, GuardFrame + PerfectWindowFrames + 1);

        Assert.False(c.GuardActive);
        Assert.True(c.GuardBroken);
    }

    // Out of the front cone the guard never met the hit, so it neither filters it nor
    // survives it.
    [Fact]
    public void HitFromBehindIsUnfilteredAndStillBreaksTheGuard()
    {
        var c = Guarding();

        var g = HitAt(c, GuardFrame + 1, WeakDamage, facing: -1);

        Assert.False(g.Absorbed);
        Assert.Equal(1f, g.DamageScale);
        Assert.Equal(1f, g.KnockbackScale);
        Assert.Equal(0, c.LastParryFrame);
        Assert.True(c.GuardBroken);
    }

    [Fact]
    public void UnguardedHitIsUnfiltered()
    {
        var c = new CombatState();   // never guarded

        var g = HitAt(c, GuardFrame);

        Assert.False(g.Absorbed);
        Assert.Equal(1f, g.DamageScale);
        Assert.Equal(1f, g.KnockbackScale);
        Assert.False(c.GuardBroken);
    }

    // The recovery lockout needs BOTH halves. Without the release requirement, a player
    // holding Shift through a break would get a fresh perfect window every recovery
    // period, and "held indefinitely" would never actually saturate.
    [Fact]
    public void BreakRecoveryNeedsBothTheCountdownAndAReleasedButton()
    {
        var c = Guarding();
        int breakFrame = GuardFrame + 600;
        HitAt(c, breakFrame);
        Assert.True(c.GuardBroken);

        // Countdown still running, button still down.
        c.Tick(breakFrame + BreakRecoveryFrames - 1, guardHeld: true);
        Assert.True(c.GuardBroken);

        // Countdown expired, but the button is still held.
        c.Tick(breakFrame + BreakRecoveryFrames, guardHeld: true);
        Assert.True(c.GuardBroken);

        // Released.
        c.Tick(breakFrame + BreakRecoveryFrames, guardHeld: false);
        Assert.False(c.GuardBroken);
    }

    [Fact]
    public void ReleasingEarlyDoesNotSkipTheCountdown()
    {
        var c = Guarding();
        int breakFrame = GuardFrame + 600;
        HitAt(c, breakFrame);

        c.Tick(breakFrame + 1, guardHeld: false);

        Assert.True(c.GuardBroken);
    }

    // Re-guarding after a break starts a fresh window — the stance is a new one.
    [Fact]
    public void ReGuardingAfterRecoveryRestoresThePerfectWindow()
    {
        var c = Guarding();
        int breakFrame = GuardFrame + 600;
        HitAt(c, breakFrame);
        c.Tick(breakFrame + BreakRecoveryFrames, guardHeld: false);

        int reGuardFrame = breakFrame + BreakRecoveryFrames + 5;
        c.BeginGuard(reGuardFrame);

        Assert.True(HitAt(c, reGuardFrame + 1).Absorbed);
    }

    // ── Re-entry cooldown ───────────────────────────────────────────────────────

    // Every deactivation, not just a break, locks re-entry briefly — otherwise the
    // timing model is free to game by mashing Shift for a fresh window per press.
    [Fact]
    public void DeactivatingGuardStartsAReEntryCooldown()
    {
        var c = Guarding();
        int releaseFrame = GuardFrame + 20;

        c.EndGuard(releaseFrame, Dt);

        Assert.True(c.GuardOnCooldown(releaseFrame));
        Assert.True(c.GuardOnCooldown(releaseFrame + CooldownFrames - 1));
        Assert.False(c.GuardOnCooldown(releaseFrame + CooldownFrames));
    }

    // The cooldown is longer than the window it protects, so a mash spends more frames
    // locked out than guarding.
    [Fact]
    public void CooldownOutlastsThePerfectWindow()
    {
        Assert.True(CooldownFrames > PerfectWindowFrames);
    }

    // ── Snapshot ────────────────────────────────────────────────────────────────

    // The whole timing model reads GuardStartFrame, and the cue dedupes on the parry
    // stamp — a field dropped from CopyFrom would make a rolled-back guard mistime its
    // own window or re-fire its block sound on every re-sim.
    [Fact]
    public void GuardStateSurvivesSnapshotRestore()
    {
        var c = Guarding();
        HitAt(c, GuardFrame + 1);          // absorbed: stamps the cue, keeps the stance
        var savedGuarding = c.Clone();

        HitAt(c, GuardFrame + 600);        // leaks + breaks
        var savedBroken = c.Clone();

        c.CopyFrom(savedGuarding);
        Assert.True(c.GuardActive);
        Assert.False(c.GuardBroken);
        Assert.Equal(GuardFrame, c.GuardStartFrame);
        Assert.Equal(GuardFrame + 1, c.LastParryFrame);
        Assert.True(c.LastParryDir.X > 0.99f);
        Assert.True(c.LastParryCharged);
        // Restored mid-guard, the window is still measured from the original entry.
        Assert.True(HitAt(c, GuardFrame + 2).Absorbed);

        c.CopyFrom(savedBroken);
        Assert.True(c.GuardBroken);
        Assert.Equal(GuardFrame + 600 + BreakRecoveryFrames, c.GuardBreakExpireFrame);
    }

    [Fact]
    public void CooldownSurvivesSnapshotRestore()
    {
        var c = Guarding();
        c.EndGuard(GuardFrame + 20, Dt);
        var saved = c.Clone();

        c.GuardCooldownExpireFrame = 0;
        c.CopyFrom(saved);

        Assert.True(c.GuardOnCooldown(GuardFrame + 21));
    }

    // ── The cooldown, wired through the action FSM ──────────────────────────────

    // A pure-CombatState test can't catch GuardAction forgetting to read the cooldown,
    // so this runs the real FSM: one player, no attacker, mashing Shift on a 1-frame
    // release. Guard must sit out the lockout between presses instead of re-entering
    // the moment the button comes back down.
    [Fact]
    public void MashingShiftCannotKeepGuardUp()
    {
        // dt is 1/30 here (SimConfigMulti's default), so the cooldown is 4 frames.
        const float SimDt = 1f / 30f;
        int cooldown = SimFrames.FromSeconds(0.15f, SimDt);

        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        // Hold 6, release 1, hold forever — the tightest mash the input allows.
        var script = new InputScript()
            .For   (5, default(PlayerInput))
            .For   (6, new PlayerInput { Shift = true })
            .For   (1, default(PlayerInput))
            .Forever  (new PlayerInput { Shift = true });

        var guarded = new List<bool>();
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 40,
            Dt      = SimDt,
            Gravity = new Vector2(0f, 600f),
            Players = new[] { new SimPlayer { StartPosition = new Vector2(60f, 20f), Script = script } },
        },
        onFrame: (f, ps) => guarded.Add(ps[0].CurrentActionName == "GuardAction"));

        int first = guarded.IndexOf(true);
        Assert.True(first >= 0, "Holding Shift should raise the guard at all.");

        int lastOfFirstRun = first;
        while (lastOfFirstRun + 1 < guarded.Count && guarded[lastOfFirstRun + 1]) lastOfFirstRun++;
        int back = guarded.FindIndex(lastOfFirstRun + 1, g => g);
        output.WriteLine($"guard {first}..{lastOfFirstRun}, back@{back}, cooldown={cooldown}");

        Assert.True(back > 0, "Guard should come back after the cooldown.");
        Assert.True(back - lastOfFirstRun > cooldown,
            $"Re-press must wait out the cooldown (gap {back - lastOfFirstRun}, cooldown {cooldown}).");
    }
}
